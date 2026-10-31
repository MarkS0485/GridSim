using System;

namespace GridSim.Core.Storage;

/// <summary>The outcome of one pack step. Powers in watts, DC side (discharge positive).</summary>
/// <param name="DcPowerW">Power actually delivered at the pack terminals (may be below the request when limited).</param>
/// <param name="CurrentA">Pack current (A).</param>
/// <param name="TerminalVoltageV">Pack terminal voltage (V).</param>
/// <param name="HeatW">Heat generated in the cells over the step (W, mean).</param>
/// <param name="AuxiliaryPowerW">Balance-of-plant draw (HVAC, pumps, standby) (W).</param>
/// <param name="Limited">True when the request could not be met in full.</param>
/// <param name="LimitReason">Which limit bound, when <see cref="Limited"/>.</param>
public sealed record PackStepResult(
    double DcPowerW, double CurrentA, double TerminalVoltageV, double HeatW, double AuxiliaryPowerW,
    bool Limited, string? LimitReason);

/// <summary>
/// A battery pack: <see cref="SeriesCells"/> x <see cref="ParallelStrings"/> identical cells of one chemistry
/// (lumped - a representative cell carries the state), with the battery-management-system limits, a lumped
/// thermal model and the balance-of-plant load. It answers the questions the grid side asks of a battery:
/// deliver this power now (<see cref="Step"/>), and how much power can you sustain for the next <c>t</c>
/// seconds (<see cref="StateOfPower"/>) - the latter by simulating the horizon, so voltage sag, rate
/// capability, the SoC window and temperature all bind exactly where the chemistry says they do.
/// </summary>
public sealed class PackModel
{
    // ---- Named constants (October 2026 review, WS13 R198). Values unchanged.

    /// <summary>Guard (dimensionless or efficiency) against a zero divisor: the derate temperature span and the
    /// coulombic efficiency are floored at this.</summary>
    public const double DivisorGuard = 1e-9;

    /// <summary>A SoC within this of its window edge counts as AT the edge when naming why a step was limited.</summary>
    public const double SocEdgeProximity = 1e-3;

    /// <summary>Two per-cell currents (A) that agree to within this are the same limit when naming why a step was
    /// limited. ABSOLUTE: the review (R198) notes a relative test would be more robust for large currents; that
    /// changes only reason strings, but it is left for Mark with the other tolerance-policy questions.</summary>
    public const double LimitMatchToleranceA = 1e-9;

    /// <summary>A step is reported as LIMITED when the delivered DC power differs from the request by more than
    /// this fraction of max(1 W, |request|).</summary>
    public const double LimitedRelativeTolerance = 1e-6;

    /// <summary>State-of-power bisection upper bound: <c>max(rated power, MaxSearchCRate x nominal energy)</c>,
    /// i.e. a 20C pulse, far beyond any chemistry's sustainable rate.</summary>
    public const double MaxSearchCRate = 20.0;

    /// <summary>State-of-power probe (W): a pack that cannot sustain even this has zero state of power.</summary>
    public const double ProbePowerW = 1.0;

    /// <summary>State-of-power bisection iteration cap.</summary>
    public const int MaxBisectionIterations = 50;

    /// <summary>State-of-power bisection stops when the bracket is narrower than this fraction of max(1 W, hi).</summary>
    public const double BisectionRelativeTolerance = 1e-4;

    public ChemistryParameters Chemistry { get; }
    public int SeriesCells { get; }
    public int ParallelStrings { get; }
    public CellModel Cell { get; private set; }
    public double AmbientC { get; set; }
    /// <summary>Rated (converter-side) power (W) - scales the auxiliary loads.</summary>
    public double RatedPowerW { get; }
    public AgeingModel? Ageing { get; private set; }

