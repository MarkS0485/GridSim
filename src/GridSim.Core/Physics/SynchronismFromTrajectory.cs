using GridSim.Core.Dynamics.Distributed;

namespace GridSim.Core.Physics;

/// <summary>
/// N246 (October 2026; Ruling R1, Mark delegated EE calls 2026-10-04): builds the law 9 input
/// (<see cref="SynchronismSample"/>) from a dynamics run's OWN trajectory, so a fault study can score synchronism on
/// what it actually integrated.
///
/// <para><b>The criterion.</b> Loss of synchronism is any machine's rotor angle relative to the inertia-weighted centre
/// of inertia (COI) exceeding 180 degrees at any sample of the run: the out-of-step criterion PSS/E and PowerFactory
/// apply to a transient-stability run (an angle past 180 degrees from the COI means the machine has passed the unstable
/// equilibrium of its swing). No frequency band is applied (a fault run's speed deviation is reported, not scored), so
/// the sample is <see cref="SynchronismSample.AngleOnly"/>.</para>
///
/// <para><b>Relation to the run's own verdict.</b> <see cref="ClassicalMultiMachine.Simulate"/> declares a loss only
/// past its pole-slip limit (default 2 pi, one full slip) from the same COI. Anything past 2 pi is past 180 degrees, so
/// when the run says UNSTABLE law 9 is always Violated; between 180 and 360 degrees the law is Violated while the run's
/// verdict still says it held. The CLI prints both and says which is which; neither replaces the other.</para>
///
/// <para><b>Arithmetic.</b> The COI is formed exactly as the integrator forms it (sum of H_i delta_i over sum of H_i, in
/// machine order, at every stored sample), and the largest |delta_i - delta_COI| is taken over every sample and machine;
/// radians to degrees is one multiplication by 180 / pi. No transcendental function is evaluated, so the figure is the
/// same bits on every CPU and OS. A non-finite angle or speed anywhere gives a non-finite maximum, which the law scores
/// as a loss.</para>
/// </summary>
public static class SynchronismFromTrajectory
{
    /// <summary>The out-of-step angle from the centre of inertia, degrees.</summary>
    public const double OutOfStepLimitDeg = 180.0;

    /// <summary>
    /// The law 9 sample of a classical multi-machine run: the largest |delta_i - delta_COI| over the run (degrees) and the
    /// largest |speed deviation| (per unit times <paramref name="nominalHz"/>, in Hz), angle-only, limit 180 degrees.
    /// <paramref name="h"/> is the inertia vector the run was integrated with (one per machine, in the run's order).
    /// </summary>
    /// <exception cref="ArgumentException">The inertia vector does not match the run, a trace is empty or ragged, the
    /// inertia sum is not positive, or the nominal frequency is not positive and finite.</exception>
    public static SynchronismSample Classical(ClassicalMultiMachine.Result run, IReadOnlyList<double> h, double nominalHz)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(h);
        int n = run.Machines.Count;
        if (n == 0) throw new ArgumentException("the run has no machines; there is no synchronism to score.", nameof(run));
        if (h.Count != n) throw new ArgumentException($"{h.Count} inertia value(s) for a run of {n} machine(s).", nameof(h));
        if (!(nominalHz > 0) || !double.IsFinite(nominalHz)) throw new ArgumentException($"nominal frequency {nominalHz} Hz is not positive and finite.", nameof(nominalHz));
        return Score(
            run.Machines.Select(m => m.DeltaRad).ToArray(),
            run.Machines.Select(m => m.SpeedPu).ToArray(),
            h, n, nominalHz, nameof(run));
    }

    /// <summary>
    /// N246 rest (CF2, October 2026): the law 9 sample of an RMS (subtransient, GENROU) run, the trajectory of
    /// <c>stability --rms --clear</c>. Same criterion and arithmetic as <see cref="Classical"/>; the one difference is the
    /// machine set, which is the set the RMS integrator itself judges: the first <paramref name="synchronismMachines"/>
    /// traces (island reference machines, appended after them, are left out of both the centre of inertia and the
    /// maximum, exactly as <see cref="MultiMachineSubtransient"/> leaves them out of its pole-slip test).
    /// <paramref name="h"/> is the system-base inertia vector the run was integrated with (one per trace).
    /// </summary>
    /// <exception cref="ArgumentException">As <see cref="Classical"/>, and a synchronism count outside 1..traces.</exception>
    public static SynchronismSample Subtransient(
        GridSim.Core.Dynamics.Models.MultiMachineSubtransient.Result run, IReadOnlyList<double> h,
        int synchronismMachines, double nominalHz)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(h);
        int traces = run.Machines.Count;
        if (traces == 0) throw new ArgumentException("the run has no machines; there is no synchronism to score.", nameof(run));
        if (h.Count != traces) throw new ArgumentException($"{h.Count} inertia value(s) for a run of {traces} machine(s).", nameof(h));
        if (synchronismMachines < 1 || synchronismMachines > traces)
            throw new ArgumentException($"synchronism set of {synchronismMachines} machine(s) for a run of {traces}; it must be 1..{traces}.", nameof(synchronismMachines));
        if (!(nominalHz > 0) || !double.IsFinite(nominalHz)) throw new ArgumentException($"nominal frequency {nominalHz} Hz is not positive and finite.", nameof(nominalHz));
        return Score(
            run.Machines.Select(m => m.DeltaRad).ToArray(),
            run.Machines.Select(m => m.SpeedPu).ToArray(),
            h, synchronismMachines, nominalHz, nameof(run));
    }

    /// <summary>The shared arithmetic of <see cref="Classical"/> and <see cref="Subtransient"/> over the first
    /// <paramref name="n"/> traces (the loop is the one Classical always had, moved here unchanged, so its bits are
    /// unchanged).</summary>
    private static SynchronismSample Score(
        IReadOnlyList<double>[] delta, IReadOnlyList<double>[] speed, IReadOnlyList<double> h, int n, double nominalHz,
        string runParam)
    {
        int samples = delta[0].Count;
        if (samples == 0) throw new ArgumentException("the run stored no samples.", runParam);
        for (int i = 0; i < n; i++)
            if (delta[i].Count != samples || speed[i].Count != samples)
                throw new ArgumentException($"machine {i}'s trace has a different number of samples from machine 0's.", runParam);

        // The same COI sum as the integrator: in machine order, H_i then H_i * delta_i.
        double hSum = 0.0;
        for (int i = 0; i < n; i++) hSum += h[i];
        if (!(hSum > 0)) throw new ArgumentException($"the inertia sum is {hSum}; the centre of inertia needs a positive total.", nameof(h));

        double maxDev = 0.0, maxSpeed = 0.0;
        bool finite = true;
        for (int k = 0; k < samples; k++)
        {
            double hDelta = 0.0;
            for (int i = 0; i < n; i++) hDelta += h[i] * delta[i][k];
            double coi = hDelta / hSum;
            for (int i = 0; i < n; i++)
            {
                double d = delta[i][k], w = speed[i][k];
                if (!double.IsFinite(d) || !double.IsFinite(w)) { finite = false; continue; }
                double dev = Math.Abs(d - coi);
                if (dev > maxDev) maxDev = dev;
                double sp = Math.Abs(w);
                if (sp > maxSpeed) maxSpeed = sp;
            }
        }
        double maxDeg = finite ? maxDev * (180.0 / Math.PI) : double.PositiveInfinity;
        double maxHz = finite ? maxSpeed * nominalHz : double.PositiveInfinity;
        return new SynchronismSample(maxDeg, maxHz, PoleSlipLimitDeg: OutOfStepLimitDeg, AngleOnly: true);
    }
}
