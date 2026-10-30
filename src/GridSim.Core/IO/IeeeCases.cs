using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;

namespace GridSim.Core.IO;

/// <summary>
/// Built-in standard test systems with published solutions, for validation and
/// demos. Kept as plain factory methods so other sources (a future GDA adapter
/// pulling real NESO/DNO data, a MATPOWER/JSON loader) can produce a
/// <see cref="GridModel"/> exactly the same way  -  the solver neither knows nor
/// cares where the model came from.
/// </summary>
public static class IeeeCases
{
    /// <summary>
    /// Transient reactance X'd (pu on the machine base) of the three WSCC 9-bus machines, by generator bus, from
    /// Anderson and Fouad, "Power System Control and Stability" (the standard WSCC-9 dynamic data). The classical and
    /// RMS stability studies use it for <see cref="Case9"/>; R151 named it once (it was repeated five times in the CLI).
    /// </summary>
    public static IReadOnlyDictionary<int, double> Wscc9TransientReactancePu { get; } =
        new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    /// <summary>
    /// Nominal frequency (Hz) of the WSCC 9-bus system: 60 Hz. N017 / R269 (D2 nominal frequency, October 2026; Ruling R1:
    /// Mark delegated EE calls 2026-10-04). Sources: P. M. Anderson and A. A. Fouad, "Power System Control and Stability",
    /// 2nd ed., IEEE Press / Wiley 2003, ch. 2 "The Elementary Mathematical Model" (chapter title confirmed from the
    /// Library of Congress contents; the example number, given here from memory as Example 2.6, is unverified) (the
    /// nine-bus system, the WSCC three-machine equivalent of the Western US grid, integrated with the synchronous speed
    /// 2 * pi * 60 = 377 rad/s); P. W. Sauer and M. A. Pai, "Power System Dynamics and Stability", Prentice Hall 1998,
    /// ch. 7, Example 7.1 (CONFIRMED 2026-10-11, N276: "the popular Western System Coordinating Council (WECC)
    /// 3-machine, 9-bus system ... The base MVA is 100, and system frequency is 60 Hz"; it names Anderson and Fouad as
    /// a source of the same system). The WSCC (now WECC) interconnection is a North American 60 Hz system; the 60 Hz
    /// itself is not in doubt. Before D2 the case inherited the GridModel default of 50 Hz.
    /// </summary>
    public const double Wscc9NominalFrequencyHz = 60.0;

    /// <summary>
    /// Nominal frequency (Hz) of the two-area four-machine case: 60 Hz, the frequency of the Kundur two-area system it is
    /// structured on. N017 / R269 (D2, October 2026; Ruling R1). Sources: P. Kundur, "Power System Stability and Control",
    /// McGraw-Hill 1994, Example 12.6 (the two-area system; that Example 12.6 is the two-area system is CONFIRMED
    /// 2026-10-11, N276, by the IEEE PES PSDP task force "Report on the 2-area, 4-generator system", Version 5, 2014,
    /// which says its data "corresponds to that presented in Example 12.6 of" Kundur); IEEE PES Technical Report TR-18,
    /// "Benchmark Systems for Small-Signal Stability Analysis and Control", August 2015 (the two-area system). The
    /// earlier "TR-18 ch. 2" is doubtful and is dropped: the task force's open TPWRS summary (Canizares et al. 2016)
    /// orders the two-area system as its Benchmark 3, so it is unlikely to be chapter 2 [VERIFY against TR-18, which is
    /// free to PES members only]. No public source consulted states 60 Hz for the two-area system in words; the 60 Hz
    /// is Kundur's (from memory of the book, [VERIFY]).
    /// </summary>
    public const double TwoAreaNominalFrequencyHz = 60.0;

