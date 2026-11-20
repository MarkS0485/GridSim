using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;

namespace GridSim.Core.Protection.ShortCircuit.Ansi;

/// <summary>A medium/high-voltage breaker's ANSI-style ratings for the check: rated maximum voltage (kV), closing and latching capability
/// (kA rms, total current) and the symmetrical interrupting capability (kA rms) at rated maximum voltage. The voltage range factor K and
/// the rating-structure rules of C37.04 / C37.06 are AWAITING STANDARD TEXT: the check compares the duty with the rated values as given
/// and does not credit any current increase below rated voltage.</summary>
public sealed record AnsiBreaker(double RatedMaxKv, double ClosingLatchRmsKa, double InterruptingKa);

/// <summary>Options of an ANSI study. <see cref="PartingCycles"/> is the contact parting time in cycles of the system frequency (null = not
/// given: the interrupting duty is OUT OF SCOPE). <see cref="PrefaultPu"/> is the prefault voltage E (pu).</summary>
public sealed record AnsiOptions(double PrefaultPu = 1.0, double? PartingCycles = null, AnsiBreaker? Breaker = null);

public enum AnsiStatus { Ok, Incomplete, Failed }

/// <summary>One source's share of the fault, from the E/X current divider of each network (kA).</summary>
public sealed record AnsiSourceCurrent(string Kind, string Name, int Bus, string Class, bool Local, double MomentaryKa, double InterruptingKa);

public sealed record AnsiBreakerCheck(bool VoltageOk, bool ClosingOk, bool InterruptingChecked, bool InterruptingOk, bool Adequate,
    double ClosingMarginKa, double InterruptingMarginKa);

/// <summary>The ANSI-style duty at one bus. NaN means not computed; <see cref="InterruptingNote"/> says why an interrupting figure is absent.</summary>
public sealed record AnsiBusResult(
    int Bus, AnsiStatus Status, IReadOnlyList<string> Reasons, double NominalKv, double BaseKa, double PrefaultPu,
    double MomentaryXPu, double RPu, double MomentaryXoverR, double MomentarySymKa, double MomentaryMf, double MomentaryRmsKa, double PeakKa,
    double InterruptingXPu, double InterruptingXoverR, double InterruptingSymKa, double Nacd, double RemoteMfReference,
    double InterruptingMf, double InterruptingKa, double ReferenceInterruptingKa, string InterruptingNote,
    IReadOnlyList<AnsiSourceCurrent> Sources, AnsiBreakerCheck? Breaker, IReadOnlyList<string> Notes, IReadOnlyList<string> Awaiting)
{
    public string Verdict => Status switch { AnsiStatus.Ok => "OK", AnsiStatus.Incomplete => "INCOMPLETE", _ => "FAILED" };
    public bool InterruptingInScope => !double.IsNaN(InterruptingKa);
}