    public PackModel(ChemistryParameters chemistry, int seriesCells, int parallelStrings, double initialSoc,
        double ambientC = 25.0, double? ratedPowerW = null, bool trackAgeing = true)
    {
        Chemistry = chemistry ?? throw new ArgumentNullException(nameof(chemistry));
        if (seriesCells <= 0 || parallelStrings <= 0) throw new ArgumentOutOfRangeException(nameof(seriesCells));
        // The cell count (series x parallel) is worked out in int arithmetic here (CellCount) and in the dispatch
        // envelope (StorageEnvelope.FromPack); a product above int.MaxValue would wrap silently and every per-cell
        // current would come out wrong. No real pack is anywhere near this (2^31 cells is about 100 TWh), so a pack
        // that large is refused loudly rather than modelled with a wrapped count (CodeQL cs/loss-of-precision).
        if ((long)seriesCells * parallelStrings > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(parallelStrings), parallelStrings,
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"A pack of {seriesCells} series cells x {parallelStrings} parallel strings has more than {int.MaxValue} cells; the cell count must fit in a 32-bit integer."));
        SeriesCells = seriesCells; ParallelStrings = parallelStrings;
        Cell = new CellModel(chemistry, initialSoc, ambientC);
        AmbientC = ambientC;
        RatedPowerW = ratedPowerW ?? NominalEnergyWh * chemistry.MaxDischargeCRate;
        Ageing = trackAgeing ? new AgeingModel(chemistry.Ageing) : null;
    }

    /// <summary>GR-136: the largest relative difference between the requested energy and the nominal energy a
    /// <see cref="Sized"/> pack achieves (0.5: the built pack is within half and one and a half times the request).</summary>
    public const double MaxSizingRelativeError = 0.5;

    /// <summary>Size a pack for a target nominal energy (Wh) and DC voltage (V): series count from the
    /// voltage, parallel strings from the energy.
    /// <para>GR-136 (October 2026): whole strings cannot match every request, and the pack used to be built whatever
    /// the rounding did: a 10 kWh request on a 1000 V NMC string (about 64 kWh a string) became a 64 kWh pack, six
    /// times the request. The achieved nominal energy is now checked and a pack more than
    /// <see cref="MaxSizingRelativeError"/> away from the request is refused, saying what one string holds, so the
    /// caller can pick a lower DC voltage (a shorter string) or a matching energy. The energy and voltage must be
    /// finite numbers above 0. Every pack within the tolerance is built exactly as before.</para></summary>
    public static PackModel Sized(ChemistryParameters chemistry, double energyWh, double dcVoltage, double initialSoc,
        double ambientC = 25.0, double? ratedPowerW = null)
    {
        ArgumentNullException.ThrowIfNull(chemistry);
        if (!(energyWh > 0.0) || !double.IsFinite(energyWh))
            throw new ArgumentOutOfRangeException(nameof(energyWh), energyWh, "The pack energy must be a finite number of Wh above 0.");
        if (!(dcVoltage > 0.0) || !double.IsFinite(dcVoltage))
            throw new ArgumentOutOfRangeException(nameof(dcVoltage), dcVoltage, "The pack DC voltage must be a finite number of V above 0.");
        int ns = Math.Max(1, (int)Math.Round(dcVoltage / chemistry.NominalVoltage));
        double stringWh = ns * chemistry.NominalEnergyWh;
        int np = Math.Max(1, (int)Math.Round(energyWh / stringWh));
        double achievedWh = np * stringWh;
        if (Math.Abs(achievedWh - energyWh) > MaxSizingRelativeError * energyWh)
            throw new ArgumentOutOfRangeException(nameof(energyWh), energyWh,
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"A {energyWh} Wh pack cannot be built from whole strings of {ns} cell(s) at {dcVoltage} V ({stringWh} Wh a string): ")
                + string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{np} string(s) hold {achievedWh} Wh, more than {MaxSizingRelativeError * 100} % away from the request. ")
                + "Use a lower DC voltage (a shorter string) or an energy nearer a whole number of strings.");
        return new PackModel(chemistry, ns, np, initialSoc, ambientC, ratedPowerW);
    }

    private int CellCount => SeriesCells * ParallelStrings;
    public double Soc => Cell.Soc;
    public double TemperatureC => Cell.TemperatureC;
    public double NominalEnergyWh => Chemistry.NominalEnergyWh * CellCount;
    /// <summary>Energy stored above the SoC floor (Wh), at the present capacity - the headroom a dispatch sees.</summary>
    public double AvailableEnergyWh => Math.Max(0, Soc - Chemistry.SocMin) * Cell.CapacityAh * ParallelStrings
        * SeriesCells * Chemistry.NominalVoltage;
    public double OpenCircuitVoltage => Cell.Ocv * SeriesCells;

    /// <summary>Deliver <paramref name="requestedDcPowerW"/> (discharge positive) for <paramref name="dt"/> seconds,
    /// within the BMS limits. The cell current solves <c>N (E I - R0 I^2) = P</c> at the start-of-step state.</summary>
    public PackStepResult Step(double requestedDcPowerW, double dt)
    {
        ChemistryParameters c = Chemistry;
        double e = Cell.InternalEmf, r = Cell.R0;
        double p = requestedDcPowerW / CellCount;           // per-cell power

        // Unconstrained current for the requested power (smaller root). Beyond E^2/(4R) no current delivers it.
        string? reason = null;
        double disc = e * e - 4.0 * r * p;
        double i;
        if (disc < 0) { i = e / (2.0 * r); reason = "maximum power transfer"; }
        else i = (e - Math.Sqrt(disc)) / (2.0 * r);

        // BMS current limits, derated with temperature.
        double tc = Cell.TemperatureC;
        double derate = Math.Clamp((c.TMaxC - tc) / Math.Max(DivisorGuard, c.TMaxC - c.TDerateC), 0.0, 1.0);
        (double iMaxDis, double iMaxChg) = CurrentLimits();

        // Never carry the SoC across its window within this step: the current that would land exactly on the
        // edge caps the step (the BMS tapering into the limit). Discharge uses the Peukert-effective current.
        double chargeRoom = (c.SocMax - Cell.Soc) * 3600.0 * Cell.CapacityAh / dt / Math.Max(c.CoulombicEfficiency, DivisorGuard);
        double dischargeRoom = (Cell.Soc - c.SocMin) * 3600.0 * Cell.CapacityAh / dt;
        if (c.PeukertExponent > 1.0 && dischargeRoom > c.PeukertReferenceCRate * Cell.CapacityAh)
            dischargeRoom = PeukertInverse(dischargeRoom);
        iMaxChg = Math.Min(iMaxChg, Math.Max(0.0, chargeRoom));
        iMaxDis = Math.Min(iMaxDis, Math.Max(0.0, dischargeRoom));
        if (i > iMaxDis)
        {
            reason ??= Cell.Soc - c.SocMin < SocEdgeProximity
                    || Math.Abs(iMaxDis - (Cell.Soc - c.SocMin) * 3600.0 * Cell.CapacityAh / dt) < LimitMatchToleranceA ? "SoC floor"
                : derate < 1 && Math.Abs(iMaxDis - c.MaxDischargeCRate * Cell.CapacityAh * derate) < LimitMatchToleranceA ? "temperature derate"
                : Math.Abs(iMaxDis - (e - c.VMin) / r) < LimitMatchToleranceA ? "cut-off voltage" : "current limit";
            i = iMaxDis;
        }
        else if (i < -iMaxChg)
        {
            reason ??= c.SocMax - Cell.Soc < SocEdgeProximity
                    || (iMaxChg > 0 && Math.Abs(iMaxChg - (c.SocMax - Cell.Soc) * 3600.0 * Cell.CapacityAh / dt / c.CoulombicEfficiency) < LimitMatchToleranceA) ? "SoC ceiling"
                : tc < c.TMinChargeC ? "too cold to charge"
                : Math.Abs(iMaxChg - (c.VMax - e) / r) < LimitMatchToleranceA ? "charge voltage" : "current limit";
            i = -iMaxChg;
        }

        double vCell = Cell.TerminalVoltage(i);
        double heatJ = Cell.Step(i, dt) * CellCount;
        Ageing?.Advance(Cell, i, dt);

        // Lumped thermal model: cell heat in, passive loss to ambient, HVAC removing heat above its setpoint.
        double mcp = c.MassKg * c.SpecificHeat * CellCount;
        double passiveW = c.PassiveCoolingWPerK * CellCount * (Cell.TemperatureK - (AmbientC + ChemistryParameters.Kelvin0));
        double tNoHvac = Cell.TemperatureK + (heatJ - passiveW * dt) / mcp;
        double hvacW = 0.0;
        double setK = c.Auxiliary.HvacSetpointC + ChemistryParameters.Kelvin0;
        if (c.Auxiliary.HvacCop > 0 && tNoHvac > setK)
            hvacW = Math.Min((tNoHvac - setK) * mcp / dt, c.Auxiliary.HvacCapacityFraction * RatedPowerW);
        Cell.TemperatureK = tNoHvac - hvacW * dt / mcp;

        double dcPower = vCell * i * CellCount;
        double aux = c.Auxiliary.StandbyFraction * RatedPowerW
            + (c.Auxiliary.HvacCop > 0 ? hvacW / c.Auxiliary.HvacCop : 0.0)
            + c.Auxiliary.PumpFraction * Math.Abs(dcPower);
        bool limited = reason is not null && Math.Abs(dcPower - requestedDcPowerW) > LimitedRelativeTolerance * Math.Max(1.0, Math.Abs(requestedDcPowerW));
        return new PackStepResult(dcPower, i * ParallelStrings, vCell * SeriesCells, heatJ / dt, aux,
            limited, limited ? reason : null);
    }

    /// <summary>
    /// The instantaneous DC power limits (W, both positive): the power at the BMS current limit for discharge and
    /// charge at the present state (current, cut-off/charge voltage, SoC window, temperature), capped at the
    /// maximum-power-transfer point. Cheap - no look-ahead - so a controller can query it every step.
    /// </summary>
    public (double DischargeW, double ChargeW) InstantaneousLimitsW()
    {
        (double iDis, double iChg) = CurrentLimits();
        double e = Cell.InternalEmf, r = Cell.R0;
        iDis = Math.Min(iDis, e / (2.0 * r));
        return ((e * iDis - r * iDis * iDis) * CellCount, (e * iChg + r * iChg * iChg) * CellCount);
    }

    /// <summary>Per-cell BMS current limits (A, both positive) at the present state.</summary>
    private (double Discharge, double Charge) CurrentLimits()
    {
        ChemistryParameters c = Chemistry;
        double e = Cell.InternalEmf, r = Cell.R0, tc = Cell.TemperatureC;
        double derate = Math.Clamp((c.TMaxC - tc) / Math.Max(DivisorGuard, c.TMaxC - c.TDerateC), 0.0, 1.0);
        double iDis = Math.Min(c.MaxDischargeCRate * Cell.CapacityAh * derate, Math.Max(0.0, (e - c.VMin) / r));
        double iChg = Math.Min(c.MaxChargeCRate * Cell.CapacityAh * derate, Math.Max(0.0, (c.VMax - e) / r));
        if (Cell.Soc <= c.SocMin) iDis = 0.0;
        if (Cell.Soc >= c.SocMax || tc < c.TMinChargeC) iChg = 0.0;
        return (iDis, iChg);
    }

    /// <summary>The current whose Peukert-effective value is <paramref name="effective"/> (inverts
    /// <c>I_eff = I (I / Iref)^(k-1)</c>, valid above the reference rate).</summary>
    private double PeukertInverse(double effective)
    {
        double k = Chemistry.PeukertExponent, iRef = Chemistry.PeukertReferenceCRate * Chemistry.CapacityAh;
        return DetMath.Pow(effective * DetMath.Pow(iRef, k - 1.0), 1.0 / k);
    }

    /// <summary>A copy with identical state (ageing tracking off), for look-ahead.</summary>
    public PackModel Clone()
    {
        var p = new PackModel(Chemistry, SeriesCells, ParallelStrings, Cell.Soc, AmbientC, RatedPowerW, trackAgeing: false);
        p.Cell = Cell.Clone();
        return p;
    }

    /// <summary>
    /// R231 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): a copy with identical state
    /// that, when <paramref name="keepAgeing"/> is true, also carries this pack's ageing model and its accumulated fade (a
    /// deep copy: the two packs then age independently). <paramref name="keepAgeing"/> false is <see cref="Clone()"/>, the
    /// look-ahead copy. A pack that does not track ageing gives a copy that does not either.
    /// </summary>
    public PackModel Clone(bool keepAgeing)
    {
        PackModel p = Clone();
        if (keepAgeing && Ageing is not null) p.Ageing = Ageing.Copy();
        return p;
    }

    /// <summary>
    /// R231: put this pack back into <paramref name="snapshot"/>'s state IN PLACE (the cell's charge, RC voltages,
    /// temperature and capacity fade; the ambient temperature; the accumulated ageing fade), keeping this object, so every
    /// holder of the pack sees the reset. Ageing tracking is this pack's own and is never switched off: a tracking pack
    /// restored from a snapshot without ageing restarts its fade from zero. Fails loud (ArgumentException) on a snapshot of
    /// another chemistry or another cell arrangement.
    /// </summary>
    public void RestoreFrom(PackModel snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Equals(snapshot.Chemistry, Chemistry) || snapshot.SeriesCells != SeriesCells || snapshot.ParallelStrings != ParallelStrings)
            throw new ArgumentException(
                $"PackModel.RestoreFrom: the snapshot is a different pack ({snapshot.Chemistry.Name} {snapshot.SeriesCells}s{snapshot.ParallelStrings}p, "
                + $"this is {Chemistry.Name} {SeriesCells}s{ParallelStrings}p).", nameof(snapshot));
        Cell = snapshot.Cell.Clone();
        AmbientC = snapshot.AmbientC;
        if (Ageing is not null)
        {
            if (snapshot.Ageing is not null) Ageing.RestoreFrom(snapshot.Ageing);
            else Ageing = new AgeingModel(Chemistry.Ageing);
        }
    }

    /// <summary>
    /// State of power: the largest constant DC power the pack can sustain for <paramref name="horizonSeconds"/>
    /// without any BMS limit binding, for discharge and for charge (both returned positive). Found by bisection
    /// on a simulated copy, so it includes the voltage sag building up over the horizon (the RC and SoC dynamics),
    /// the SoC window and heating - a short pulse and a 30-minute delivery give very different answers, and
    /// chemistries differ exactly there. Bug hunt 2026-10-29, finding 64 (documented): both powers are at the pack's DC
    /// TERMINALS (after the cells' internal loss, before the converter). StorageDispatch.FromPack relies on that: it caps
    /// grid-side discharge at this x the converter efficiency and charge at this / the converter efficiency, and then
    /// applies the cell efficiency only in the energy balance, which is consistent only for terminal watts.
    /// </summary>
    public (double DischargeW, double ChargeW) StateOfPower(double horizonSeconds, int substeps = 40)
    {
        double Search(double sign)
        {
            // (The old expression carried a no-op "/ 1.0", i.e. per one hour; x / 1.0 == x exactly in IEEE 754,
            // so dropping it leaves the bound bit-identical.)
            double hi = Math.Max(RatedPowerW, NominalEnergyWh * MaxSearchCRate);
            double lo = 0.0;
            if (!Sustains(sign * ProbePowerW, horizonSeconds, substeps)) return 0.0;
            for (int it = 0; it < MaxBisectionIterations; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (Sustains(sign * mid, horizonSeconds, substeps)) lo = mid; else hi = mid;
                if (hi - lo < BisectionRelativeTolerance * Math.Max(1.0, hi)) break;
            }
            return lo;
        }
        return (Search(+1.0), Search(-1.0));
    }

    private bool Sustains(double powerW, double horizon, int substeps)
    {
        PackModel sim = Clone();
        double dt = horizon / substeps;
        for (int k = 0; k < substeps; k++)
            if (sim.Step(powerW, dt).Limited) return false;
        return true;
    }
}