    /// <summary>
    /// The WSCC 9-bus system (MATPOWER <c>case9</c>): 3 generators, 3 loads, 100 MVA base, 345 kV. A small,
    /// well-documented benchmark for load flow.
    ///
    /// <para><b>N199 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): a built-in case equals its MATPOWER
    /// file.</b> This case used to be typed out here by hand, and the hand copy had drifted from
    /// <c>data/matpower/lib/case9.m</c>: it carried no per-bus voltage limits (the file gives 0.9 to 1.1 pu on every
    /// bus), no generator Pmin (the file gives 10 MW per unit), no RATE_B / RATE_C ratings (the file repeats RATE_A) and
    /// no cost curves (the file's <c>mpc.gencost</c>). Since per-bus limits became the contingency default (N092) the
    /// name <c>case9</c> and the file <c>case9.m</c> gave different N-1 verdicts for the same network (5 vs 4 insecure
    /// outages), and since N193 their DC-OPF problems differed (Pmin). The network and every power-flow and OPF field are
    /// now read from the very file (the embedded copy of <c>data/matpower/lib/case9.m</c>, the resource the other
    /// built-in IEEE cases already use), so the two cannot drift again.</para>
    ///
    /// <para><b>The one deliberate addition: the dynamic data.</b> A MATPOWER file has no inertia column, and its
    /// <c>mBase</c> of 100 MVA is the file's placeholder, not a machine rating. The built-in case keeps the published
    /// WSCC 3-machine dynamic set from Anderson and Fouad, "Power System Control and Stability" (the classic WSCC 9-bus):
    /// each machine's own rating as MBase (247.5 / 192 / 128 MVA) and its inertia constant H on that base (23.64 / 6.40 /
    /// 3.01 s), so the machines carry about 5851 / 1229 / 385 MW*s of stored energy (E = H * MBase). These fields do NOT
    /// enter the power-flow or OPF equations (the load flow and the dispatch are the file's, bit for bit); they drive the
    /// frequency, RoCoF and stability screens, and, through MBase, the per-unit base of the machine reactances that the
    /// fault and harmonic studies synthesise. Those studies therefore still differ between <c>case9</c> and
    /// <c>case9.m</c>, because the file carries no machine data (exactly as before N199). N199AlignedCase9Tests proves
    /// every other field equal to the file and the solves bit-identical.</para>
    /// </summary>
    public static GridModel Case9()
    {
        // Read the network, the limits, the ratings and the costs from the embedded MATPOWER file itself, declaring the
        // system's nominal frequency (N017, D2: 60 Hz, see Wscc9NominalFrequencyHz; the file carries none). The frequency
        // enters no power-flow or OPF equation, so every load-flow and dispatch number is the file's, bit for bit.
        GridModel file = Mpc("case9", "IEEE 9-bus (WSCC)", Wscc9NominalFrequencyHz);

        // Overlay the Anderson and Fouad machine data on each unit (keyed by its bus; the file has one unit per bus).
        // Every other generator field (Pg, Qg, Vg, Q limits, Pmin, Pmax, status, cost) stays exactly as the file gives it.
        var units = new Generator[file.AllGenerators.Count];
        for (int i = 0; i < units.Length; i++)
        {
            Generator g = file.AllGenerators[i];
            (double mBase, double h) = Wscc9MachineData[g.Bus];
            units[i] = g with { MBase = mBase, H = h };
        }
        return file.CopyWith(generators: units);
    }

    /// <summary>
    /// The WSCC 9-bus machine data by generator bus: (MBase, the machine's own rating in MVA; H, the inertia constant in
    /// seconds on that base), from Anderson and Fouad, "Power System Control and Stability" (the values the hand-coded
    /// case carried before N199, unchanged).
    /// </summary>
    private static readonly IReadOnlyDictionary<int, (double MBase, double H)> Wscc9MachineData =
        new Dictionary<int, (double MBase, double H)>
        {
            [1] = (247.5, 23.64),
            [2] = (192.0, 6.40),
            [3] = (128.0, 3.01),
        };