/// <summary>
/// An ANSI/IEEE C37.010-style (E/X method) breaker duty, built beside <see cref="Iec60909"/> (called only for the fault island; unchanged).
/// <para>
/// Method, from public descriptions (EasyPower and SKM ANSI documentation, EC&amp;M breaker-duty article, Siemens TechTopics; see the manual):
/// (1) a first-cycle (momentary) reactance network and an interrupting (contact-parting) reactance network, each with the machine X''d
/// scaled by the multiplier of its source; (2) an R network (all reactances zero, machine R = X''d / (X/R)) so that the fault-point X/R is
/// X (network) / R (network); (3) E/X for the symmetrical currents; (4) a multiplying factor on the first-cycle current (close and latch) and
/// on the interrupting current (function of X/R and contact parting time; here the remote-source factor derived from DC-offset physics).
/// Order: validate, resolve sources, build networks, solve, per-source currents, X/R, factors, NACD, breaker check.
/// </para>
/// <para>
/// NOT in this build (AWAITING STANDARD TEXT, listed on every result): the multiplier table per machine type, the local-source multiplying
/// factors against NACD (a fault with local generation gets no interrupting figure), the contact-parting-time table, the C37.010 first-cycle
/// factor above X/R 25, the exact definition of NACD, the voltage range factor K, the clause numbers and the standard's worked examples.
/// Unit transformer R, line charging, loads and converter-interfaced plant are neglected (converters are listed in the notes).
/// </para>
/// </summary>
public static class AnsiDuty
{
    public static readonly IReadOnlyList<string> Awaiting = new[]
    {
        "AWAITING STANDARD TEXT: IEEE C37.010 / C37.5 clause numbers and edition (text is paid; not read)",
        "AWAITING STANDARD TEXT: reactance multipliers per machine type for the first-cycle and interrupting networks (supplied in the data file, not defaulted)",
        "AWAITING STANDARD TEXT: local-source interrupting multiplying factors against NACD and X/R (a fault with NACD > 0 has no interrupting figure)",
        "AWAITING STANDARD TEXT: exact definition of NACD and the network used to form the X/R for each duty",
        "AWAITING STANDARD TEXT: first-cycle multiplying factor above X/R 25 (the half-cycle rms expression is used) and the standard's peak (crest) factor",
        "AWAITING STANDARD TEXT: breaker contact parting time against rated interrupting time; voltage range factor K; low-voltage (C37.13) duty",
        "AWAITING STANDARD TEXT: the standard's worked examples (none reproduced)",
    };

    /// <summary>Resistance floor (pu) for a branch with no resistance in the R network, which would otherwise be a short circuit (singular).</summary>
    public const double RFloorPu = 1e-9;

    private sealed record Src(string Kind, string Name, int Bus, int Index, double MBase, double XdSys, double XtSys, AnsiSourceData D);

