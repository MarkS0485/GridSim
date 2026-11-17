using System.Numerics;
using GridSim.Core.Analysis.MotorStart;
using GridSim.Core.Model;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8: shared fixtures, requests and an INDEPENDENT motor reference for the motor acceleration tests. The reference
/// uses System.Numerics.Complex arithmetic written from the textbook single-cage circuit (not the incumbent
/// InductionMachines code), and closed-form network voltages, so it shares no code with the study.
/// </summary>
internal static class MotorStartTestSupport
{
    internal static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "motor-start", name);

    internal static string Schemas => Path.Combine(CliProcess.Root(), "schemas", "v2");

    internal static string DataRoot => Path.Combine(CliProcess.Root(), "data");

    internal static MotorStartCaseInput Load(string manifest, bool dispatch = false) => MotorStartCase.LoadV2(Fixture(manifest), Schemas, DataRoot, dispatch);

    internal static readonly MotorStartPreStartTolerance PreStart = new(1e-3, 1e-6, 2.0);

    internal static MotorStartRequest Request(string motor = "M1", double step = 0.001, double start = 0.1, double horizon = 10.0,
        double speed = 0.0, double dwell = 0.5, double stallMargin = 0.0, int sampleEvery = 1, double? dipLimit = null) =>
        new(motor, new MotorStartInitialState(speed, false), start, MotorStartMethod.DirectOnLine, step,
            new MotorStartTermination(0.98, horizon, dwell, stallMargin), PreStart, sampleEvery, dipLimit);

    // ---- The independent reference ----

    /// <summary>Input impedance and parallel (air-gap) part of the single-cage circuit at slip s, pu on the motor base.</summary>
    internal static (Complex Zin, Complex Zp) Circuit(PlacedMotor m, double s)
    {
        var jxm = new Complex(0, m.XmPu);
        Complex zp = s == 0 ? jxm : jxm * new Complex(m.R1Pu / s, m.X1Pu) / (jxm + new Complex(m.R1Pu / s, m.X1Pu));
        return (new Complex(m.RaPu, m.XaPu) + zp, zp);
    }

    /// <summary>Electrical torque (air-gap power, pu) at terminal voltage v (pu of rated) and slip s: |I|^2 Re(Zp).</summary>
    internal static double Torque(PlacedMotor m, double v, double s)
    {
        (Complex z, Complex zp) = Circuit(m, s);
        double i = v / z.Magnitude;
        return i * i * zp.Real;
    }

    /// <summary>Terminal current (pu of rated) at terminal voltage v and slip s.</summary>
    internal static double Current(PlacedMotor m, double v, double s) => v / Circuit(m, s).Zin.Magnitude;

    /// <summary>Motor-bus voltage of a two-bus system: a source of <paramref name="vs"/> pu behind the feeder
    /// <paramref name="zf"/> (pu on <paramref name="baseMva"/>), the motor the only load (bus and motor kV equal).</summary>
    internal static double Divider(PlacedMotor m, double s, Complex zf, double vs = 1.0, double baseMva = 100.0)
    {
        Complex zm = Circuit(m, s).Zin * (baseMva / m.RatedMva);
        return vs * (zm / (zf + zm)).Magnitude;
    }

    /// <summary>Acceleration time from <paramref name="w0"/> to <paramref name="wr"/> by quadrature of
    /// t = integral 2H / (Te(w) - Tl(w)) dw (composite midpoint, <paramref name="n"/> panels), with the terminal voltage a
    /// function of slip. Independent of any time integration.</summary>
    internal static double AccelerationTime(PlacedMotor m, Func<double, double> voltageAtSlip, double w0 = 0.0, double wr = 0.98, int n = 400_000)
    {
        double h = (wr - w0) / n, t = 0;
        for (int k = 0; k < n; k++)
        {
            double w = w0 + (k + 0.5) * h, s = 1 - w;
            double ta = Torque(m, voltageAtSlip(s), s) - m.LoadTorque.At(w);
            if (ta <= 0) return double.PositiveInfinity;
            t += 2 * m.InertiaH / ta * h;
        }
        return t;
    }
}