    /// <summary>
    /// A symmetric two-area, four-machine test system - the canonical <b>intermediate-N</b> transient
    /// benchmark shape (two areas of two machines each, joined by a weak tie), used to validate inter-area
    /// vs local electromechanical modes between the analytic N=1/2 oracles and the 3-machine WSCC-9.
    ///
    /// <para><b>Provenance:</b> this is Kundur-STRUCTURED but carries self-consistent, mirror-symmetric
    /// parameters chosen so the modal ordering is analytically predictable (a weak tie makes the inter-area
    /// mode slower than the stiff intra-area local mode) - it is NOT the exact published Kundur two-area
    /// dataset. Reproducing Kundur's / IEEE-39's published rotor-angle CURVES needs the full exciter/governor
    /// and fault-clearing records (the .dyr data), which remain deferred. Machines are identical: H = 6.0 s
    /// on a 200 MVA base, transient reactance X'd = 0.05 pu.</para>
    ///
    /// <para><b>Nominal frequency (N017 / R269, D2, October 2026):</b> 60 Hz, the frequency of Kundur's two-area system
    /// (see <see cref="TwoAreaNominalFrequencyHz"/> for the sources). It used to inherit the GridModel default of 50 Hz.
    /// The network data are per unit, so the power flow is unchanged; the swing, modal and EMT studies scale with f0.</para>
    /// </summary>
    public static GridModel TwoAreaFourMachine() => new GridModel
    {
        Name = "Two-area four-machine (Kundur-structured, symmetric)",
        BaseMva = 100,
        FrequencyHz = TwoAreaNominalFrequencyHz,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 230),                  // Area 1, G1
            new Bus(2, BusType.Pv,    BaseKv: 230),                  // Area 1, G2
            new Bus(3, BusType.Pv,    BaseKv: 230),                  // Area 2, G3
            new Bus(4, BusType.Pv,    BaseKv: 230),                  // Area 2, G4
            new Bus(5, BusType.Pq, Pd: 350, Qd: 80, BaseKv: 230),   // Area 1 hub / load
            new Bus(6, BusType.Pq, Pd: 350, Qd: 80, BaseKv: 230),   // Area 2 hub / load
        },
        // Identical machines (H on the 200 MVA machine base); X'd = 0.05 is supplied to ClassicalInit.
        Generators = new[]
        {
            new Generator(1, Pg: 175, Qg: 0, Vg: 1.03, Qmin: -200, Qmax: 200, Pmax: 300, MBase: 200, H: 6.0),
            new Generator(2, Pg: 175, Qg: 0, Vg: 1.03, Qmin: -200, Qmax: 200, Pmax: 300, MBase: 200, H: 6.0),
            new Generator(3, Pg: 175, Qg: 0, Vg: 1.03, Qmin: -200, Qmax: 200, Pmax: 300, MBase: 200, H: 6.0),
            new Generator(4, Pg: 175, Qg: 0, Vg: 1.03, Qmin: -200, Qmax: 200, Pmax: 300, MBase: 200, H: 6.0),
        },
        // Stiff intra-area machine->hub links (X = 0.05); a weak inter-area tie (X = 0.25, 5x). Balanced
        // areas (350 MW gen vs 350 MW load each) carry ~zero base tie flow, keeping the mirror symmetry.
        Branches = new[]
        {
            new Branch(1, 5, 0.0025, 0.0500, 0.0),
            new Branch(2, 5, 0.0025, 0.0500, 0.0),
            new Branch(3, 6, 0.0025, 0.0500, 0.0),
            new Branch(4, 6, 0.0025, 0.0500, 0.0),
            new Branch(5, 6, 0.0125, 0.2500, 0.0, Name: "tie"),
        },
    };

    // The remaining standard systems are the stock MATPOWER cases, parsed from the .m files
    // embedded in this assembly (source: MATPOWER 8.0 data/, committed under data/matpower/lib).
    // Kept as factory methods so callers use them exactly like the hand-coded Case9() above.

    /// <summary>IEEE 14-bus system (MATPOWER <c>case14</c>).</summary>
    public static GridModel Case14() => Mpc("case14", "IEEE 14-bus");

    /// <summary>IEEE 30-bus system (MATPOWER <c>case30</c>).</summary>
    public static GridModel Case30() => Mpc("case30", "IEEE 30-bus");

    /// <summary>IEEE 57-bus system (MATPOWER <c>case57</c>).</summary>
    public static GridModel Case57() => Mpc("case57", "IEEE 57-bus");

    /// <summary>IEEE 118-bus system (MATPOWER <c>case118</c>).</summary>
    public static GridModel Case118() => Mpc("case118", "IEEE 118-bus");

    /// <summary>IEEE 300-bus system (MATPOWER <c>case300</c>).</summary>
    public static GridModel Case300() => Mpc("case300", "IEEE 300-bus");

    /// <summary>IEEE 145-bus dynamic test system (MATPOWER <c>case145</c>).</summary>
    public static GridModel Case145() => Mpc("case145", "IEEE 145-bus (dynamic)");

    /// <summary>
    /// IEEE Reliability Test System (RTS-79), 24-bus single area (MATPOWER <c>case24_ieee_rts</c>).
    /// The three-area 73-bus RTS-96 is a separate, larger case (see <c>data/matpower/rts</c>).
    /// </summary>
    public static GridModel Case24Rts() => Mpc("case24_ieee_rts", "IEEE RTS-79 24-bus");

    /// <summary>
    /// CIGRE Task Force C6.04.02 European medium-voltage distribution benchmark (~15 nodes, 20 kV).
    /// A radial distribution feeder rather than a transmission system.
    /// </summary>
    public static GridModel CigreMv() => Mpc("cigre_mv", "CIGRE MV benchmark (TF C6.04.02)");

    /// <summary>Parse a MATPOWER case embedded in this assembly as <c>GridSim.Core.cases.&lt;res&gt;.m</c>.
    /// <paramref name="frequencyHz"/> is the system's nominal frequency when the source states it (case9, N017); null keeps
    /// the parser's documented 50 Hz assumption (the other embedded cases: an open item in the D2 notes).</summary>
    private static GridModel Mpc(string res, string name, double? frequencyHz = null)
    {
        string resource = $"GridSim.Core.cases.{res}.m";
        using Stream s = typeof(IeeeCases).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded MATPOWER case '{resource}' not found.");
        using var reader = new StreamReader(s);
        return MatpowerParser.Parse(reader.ReadToEnd(), name, frequencyHz);
    }
}