    public static AnsiBusResult Compute(GridModel model, int busId, AnsiData data, AnsiOptions options)
    {
        var reasons = new List<string>();
        var notes = new List<string>();
        if (!model.Buses.Any(b => b.Id == busId)) return Fail(busId, AnsiStatus.Failed, $"bus {busId} is not in the case");
        double kv = model.Buses.First(b => b.Id == busId).BaseKv;
        if (!(kv > 0.0)) return Fail(busId, AnsiStatus.Failed, $"bus {busId} has no base kV");
        if (!(options.PrefaultPu > 0.0) || !double.IsFinite(options.PrefaultPu)) return Fail(busId, AnsiStatus.Failed, "prefault voltage must be finite and above 0 pu");
        if (options.PartingCycles is double pc && (!(pc >= 0.0) || !double.IsFinite(pc))) return Fail(busId, AnsiStatus.Failed, "contact parting time must be finite and 0 cycles or more");

        GridModel isl;
        try { isl = Iec60909.FaultIsland(model, busId); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return Fail(busId, AnsiStatus.Failed, ex.Message); }
        int n = isl.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[isl.Buses[i].Id] = i;
        int k = index[busId];
        double baseMva = isl.BaseMva;

        // Resolve sources: island generators in model order, then motors in file order.
        var srcs = new List<Src>();
        var used = new HashSet<AnsiSourceData>();
        var plain = isl.Generators.Where(g => !g.PowerFlowOnly && !g.IsConverterInterfaced).ToList();
        foreach (Generator g in isl.Generators)
        {
            string name = g.Name ?? $"gen@bus {g.Bus}";
            if (g.PowerFlowOnly) continue;
            if (g.IsConverterInterfaced) { notes.Add($"converter-interfaced unit '{name}' at bus {g.Bus} is neglected (no ANSI converter model)"); continue; }
            int atBus = plain.Count(y => y.Bus == g.Bus);
            AnsiSourceData? d = data.Sources.FirstOrDefault(x => x.Bus == g.Bus && (x.Name is null ? atBus == 1 : x.Name == name));
            if (d is null) { reasons.Add($"source '{name}' at bus {g.Bus}: no ANSI data (X/R, local, multipliers); none is defaulted"); continue; }
            if (!(g.SubtransientXdPu > 0.0)) { reasons.Add($"source '{name}': the case carries no X''d (the synthesised default would be used); ANSI needs a real value"); continue; }
            if (!(g.MBase > 0.0)) { reasons.Add($"source '{name}': MBase missing"); continue; }
            used.Add(d);
            double f = baseMva / g.MBase;
            srcs.Add(new Src("machine", name, g.Bus, index[g.Bus], g.MBase, g.SubtransientXdPu * f, Math.Max(0.0, g.UnitTransformerXPu) * f, d));
        }
        foreach (AnsiMotorData m in data.Motors)
        {
            if (!model.Buses.Any(b => b.Id == m.Source.Bus)) { reasons.Add($"motor '{m.Name}': bus {m.Source.Bus} is not in the case"); continue; }
            if (!index.TryGetValue(m.Source.Bus, out int mi)) { notes.Add($"motor '{m.Name}' at bus {m.Source.Bus} is outside the fault island and is ignored"); continue; }
            used.Add(m.Source);
            srcs.Add(new Src("motor", m.Name, m.Source.Bus, mi, m.RatedMva, m.XdppPu * baseMva / m.RatedMva, 0.0, m.Source));
        }
        foreach (AnsiSourceData d in data.Sources)
            if (!used.Contains(d) && !model.Generators.Any(g => g.Bus == d.Bus && (d.Name is null || d.Name == (g.Name ?? $"gen@bus {g.Bus}"))))
                reasons.Add($"ANSI data for bus {d.Bus}{(d.Name is null ? "" : " '" + d.Name + "'")} matches no generator in the case");
        if (reasons.Count > 0) return Fail(busId, AnsiStatus.Incomplete, reasons);
        if (srcs.Count == 0) return Fail(busId, AnsiStatus.Failed, $"no fault source in the island of bus {busId}");

        bool intKnown = srcs.All(s => s.D.InterruptingMultiplier.HasValue);
        string? intNote = null;
        if (!intKnown) intNote = "OUT OF SCOPE: an interrupting multiplier is not given for " + string.Join(", ", srcs.Where(s => !s.D.InterruptingMultiplier.HasValue).Select(s => s.Name));
        else if (options.PartingCycles is null) intNote = "OUT OF SCOPE: contact parting time (cycles) not given";

        double baseKa = baseMva / (Math.Sqrt(3.0) * kv);
        double e = options.PrefaultPu;

        // Networks.
        GridModel xModel = isl.CopyWith(branches: isl.Branches.Select(b => b with { R = 0.0, B = 0.0 }).ToList());
        GridModel rModel = isl.CopyWith(branches: isl.Branches.Select(b => b with { R = Math.Max(b.R, RFloorPu), X = 0.0, B = 0.0 }).ToList());
        var unit = new Complex[n];
        unit[k] = Complex.One;
        Complex[] zMom, zInt = Array.Empty<Complex>(), zR;
        try
        {
            zMom = XColumn(xModel, srcs, s => s.D.FirstCycleMultiplier, unit);
            if (intKnown) zInt = XColumn(xModel, srcs, s => s.D.InterruptingMultiplier!.Value, unit);
            var rShunts = srcs.Select(s => (s.Index, Complex.One / new Complex(s.XdSys / s.D.XOverR, 0.0))).ToList();
            zR = FaultNetworkSystem.ForModel(rModel, rShunts, seriesOnly: true).Solve(unit);
        }
        catch (InvalidOperationException ex) { return Fail(busId, AnsiStatus.Failed, ex.Message); }

        double xm = zMom[k].Imaginary, rth = zR[k].Real;
        if (!(xm > 0.0)) return Fail(busId, AnsiStatus.Failed, $"first-cycle network reactance at bus {busId} is {xm:R} pu; it must be above 0");
        double xrM = rth > 0.0 ? xm / rth : double.PositiveInfinity;
        double symM = e / xm * baseKa;
        double mfM = MultiplyingFactors.MomentaryMf(xrM);
        double peak = Math.Sqrt(2.0) * MultiplyingFactors.HalfCyclePeakFactor(xrM) * symM;

        // Per-source currents (E/X current divider): I_g = E |Z_gk| / (|Z_kk| X_src).
        var contribs = new List<AnsiSourceCurrent>(srcs.Count);
        double xi = double.NaN, xrI = double.NaN, symI = double.NaN, nacd = double.NaN, mfRef = double.NaN, mfI = double.NaN, intKa = double.NaN, refKa = double.NaN;
        if (intKnown) { xi = zInt[k].Imaginary; if (!(xi > 0.0)) return Fail(busId, AnsiStatus.Failed, $"interrupting network reactance at bus {busId} is {xi:R} pu; it must be above 0"); }
        double sumLocal = 0.0, sumAll = 0.0;
        foreach (Src s in srcs)
        {
            double im = e * Math.Abs(zMom[s.Index].Imaginary) / (Math.Abs(xm) * (s.D.FirstCycleMultiplier * s.XdSys + s.XtSys)) * baseKa;
            double ii = double.NaN;
            if (intKnown)
            {
                ii = e * Math.Abs(zInt[s.Index].Imaginary) / (Math.Abs(xi) * (s.D.InterruptingMultiplier!.Value * s.XdSys + s.XtSys)) * baseKa;
                sumAll += ii;
                if (s.D.Local) sumLocal += ii;
            }
            contribs.Add(new AnsiSourceCurrent(s.Kind, s.Name, s.Bus, s.D.Class, s.D.Local, im, ii));
        }
        if (intKnown)
        {
            xrI = rth > 0.0 ? xi / rth : double.PositiveInfinity;
            symI = e / xi * baseKa;
            nacd = sumAll > 0.0 ? sumLocal / sumAll : 0.0;
            if (options.PartingCycles is double cyc)
            {
                mfRef = MultiplyingFactors.RemoteInterruptingMf(xrI, cyc);
                refKa = mfRef * symI;
                if (nacd > 1e-12) intNote = $"OUT OF SCOPE: NACD = {nacd:R} > 0 (local generation); local multiplying factors are AWAITING STANDARD TEXT. Reference value uses the remote factor and is not a duty";
                else { mfI = mfRef; intKa = refKa; }
            }
        }

        AnsiBreakerCheck? chk = null;
        if (options.Breaker is AnsiBreaker br)
        {
            bool vOk = kv <= br.RatedMaxKv;
            bool cOk = mfM * symM <= br.ClosingLatchRmsKa;
            bool iChk = !double.IsNaN(intKa);
            bool iOk = !iChk || intKa <= br.InterruptingKa;
            chk = new AnsiBreakerCheck(vOk, cOk, iChk, iOk, vOk && cOk && iChk && iOk, br.ClosingLatchRmsKa - mfM * symM,
                iChk ? br.InterruptingKa - intKa : double.NaN);
        }
        return new AnsiBusResult(busId, AnsiStatus.Ok, Array.Empty<string>(), kv, baseKa, e, xm, rth, xrM, symM, mfM, mfM * symM, peak,
            xi, xrI, symI, nacd, mfRef, mfI, intKa, refKa, intNote ?? "", contribs, chk, notes, Awaiting);
    }

    private static Complex[] XColumn(GridModel xModel, List<Src> srcs, Func<Src, double> mult, Complex[] unit)
    {
        var shunts = srcs.Select(s => (s.Index, Complex.One / new Complex(0.0, mult(s) * s.XdSys + s.XtSys))).ToList();
        return FaultNetworkSystem.ForModel(xModel, shunts, seriesOnly: true).Solve(unit);
    }

    private static AnsiBusResult Fail(int bus, AnsiStatus st, string reason) => Fail(bus, st, new[] { reason });

    private static AnsiBusResult Fail(int bus, AnsiStatus st, IReadOnlyList<string> reasons)
    {
        double nan = double.NaN;
        return new AnsiBusResult(bus, st, reasons, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan, nan,
            "", Array.Empty<AnsiSourceCurrent>(), null, Array.Empty<string>(), Awaiting);
    }
}
