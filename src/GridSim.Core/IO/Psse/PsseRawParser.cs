using System.Globalization;
using System.Text.RegularExpressions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// Reads a PSS/E power-flow RAW file (revisions 30, 33, 34 and 35) into a <see cref="GridModel"/> (Tranche A, October 2026).
/// Mirrors <see cref="Matpower.MatpowerParser"/>: every number exactly as the file gives it where the model holds the
/// same quantity, every element the solvers cannot use either carried (in the parse result) or refused, never dropped
/// silently, and every refusal names the line, the section and the field.
/// </summary>
/// <remarks>
/// <para><b>What is read.</b> Case identification (system base, revision, base frequency), buses, loads, fixed shunts,
/// generators, non-transformer branches, system switching devices (revision 34 and 35), two- and three-winding
/// transformers with their impedance correction tables, two-terminal (line-commutated), VSC and multi-terminal DC links,
/// FACTS devices in modes 1 to 3, areas, zones and owners (as metadata), and switched shunts. Every other section is read
/// past with its records counted; an in-service device there that changes the power flow (capacitor-commutated
/// two-terminal links, a VSC converter regulating a remote bus, a multi-terminal converter on two DC buses, a FACTS device
/// regulating a remote bus or in modes 4 to 8, an induction machine with a standard design code, a double cage or
/// saturation) is refused unless <see cref="PsseReadOptions.AllowUnmodelled"/> is set. Induction machines of custom
/// design with a single cage are read.
/// GNE devices are always refused. Revision 35's system-wide data and node-breaker substation data are read past
/// (the bus-branch records are complete without them), with a note.</para>
/// <para><b>How each element becomes the model.</b></para>
/// <list type="bullet">
/// <item>Buses keep file order and ids. IDE 4 (isolated) buses are dropped with everything connected to them, as the
/// MATPOWER reader does. NVHI / NVLO, when written, become the bus's own voltage limits.</item>
/// <item>Loads: constant power (PL, QL) is the bus demand. Constant admittance (YP, YQ: MW and MVAr at 1 pu, YQ
/// positive for a capacitive load) is a bus shunt, Gs += YP and Bs += YQ, which is exactly a constant-admittance load.
/// Constant current (IP, IQ) makes the bus a ZIP load (P = (PL + IP) (Cp + Bp V) with Bp = IP / (PL + IP), and the same
/// for Q). A bus whose constant-current part cannot be written that way (PL + IP = 0 with IP not 0) is refused.
/// In-service distributed generation on a load (DGENP, DGENQ with DGENF 1, revision 34 and 35) is netted off the load.
/// Out-of-service loads (STATUS 0) are skipped and counted.</item>
/// <item>Fixed shunts add GL / BL (MW and MVAr at 1 pu) to the bus shunt. Switched shunts are fixed at their initial
/// susceptance BINIT (no GridSim device switches them), with a note.</item>
/// <item>Generators: PG, QG, QT / QB (Q limits), PT / PB (P limits), VS (the set point, held at the unit's own bus),
/// MBASE (0: the system base). Out-of-service units are kept, marked out of service. A unit regulating a remote bus
/// (IREG) holds its own bus instead, with a note, as MATPOWER does. The machine impedances (ZR, ZX, RT, XT, GTAP) are
/// not used: a power flow does not need them and the fault data comes from the SEQ file.</item>
/// <item>Branches: R, X, B and the first three ratings. The line-end shunts GI, BI, GJ, BJ (pu on the system base) are
/// added to the end buses' shunts. A branch or switching device with R = X = 0 is given X = 0.0001 pu (PSS/E's default
/// zero-impedance threshold), with a note. Out-of-service branches are skipped and counted.</item>
/// <item>Two-winding transformers become one branch, winding 1 the from end: ratio t = w1 / w2, where each winding's
/// per-unit voltage is WINDV (CW 1), WINDV / bus base kV (CW 2), or WINDV x NOMV / bus base kV (CW 3, NOMV 0 = the
/// bus base); phase shift ANG1. Impedance: CZ 1 as written (system base), CZ 2 converted from the winding base
/// (SBASE1-2 and NOMV1) to the system base at winding 1's bus, CZ 3 from load loss (W) and impedance magnitude (pu on
/// SBASE1-2). This is MATPOWER's psse2mpc convention, except that a NOMV of 0 is taken as the bus base (psse2mpc divides
/// by it).</item>
/// <item>Magnetising admittance (MAG1, MAG2; CM 1 in pu on the system base, CM 2 as no-load loss in W and exciting
/// current in pu) is a shunt at winding 1's bus for a two-winding unit and at the star point for a three-winding unit.
/// psse2mpc leaves it out.</item>
/// <item>Three-winding transformers become a star-point bus (numbered from the next power of ten above the highest bus
/// number, as psse2mpc does; base kV of winding 1's bus; VMSTAR / ANSTAR as its starting voltage) and one branch per
/// winding, from the winding's bus to the star point, carrying that winding's ratio, phase shift and ratings and its
/// star impedance Zk = (Zkj + Zki - Zij) / 2. STAT 2, 3 and 4 take one winding out; STAT 0 the whole unit.</item>
/// <item>Automatic tap and phase control (COD) is not applied by default: taps stay at the file's ratios, as psse2mpc
/// keeps them, and the count is noted. With <see cref="PsseReadOptions.ApplyTapControl"/> each in-service voltage-control
/// winding (COD 1) becomes an <see cref="Oltc"/> record on its branch (see <see cref="Reader"/>'s tap control notes);
/// COD 2 to 5 are still noted, not applied.</item>
/// </list>
/// <para><b>Order.</b> Model branches are lines, then switching devices, then two-winding and then three-winding
/// transformers, each in file order. <see cref="PsseParseResult.BranchKeys"/> and
/// <see cref="PsseParseResult.MachineKeys"/> name the record each element came from.</para>
/// <para><b>Revision 30</b> (the layout of the UKGDS distribution cases and of PSS/E 30's own sample case) differs from 33
/// in three places, all read here: a bus record carries its own shunt (I, NAME, BASKV, IDE, GL, BL, AREA, ZONE, VM, VA,
/// OWNER; GL and BL are MW and MVAr at 1 pu, added to the bus shunt exactly as a fixed shunt is) and there is no fixed
/// shunt section; a switched shunt has no STAT field (it is always in service) and its BINIT is field 8; and the switched
/// shunt section comes straight after the VSC DC section, before the impedance correction tables. There are no GNE,
/// induction machine, system switching device or substation sections. Revisions 31 and 32 are refused: their layouts
/// sit between 30 and 33 (the fixed shunt section, the switched shunt STAT and ADJM fields and the bus voltage limits
/// arrived in steps), and with no sample file or format reference for either on hand, a guessed layout could read a
/// field as its neighbour without any error, so they are not guessed.</para>
/// <para><b>Nominal frequency.</b> BASFRQ when the file gives it; otherwise the caller's value; otherwise
/// <see cref="AssumedFrequencyHz"/> (PSS/E's own default base frequency), with a note.</para>
/// </remarks>
public static class PsseRawParser
{
    /// <summary>The revisions this reader supports.</summary>
    public static readonly IReadOnlyList<int> SupportedRevisions = new[] { 30, 33, 34, 35 };

    /// <summary>Why a revision is not read, for the refusal message: revisions 31 and 32 get the reason (no layout
    /// reference to check them against), any other the supported list alone.</summary>
    internal static string NotSupported(int revision)
    {
        string msg = $"PSS/E revision {revision} is not supported; supported: {string.Join(", ", SupportedRevisions)}";
        if (revision is 31 or 32)
            msg += " (revisions 31 and 32 lie between the 30 and 33 layouts and are not read: with no sample or format reference to check them against, a guessed layout could read one field as another without any error)";
        return msg;
    }

    /// <summary>The nominal frequency (Hz) assumed when the file's BASFRQ is 0 or missing and the caller gives none:
    /// PSS/E's own default base frequency. Recorded in the notes when used.</summary>
    public const double AssumedFrequencyHz = 60.0;

    /// <summary>The reactance (pu) given to a branch or switching device written with R = X = 0: PSS/E's default
    /// zero-impedance threshold THRSHZ.</summary>
    public const double ZeroImpedanceXPu = 0.0001;

    /// <summary>Read RAW text into a model. See <see cref="ParseWithDiagnostics"/>.</summary>
    public static GridModel Parse(string text, string? name = null, PsseReadOptions? options = null)
        => ParseWithDiagnostics(text, name, options).Model;

    /// <summary>Read a RAW file from disk. The case name defaults to the file stem.</summary>
    public static PsseParseResult ParseFile(string path, PsseReadOptions? options = null)
        => ParseWithDiagnostics(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), options);

    /// <summary>Read RAW text, returning the model with the notes and the metadata the model has no place for.</summary>
    public static PsseParseResult ParseWithDiagnostics(string text, string? name = null, PsseReadOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Empty PSS/E RAW text.");
        options ??= new PsseReadOptions();
        if (options.FrequencyHz is double fGiven && !(fGiven > 0 && double.IsFinite(fGiven)))
            throw new ArgumentOutOfRangeException(nameof(options), options.FrequencyHz, "The nominal frequency must be a positive, finite number of hertz.");
        if (options.Revision is int rGiven && !SupportedRevisions.Contains(rGiven))
            throw new ArgumentOutOfRangeException(nameof(options), rGiven, NotSupported(rGiven) + ".");

        var reader = new Reader(PsseText.Lines(text), name, options);
        return reader.Read();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Section names and the labels writers put in terminator comments ("0 / END OF BUS DATA, BEGIN LOAD DATA").
    // ------------------------------------------------------------------------------------------------------------

    private const string SystemWide = "SYSTEM-WIDE", BusS = "BUS", LoadS = "LOAD", FixedShuntS = "FIXED SHUNT",
        GeneratorS = "GENERATOR", BranchS = "BRANCH", SwitchingS = "SYSTEM SWITCHING DEVICE", TransformerS = "TRANSFORMER",
        AreaS = "AREA", TwoTerminalS = "TWO-TERMINAL DC", VscS = "VSC DC", ImpedanceS = "IMPEDANCE CORRECTION",
        MultiTerminalS = "MULTI-TERMINAL DC", MultiSectionS = "MULTI-SECTION LINE", ZoneS = "ZONE",
        InterAreaS = "INTER-AREA TRANSFER", OwnerS = "OWNER", FactsS = "FACTS DEVICE", SwitchedShuntS = "SWITCHED SHUNT",
        GneS = "GNE DEVICE", InductionS = "INDUCTION MACHINE", SubstationS = "SUBSTATION";

    /// <summary>The sections of a revision, in file order.</summary>
    internal static IReadOnlyList<string> SectionsFor(int revision)
    {
        // Revision 30: no fixed shunt section (the bus record carries GL, BL), the switched shunts straight after the VSC
        // DC links, and nothing after the FACTS devices.
        if (revision == 30)
            return new List<string> { BusS, LoadS, GeneratorS, BranchS, TransformerS, AreaS, TwoTerminalS, VscS, SwitchedShuntS,
                ImpedanceS, MultiTerminalS, MultiSectionS, ZoneS, InterAreaS, OwnerS, FactsS };
        var s = new List<string> { BusS, LoadS, FixedShuntS, GeneratorS, BranchS };
        if (revision >= 34) s.Add(SwitchingS);
        s.AddRange(new[] { TransformerS, AreaS, TwoTerminalS, VscS, ImpedanceS, MultiTerminalS, MultiSectionS, ZoneS,
            InterAreaS, OwnerS, FactsS, SwitchedShuntS, GneS, InductionS });
        if (revision >= 35) { s.Insert(0, SystemWide); s.Add(SubstationS); }
        return s;
    }

    /// <summary>The section a terminator label names, or null for a label this reader does not recognise.</summary>
    private static string? SectionOfLabel(string label)
    {
        string l = Regex.Replace(label.ToUpperInvariant(), @"\s+", " ").Trim();
        return l switch
        {
            "SYSTEM-WIDE" or "SYSTEM WIDE" => SystemWide,
            "BUS" => BusS,
            "LOAD" => LoadS,
            "FIXED SHUNT" => FixedShuntS,
            "GENERATOR" => GeneratorS,
            "BRANCH" or "NON-TRANSFORMER BRANCH" => BranchS,
            "SYSTEM SWITCHING DEVICE" or "SWITCHING DEVICE" => SwitchingS,
            "TRANSFORMER" => TransformerS,
            "AREA" or "AREA INTERCHANGE" => AreaS,
            "TWO-TERMINAL DC" or "TWO TERMINAL DC" or "2-TERMINAL DC" => TwoTerminalS,
            "VOLTAGE SOURCE CONVERTER" or "VSC DC" or "VSC DC LINE" => VscS,
            "IMPEDANCE CORRECTION" or "TRANSFORMER IMPEDANCE CORRECTION" => ImpedanceS,
            "MULTI-TERMINAL DC" or "MULTI TERMINAL DC" => MultiTerminalS,
            "MULTI-SECTION LINE" or "MULTI SECTION LINE" or "MULTI-SECTION LINE GROUPING" => MultiSectionS,
            "ZONE" => ZoneS,
            "INTER-AREA TRANSFER" or "INTERAREA TRANSFER" => InterAreaS,
            "OWNER" => OwnerS,
            "FACTS DEVICE" or "FACTS CONTROL DEVICE" or "FACTS" => FactsS,
            "SWITCHED SHUNT" => SwitchedShuntS,
            "GNE DEVICE" or "GNE" => GneS,
            "INDUCTION MACHINE" => InductionS,
            "SUBSTATION" => SubstationS,
            _ => null,
        };
    }

    /// <summary>The whole read of one file. A class (not a pile of statics) so the many lists stay together.</summary>
    private sealed class Reader
    {
        private readonly string[] _lines;
        private readonly string? _name;
        private readonly PsseReadOptions _options;
        private readonly List<string> _notes = new();

        private int _rev;
        private double _sbase;

        // Bus record field positions: revision 30 carries GL and BL at 5 and 6 (1-based) and the owner last.
        private bool V30 => _rev == 30;
        private int BusArea => V30 ? 6 : 4;
        private int BusZone => V30 ? 7 : 5;
        private int BusOwner => V30 ? 10 : 6;
        private int BusVm => V30 ? 8 : 7;
        private int BusVa => V30 ? 9 : 8;
        private PsseBusMeta MetaOf(PsseRecord bus) => new(bus.Int(BusArea, "AREA", 1), bus.Int(BusZone, "ZONE", 1), bus.Int(BusOwner, "OWNER", 1));

        // Records of the sections that build the model, in file order.
        private readonly List<PsseRecord> _buses = new(), _loads = new(), _fixedShunts = new(), _gens = new(),
            _branches = new(), _switching = new(), _areas = new(), _zones = new(), _owners = new(), _switchedShunts = new();
        private readonly List<PsseRecord[]> _transformers = new();
        private readonly List<PsseRecord[]> _twoTerminal = new();   // two-terminal DC: the link line, rectifier, inverter
        private readonly List<PsseRecord[]> _vsc = new();           // VSC DC: the link line, the two converters
        private readonly List<PsseRecord[]> _mtdc = new();          // multi-terminal DC: the link line, converters, DC buses, DC lines
        private readonly List<PsseRecord> _facts = new();           // FACTS devices
        private readonly List<PsseRecord> _induction = new();       // in-service induction machines

        // Sections read past: in-service and out-of-service device counts with the first line of each, by kind.
        private readonly Dictionary<string, (int InService, int Out, int FirstLine)> _unmodelled = new(StringComparer.Ordinal);
        // Impedance correction tables by number: the record (for messages) and the points, T strictly increasing.
        private readonly Dictionary<int, (PsseRecord Record, ImpedanceCorrectionPoint[] Points)> _impedanceTables = new();
        private int _multiSection, _interArea, _systemWide, _substations;

        public Reader(string[] lines, string? name, PsseReadOptions options)
        {
            _lines = lines;
            _name = name;
            _options = options;
        }

        /// <summary>
        /// The regulating bank for one in-service switched shunt (<see cref="PsseReadOptions.ApplyShuntControl"/>), or null
        /// with the reason it stays fixed (null reason: locked, MODSW 0, which is not noted). Applied: MODSW 1 (discrete
        /// voltage control) of its own bus (SWREM / SWREG 0 or the shunt's bus), band VSWLO..VSWHI, blocks (Ni, Bi) all of
        /// one size |B| (reactor blocks negative, capacitor positive; a revision 35 block with status 0 is left out), and a
        /// BINIT that is a whole number of blocks inside their range. The bank: step |B| MVAr, steps counted from BINIT
        /// (initial 0, from -(reactor blocks) - k to (capacitor blocks) - k for BINIT = k |B|). A band that cannot make
        /// voltage control (VSWHI not above VSWLO, or VSWLO not above 0) and a negative block count are refused with the
        /// line.
        /// <para>Field positions: revision 30 I, MODSW, VSWHI, VSWLO, SWREM, RMPCT, RMIDNT, BINIT, then N1, B1, ...;
        /// revisions 33 and 34 add ADJM and STAT after MODSW; revision 35 has I, ID, MODSW, ADJM, STAT, VSWHI, VSWLO, SWREG,
        /// NREG, RMPCT, RMIDNT, BINIT, then S1, N1, B1, ....</para>
        /// </summary>
        private (SwitchedShunt? Bank, string? Why) ShuntBank(PsseRecord r, int bus, int modsw, double binit, Func<int, string?> remoteWhy)
        {
            if (modsw == 0) return (null, null);
            if (modsw != 1) return (null, "have a control mode other than voltage control (MODSW 3 to 6)");
            bool v35 = _rev >= 35;
            int hiIdx = V30 ? 2 : v35 ? 5 : 4, remIdx = V30 ? 4 : v35 ? 7 : 6, blockIdx = V30 ? 8 : v35 ? 12 : 10;
            int rem = r.Int(remIdx, v35 ? "SWREG" : "SWREM", 0);
            if (rem != 0 && rem != bus && remoteWhy(rem) is string rw) return (null, rw);
            double hi = r.Real(hiIdx, "VSWHI", 1.0), lo = r.Real(hiIdx + 1, "VSWLO", 1.0);
            if (!(hi > lo) || !(lo > 0))
                throw r.Error($"VSWHI {hi.ToString("R", CultureInfo.InvariantCulture)} and VSWLO {lo.ToString("R", CultureInfo.InvariantCulture)} cannot make voltage control: VSWHI must be above VSWLO, and VSWLO above 0");
            int width = v35 ? 3 : 2, caps = 0, reactors = 0;
            double size = 0;
            bool mixed = false;
            var capBlocks = new List<double>();   // one entry per unit, in the order listed (PSS/E switches them in that order)
            var reactorBlocks = new List<double>();
            for (int k = 0, i = blockIdx; k < 8 && r.Has(i); k++, i += width)
            {
                if (v35 && r.Int(i, $"S{k + 1}", 1) == 0) continue;
                int n = r.Int(v35 ? i + 1 : i, $"N{k + 1}", 0);
                double b = r.Real(v35 ? i + 2 : i + 1, $"B{k + 1}", 0);
                if (n < 0) throw r.Error($"N{k + 1} is {n}; a block count must be 0 or more");
                if (n == 0 || b == 0) continue;
                if (size == 0) size = Math.Abs(b);
                else if (Math.Abs(b) != size) mixed = true;
                if (b > 0) caps += n; else reactors += n;
                for (int u = 0; u < n; u++) (b > 0 ? capBlocks : reactorBlocks).Add(b);
            }
            if (size == 0) return (null, "have no switchable blocks");
            string id = v35 ? r.Str(1) : "";
            string name = id.Length > 0 ? $"switched shunt {bus} {id}" : $"switched shunt {bus}";
            int? controls = rem != 0 && rem != bus ? rem : null;
            if (mixed)
            {
                // Unequal blocks: the levels from every reactor block in to every capacitor block in, one block at a time in
                // the order listed; BINIT must be one of them, and the bank's steps count from it.
                var levels = new List<double>();
                double acc = 0;
                foreach (double b in reactorBlocks) { acc += b; levels.Add(acc); }
                levels.Reverse();
                levels.Add(0);
                acc = 0;
                foreach (double b in capBlocks) { acc += b; levels.Add(acc); }
                int pos = -1;
                for (int k = 0; k < levels.Count; k++)
                    if (Math.Abs(levels[k] - binit) <= 1e-9 * Math.Max(1.0, Math.Abs(binit))) { pos = k; break; }
                if (binit > levels[^1] + 1e-9 * Math.Max(1.0, Math.Abs(binit)) || binit < levels[0] - 1e-9 * Math.Max(1.0, Math.Abs(binit)))
                    return (null, "have a BINIT outside the range their blocks reach");
                if (pos < 0) return (null, "have a BINIT that is not a whole number of blocks");
                var rel = levels.Select(x => x - levels[pos]).ToArray();
                rel[pos] = 0;
                return (new SwitchedShunt(bus, 1.0, -pos, levels.Count - 1 - pos, 0, lo, hi, name) { ControlsBus = controls, StepLevelsMvar = rel }, null);
            }
            double steps = binit / size, whole = Math.Round(steps);
            if (Math.Abs(steps - whole) > 1e-9 * Math.Max(1.0, Math.Abs(steps)))
                return (null, "have a BINIT that is not a whole number of blocks");
            if (whole > caps || whole < -reactors) return (null, "have a BINIT outside the range their blocks reach");
            int at = (int)whole;
            return (new SwitchedShunt(bus, size, -reactors - at, caps - at, 0, lo, hi, name) { ControlsBus = controls }, null);
        }

        /// <summary>
        /// The static var compensator for one in-service switched shunt with continuous voltage control (MODSW 2), read only
        /// with <see cref="PsseReadOptions.ApplyShuntControl"/>, or null with the reason it stays fixed at BINIT. Its range
        /// runs from every reactor block in (the sum of the negative Ni Bi) to every capacitor block in (the positive);
        /// block sizes may differ (the susceptance varies continuously). It regulates its own bus (SWREM / SWREG 0 or the
        /// shunt's bus) in VSWLO..VSWHI (equal for a fixed set-point). A band with VSWLO not above 0 or VSWHI below VSWLO and
        /// a negative block count are refused with the line.
        /// </summary>
        private (StaticVarCompensator? Svc, string? Why) Svc(PsseRecord r, int bus, double binit)
        {
            bool v35 = _rev >= 35;
            int hiIdx = V30 ? 2 : v35 ? 5 : 4, remIdx = V30 ? 4 : v35 ? 7 : 6, blockIdx = V30 ? 8 : v35 ? 12 : 10;
            double hi = r.Real(hiIdx, "VSWHI", 1.0), lo = r.Real(hiIdx + 1, "VSWLO", 1.0);
            if (!(hi >= lo) || !(lo > 0))
                throw r.Error($"VSWHI {hi.ToString("R", CultureInfo.InvariantCulture)} and VSWLO {lo.ToString("R", CultureInfo.InvariantCulture)} cannot make continuous voltage control: VSWHI must be at or above VSWLO, and VSWLO above 0");
            int rem = r.Int(remIdx, v35 ? "SWREG" : "SWREM", 0);
            if (rem != 0 && rem != bus) return (null, "with continuous voltage control (MODSW 2) regulate a remote bus (SWREM)");
            int width = v35 ? 3 : 2;
            double bmin = 0, bmax = 0;
            for (int k = 0, i = blockIdx; k < 8 && r.Has(i); k++, i += width)
            {
                if (v35 && r.Int(i, $"S{k + 1}", 1) == 0) continue;
                int n = r.Int(v35 ? i + 1 : i, $"N{k + 1}", 0);
                double b = r.Real(v35 ? i + 2 : i + 1, $"B{k + 1}", 0);
                if (n < 0) throw r.Error($"N{k + 1} is {n}; a block count must be 0 or more");
                if (b > 0) bmax += n * b; else bmin += n * b;
            }
            if (bmin == 0 && bmax == 0) return (null, "with continuous voltage control (MODSW 2) have no blocks");
            if (binit < bmin || binit > bmax) return (null, "with continuous voltage control (MODSW 2) have a BINIT outside the range their blocks reach");
            string id = v35 ? r.Str(1) : "";
            // RMPCT follows SWREM in revisions 30 to 34 and NREG (after SWREG) in revision 35; used only when another device
            // holds the same bus.
            double rmpct = r.Real(v35 ? remIdx + 2 : remIdx + 1, "RMPCT", 100);
            return (new StaticVarCompensator(bus, bmin, bmax, binit, lo, hi, id.Length > 0 ? $"SVC {bus} {id}" : $"SVC {bus}") { RmpctPercent = rmpct }, null);
        }

        /// <summary>One winding's voltage control (COD 1), read only with <see cref="PsseReadOptions.ApplyTapControl"/>. The
        /// ratio limits are in pu of the winding's bus base (the units of the winding's ratio in the branch tap); the band
        /// is in pu of the controlled bus's base.</summary>
        private sealed record TapControl(PsseRecord Record, int Winding, int Cont, double RmaPu, double RmiPu, double VmaPu, double VmiPu, int Ntp)
        {
            /// <summary>The control code: 1 voltage (the fields as named), 2 reactive power flow (VMA, VMI in Mvar), 3 active
            /// power flow (VMA, VMI in MW; RMA, RMI the phase shift angle limits in degrees).</summary>
            public int Cod { get; init; } = 1;

            /// <summary>The winding's impedance correction table as the tap loop applies it, when it has one.</summary>
            public TapImpedanceCorrection? Correction { get; init; }
        }

        /// <summary>
        /// Read winding <paramref name="n"/>'s COD 1 fields: CONT (the controlled bus, a number; the sign says which side of
        /// the winding a remote bus is on), RMA and RMI (ratio limits, in the units of WINDV: pu of the bus base for CW 1, kV
        /// for CW 2, pu of NOMV for CW 3; PSS/E's defaults 1.1 and 0.9 are taken as pu), VMA and VMI (the voltage band, pu;
        /// defaults 1.1 and 0.9) and NTP (tap positions; default 33). Their positions are fixed back from TAB (NTP is the
        /// field before it) in every supported revision. Data that cannot make a tap changer is refused with its line: fewer
        /// than 2 positions, RMA not above RMI, or a band that is empty or not above 0 pu.
        /// </summary>
        private static TapControl ReadTapControl(PsseRecord wr, int n, int cw, double busKv, double nomvKv, int codIdx, int tabIdx)
        {
            int w = n + 1;
            int cont = wr.Int(codIdx + 1, $"CONT{w}", 0);
            double Pu(double v) => cw switch { 1 => v, 2 => v / busKv, _ => v * nomvKv / busKv };
            double rma = wr.Has(tabIdx - 5) ? Pu(wr.Real(tabIdx - 5, $"RMA{w}")) : 1.1;
            double rmi = wr.Has(tabIdx - 4) ? Pu(wr.Real(tabIdx - 4, $"RMI{w}")) : 0.9;
            double vma = wr.Real(tabIdx - 3, $"VMA{w}", 1.1);
            double vmi = wr.Real(tabIdx - 2, $"VMI{w}", 0.9);
            int ntp = wr.Int(tabIdx - 1, $"NTP{w}", 33);
            if (ntp < 2) throw wr.Error($"NTP{w} is {ntp}; a tap changer needs at least 2 tap positions");
            if (!(rma > rmi) || !(rmi > 0))
                throw wr.Error($"RMA{w} and RMI{w} give the ratio range {R(rmi)}..{R(rma)} pu; a tap changer needs RMA above RMI, both above 0");
            if (!(vma > vmi) || !(vmi > 0))
                throw wr.Error($"VMA{w} and VMI{w} give the voltage band {R(vmi)}..{R(vma)} pu; voltage control needs VMA above VMI, both above 0");
            return new TapControl(wr, w, cont, rma, rmi, vma, vmi, ntp);

            static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Winding <paramref name="n"/>'s flow control (COD 2 or 3), read only with <see cref="PsseReadOptions.ApplyTapControl"/>:
        /// VMA and VMI are the band of the reactive (COD 2, Mvar) or active (COD 3, MW) power flowing into the transformer at
        /// the winding's bus; RMA and RMI the ratio limits (COD 2, in the units of WINDV, as for COD 1) or the phase shift
        /// angle limits (COD 3, degrees); NTP the positions. Refused with the line: fewer than 2 positions, RMA not above RMI
        /// (or RMI not above 0 for a ratio), VMA not above VMI.
        /// </summary>
        private static TapControl ReadFlowControl(PsseRecord wr, int n, int cw, double busKv, double nomvKv, int cod, int tabIdx)
        {
            int w = n + 1;
            double Pu(double v) => cw switch { 1 => v, 2 => v / busKv, _ => v * nomvKv / busKv };
            double rmaRaw = wr.Real(tabIdx - 5, $"RMA{w}", cod == 3 ? 30 : 1.1), rmiRaw = wr.Real(tabIdx - 4, $"RMI{w}", cod == 3 ? -30 : 0.9);
            double rma = cod == 3 ? rmaRaw : Pu(rmaRaw), rmi = cod == 3 ? rmiRaw : Pu(rmiRaw);
            double vma = wr.Real(tabIdx - 3, $"VMA{w}"), vmi = wr.Real(tabIdx - 2, $"VMI{w}");
            int ntp = wr.Int(tabIdx - 1, $"NTP{w}", 33);
            string what = cod == 3 ? "phase shift angle" : "ratio";
            if (ntp < 2) throw wr.Error($"NTP{w} is {ntp}; a {(cod == 3 ? "phase shifter" : "tap changer")} needs at least 2 positions");
            if (!(rma > rmi) || (cod == 2 && !(rmi > 0)))
                throw wr.Error($"RMA{w} and RMI{w} give the {what} range {R(rmi)}..{R(rma)}; flow control needs RMA above RMI{(cod == 2 ? ", both above 0" : "")}");
            if (!(vma > vmi))
                throw wr.Error($"VMA{w} and VMI{w} give the {(cod == 3 ? "active" : "reactive")} power band {R(vmi)}..{R(vma)} {(cod == 3 ? "MW" : "Mvar")}; flow control needs VMA above VMI");
            return new TapControl(wr, w, 0, rma, rmi, vma, vmi, ntp) { Cod = cod };

            static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The <see cref="Oltc"/> records for the controlled windings (<see cref="PsseReadOptions.ApplyTapControl"/>), in
        /// branch order, with notes. Each record: the controlled bus |CONT|; target (VMA + VMI) / 2 and deadband
        /// (VMA - VMI) / 2; limits RMI and RMA and step (RMA - RMI) / (NTP - 1), each divided by <c>Scale</c> (winding 2's
        /// ratio for a two-winding unit, whose branch tap is w1 / w2; 1 for a three-winding unit's winding branch).
        /// <list type="bullet">
        /// <item>CONT 0, or a controlled bus that is a dropped isolated bus: no tap changer (that tap stays at the file's
        /// ratio), noted. A CONT naming a bus the file does not define is refused.</item>
        /// <item>A negative CONT naming a bus other than the winding's own: refused. PSS/E then regulates the bus as if it
        /// were on the winding 1 side; the tap loop steps a bus that is on neither end of the branch as one on the far
        /// side, so it would step the wrong way.</item>
        /// <item>A tap changer finds its transformer by branch name, so a controlled branch with no name, or with a name
        /// another branch shares (parallel units often do), is given one of its own: "transformer I-J CKT" or
        /// "NAME (I-J CKT)" (with K and the winding for a three-winding unit). Noted.</item>
        /// </list>
        /// </summary>
        private List<Oltc> TapChangers(List<(int Index, TapControl Control, double Scale, int OwnBus, string Key)> controlled,
            List<Branch> branches, Func<int, bool> defined, Func<int, bool> live, List<TransformerFlowControl> flows)
        {
            var oltcs = new List<Oltc>(controlled.Count);
            var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Branch b in branches)
                if (b.Name is string nm) nameCount[nm] = nameCount.GetValueOrDefault(nm) + 1;
            int renamed = 0, noBus = 0, deadBus = 0, outside = 0;
            foreach ((int index, TapControl t, double scale, int ownBus, string key) in controlled)
            {
                if (t.Cod is 2 or 3)
                {
                    // Flow control: the transformer's own flow, no controlled bus. It needs a name of its own, as below.
                    Branch fb = branches[index];
                    string fname;
                    if (fb.Name is null || nameCount[fb.Name] > 1)
                    {
                        fname = fb.Name is null ? $"transformer {key}" : $"{fb.Name} ({key})";
                        if (nameCount.ContainsKey(fname)) throw t.Record.Error($"this flow-controlling transformer needs a name of its own and '{fname}' is already a branch's name");
                        nameCount[fname] = 1;
                        branches[index] = fb with { Name = fname };
                        renamed++;
                    }
                    else fname = fb.Name;
                    double fs = t.Cod == 2 ? scale : 1.0;
                    var kind = t.Cod == 2 ? TransformerFlowKind.ReactivePower : TransformerFlowKind.ActivePower;
                    double at = t.Cod == 2 ? fb.TapRatio : fb.PhaseShiftDeg;
                    if (at * fs < t.RmiPu || at * fs > t.RmaPu) outside++;
                    flows.Add(new TransformerFlowControl(fname, fb.From, kind, t.VmiPu, t.VmaPu, t.RmiPu / fs, t.RmaPu / fs, (t.RmaPu - t.RmiPu) / (t.Ntp - 1) / fs)
                        { ImpedanceCorrection = t.Correction });
                    continue;
                }
                int bus = Math.Abs(t.Cont);
                if (bus == 0) { noBus++; continue; }
                if (!defined(bus)) throw t.Record.Error($"CONT{t.Winding} names bus {bus}, which the file does not define");
                if (!live(bus)) { deadBus++; continue; }
                if (t.Cont < 0 && bus != ownBus)
                    throw t.Record.Error($"CONT{t.Winding} is {t.Cont}, a remote controlled bus taken as on the winding 1 side; GridSim's tap loop steps a remote bus as one on the far side of the winding, so it cannot regulate this one. Read the case without tap control to keep the file's ratios");
                // Bug hunt 2026-10-29, finding 46: the mirror case. A POSITIVE CONT is regulated by PSS/E as if on the
                // winding 2 (far) side; naming the winding's own bus that way asks PSS/E to step the tap in the far-side
                // sense, but GridSim's loop steps its own bus in the near-side sense, i.e. the opposite way. It used to
                // be passed through unchecked; it is refused like the negative remote case above.
                if (t.Cont > 0 && bus == ownBus)
                    throw t.Record.Error($"CONT{t.Winding} is {t.Cont}, the winding's own bus given as positive, which PSS/E regulates as if on the winding 2 side; GridSim's tap loop steps its own bus as the near side, the opposite sense, so it cannot regulate it as the file means. Give CONT as -{bus} for near-side control, or read the case without tap control to keep the file's ratios");
                Branch br = branches[index];
                string name;
                if (br.Name is null || nameCount[br.Name] > 1)
                {
                    name = br.Name is null ? $"transformer {key}" : $"{br.Name} ({key})";
                    if (nameCount.ContainsKey(name)) throw t.Record.Error($"this tap-changing transformer needs a name of its own and '{name}' is already a branch's name");
                    nameCount[name] = 1;
                    branches[index] = br with { Name = name };
                    renamed++;
                }
                else name = br.Name;
                double min = t.RmiPu / scale, max = t.RmaPu / scale, step = (t.RmaPu - t.RmiPu) / (t.Ntp - 1) / scale;
                if (br.TapRatio < min || br.TapRatio > max) outside++;
                oltcs.Add(new Oltc(name, bus, TargetVpu: (t.VmaPu + t.VmiPu) / 2, MinTap: min, MaxTap: max, StepPu: step,
                    DeadbandPu: (t.VmaPu - t.VmiPu) / 2) { ImpedanceCorrection = t.Correction });
            }
            if (oltcs.Count > 0)
                _notes.Add($"{oltcs.Count} transformer winding(s) regulate a bus voltage by tap (COD 1): applied as on-load tap changers that the solve's tap loop steps from the file's ratios (band VMI..VMA, limits RMI..RMA, step (RMA - RMI) / (NTP - 1)).");
            int nq = flows.Count(f => f.Kind == TransformerFlowKind.ReactivePower), np = flows.Count - nq;
            if (flows.Count > 0)
                _notes.Add($"{flows.Count} transformer winding(s) regulate the power flowing through them ({nq} reactive power by tap, COD 2; {np} active power by phase shift, COD 3): applied in the solve's tap loop, one position per pass from the file's values until the flow into the transformer at the winding's bus is in VMI..VMA (limits RMI..RMA, step (RMA - RMI) / (NTP - 1)).");
            if (renamed > 0) _notes.Add($"{renamed} tap-changing transformer(s) with no name, or a name another branch shares, were given a name of their own (a tap changer names its transformer): 'NAME (I-J CKT)' or 'transformer I-J CKT'.");
            if (noBus > 0) _notes.Add($"{noBus} winding(s) have voltage control (COD 1) but no controlled bus (CONT 0); their taps stay at the file's ratios.");
            if (deadBus > 0) _notes.Add($"{deadBus} winding(s) control the voltage of a dropped isolated (IDE 4) bus; their taps stay at the file's ratios.");
            if (outside > 0) _notes.Add($"{outside} tap-changing winding(s) start outside their RMI..RMA range; the tap loop never steps such a tap further out, and reports it at its limit if its bus stays out of band.");
            return oltcs;
        }

        public PsseParseResult Read()
        {
            (int start, string[] titles, string caseName, double freq) = ReadHeader();
            ReadSections(start);
            return Build(titles, caseName, freq);
        }

        // ---- Case identification -------------------------------------------------------------------------------

        private (int Start, string[] Titles, string CaseName, double FrequencyHz) ReadHeader()
        {
            int h = 0;
            while (h < _lines.Length && (_lines[h].Trim().Length == 0 || PsseText.IsHeading(_lines[h]))) h++;
            if (h >= _lines.Length) throw new InvalidDataException("PSS/E RAW text has no case identification line.");
            (List<string?> f, string? comment) = PsseText.Split(_lines[h], h + 1, "CASE IDENTIFICATION");
            var id = new PsseRecord(f, h + 1, "CASE IDENTIFICATION", comment);

            int ic = id.Int(0, "IC", 0);
            if (ic != 0)
                throw id.Error($"IC is {ic}: this is a change case (data to add to a case already in memory), not a whole network; only IC 0 can be read");
            _sbase = id.Real(1, "SBASE", 100.0);
            if (!(_sbase > 0)) throw id.Error($"SBASE is {_sbase}; the system base must be above 0 MVA");

            // The revision: the REV field, else "PSS/E-NN" (or PSS(tm)E-NN, PSS(R)E-NN) in the comment, else the caller's.
            int? fileRev = id.Has(2) ? id.Int(2, "REV") : null;
            if (fileRev == 0) fileRev = null;
            if (fileRev is null && comment is not null)
            {
                Match m = Regex.Match(comment, @"PSS(?:/|\(tm\)|\(TM\)|\(R\)|\u00AE)?E-(\d+)");
                if (m.Success) fileRev = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            if (fileRev is int fr && _options.Revision is int asked && fr != asked)
                throw id.Error($"the file says revision {fr} but revision {asked} was asked for; remove one of the two");
            _rev = fileRev ?? _options.Revision
                ?? throw id.Error("the file does not say its PSS/E revision (no REV field and no PSS/E-NN in the comment); give the revision when reading it");
            if (!SupportedRevisions.Contains(_rev))
                throw id.Error(NotSupported(_rev));

            double basfrq = id.Real(5, "BASFRQ", 0.0);
            if (basfrq < 0) throw id.Error($"BASFRQ is {basfrq}; the base frequency must be 0 (not given) or above");
            double freq;
            if (basfrq > 0)
            {
                if (_options.FrequencyHz is double given && given != basfrq)
                    throw id.Error($"the file's base frequency is {basfrq.ToString("R", CultureInfo.InvariantCulture)} Hz but {given.ToString("R", CultureInfo.InvariantCulture)} Hz was asked for; remove one of the two");
                freq = basfrq;
            }
            else if (_options.FrequencyHz is double given) freq = given;
            else
            {
                freq = AssumedFrequencyHz;
                _notes.Add(FormattableString.Invariant($"base frequency not given (BASFRQ 0 or missing); assumed {AssumedFrequencyHz:0.##} Hz, PSS/E's default - pass the real value if this is, for example, a 50 Hz system (gridsim: --frequency-hz 50)."));
            }

            // The two title lines follow, taken as written (they may be blank); heading lines are skipped.
            var titles = new string[2];
            int at = h + 1;
            for (int k = 0; k < 2; k++)
            {
                while (at < _lines.Length && PsseText.IsHeading(_lines[at])) at++;
                if (at >= _lines.Length) throw id.Error("the file ends before the two title lines");
                titles[k] = _lines[at].Trim();
                at++;
            }
            string caseName = _name ?? (titles[0].Length > 0 ? titles[0] : "psse");
            return (at, titles, caseName, freq);
        }

        // ---- Sections --------------------------------------------------------------------------------------------

        private void ReadSections(int start)
        {
            IReadOnlyList<string> sections = SectionsFor(_rev);
            var cur = new PsseLineCursor(_lines, start);
            bool ended = false, sawQ = false;
            for (int si = 0; si < sections.Count && !ended; si++)
            {
                string key = sections[si];
                string? next = si + 1 < sections.Count ? sections[si + 1] : null;
                while (true)
                {
                    PsseRecord? r = cur.Next(key);
                    if (r is null) { ended = true; break; }
                    if (r.IsEndOfData) { ended = true; sawQ = true; break; }
                    if (r.IsTerminator) { CheckLabel(r, key, next); break; }
                    // Revision 35: the system-wide records are keyword lines. A file that starts its bus data straight
                    // away (no system-wide section and no terminator for it) is read as such.
                    if (key == SystemWide && !char.IsLetter(r.Raw(0)?[0] ?? ' '))
                    {
                        cur = new PsseLineCursor(_lines, r.Line - 1);
                        break;
                    }
                    ReadRecord(key, r, cur);
                }
            }
            if (!ended)
            {
                // Every section was read: the end-of-data record (or the end of the text) must come next.
                PsseRecord? after = cur.Next("END OF DATA");
                if (after is not null && !after.IsEndOfData)
                    throw after.Error("a record follows the last section; the file should end with Q");
                sawQ = after is not null;
            }
            if (!sawQ) _notes.Add("the file has no Q end-of-data record; read to the end of the text.");
        }

        /// <summary>A terminator's "END OF x DATA, BEGIN y DATA" comment must agree with the section order when it names
        /// sections this reader knows; a disagreement means the file is not laid out as its revision says.</summary>
        private static void CheckLabel(PsseRecord r, string key, string? next)
        {
            if (r.Comment is null) return;
            string c = r.Comment.ToUpperInvariant();
            Match end = Regex.Match(c, @"END OF (.+?) DATA");
            if (end.Success && SectionOfLabel(end.Groups[1].Value) is string named && named != key)
                throw r.Error($"this terminator says it ends {end.Groups[1].Value.Trim()} data, but the {key} section was being read; the sections are not in the order of the file's revision");
            Match begin = Regex.Match(c, @"BEGIN (.+?) DATA");
            if (begin.Success && SectionOfLabel(begin.Groups[1].Value) is string nextNamed && nextNamed != next)
                throw r.Error($"this terminator says {begin.Groups[1].Value.Trim()} data comes next, but the file's revision puts {next ?? "the end of the file"} next");
        }

        private void ReadRecord(string key, PsseRecord r, PsseLineCursor cur)
        {
            switch (key)
            {
                case SystemWide: _systemWide++; break;
                case BusS: _buses.Add(r); break;
                case LoadS: _loads.Add(r); break;
                case FixedShuntS: _fixedShunts.Add(r); break;
                case GeneratorS: _gens.Add(r); break;
                case BranchS: _branches.Add(r); break;
                case SwitchingS: _switching.Add(r); break;
                case TransformerS:
                {
                    bool three = r.Int(2, "K", 0) != 0;
                    var rec = new PsseRecord[three ? 5 : 4];
                    rec[0] = r;
                    rec[1] = cur.Continuation(key, r, "the impedance line");
                    rec[2] = cur.Continuation(key, r, "the winding 1 line");
                    rec[3] = cur.Continuation(key, r, "the winding 2 line");
                    if (three) rec[4] = cur.Continuation(key, r, "the winding 3 line");
                    _transformers.Add(rec);
                    break;
                }
                case AreaS: _areas.Add(r); break;
                case TwoTerminalS:
                    _twoTerminal.Add(new[] { r, cur.Continuation(key, r, "the rectifier line"), cur.Continuation(key, r, "the inverter line") });
                    break;
                case VscS:
                    _vsc.Add(new[] { r, cur.Continuation(key, r, "the first converter line"), cur.Continuation(key, r, "the second converter line") });
                    break;
                case ImpedanceS:
                {
                    int number = r.Int(0, "I");
                    if (number < 1) throw r.Error($"impedance correction table number {number} must be 1 or above");
                    if (_impedanceTables.TryGetValue(number, out var prior)) throw r.Error($"impedance correction table {number} is defined twice (also at line {prior.Record.Line})");
                    _impedanceTables[number] = (r, _rev >= 34 ? ReadComplexTable(r, cur) : ReadRealTable(r));
                    break;
                }
                case MultiTerminalS:
                {
                    int lines = r.Int(1, "NCONV") + r.Int(2, "NDCBS") + r.Int(3, "NDCLN");
                    if (lines < 0) throw r.Error("NCONV, NDCBS and NDCLN must not be negative");
                    var rec = new PsseRecord[lines + 1];
                    rec[0] = r;
                    for (int k = 0; k < lines; k++) rec[k + 1] = cur.Continuation(key, r, $"line {k + 2} of the multi-terminal DC record");
                    _mtdc.Add(rec);
                    break;
                }
                case MultiSectionS: _multiSection++; break;
                case ZoneS: _zones.Add(r); break;
                case InterAreaS: _interArea++; break;
                case OwnerS: _owners.Add(r); break;
                case FactsS:
                    if (r.Int(3, "MODE", 1) == 0) Count("FACTS device", r, false);   // out of service: counted, as before
                    else _facts.Add(r);
                    break;
                case SwitchedShuntS: _switchedShunts.Add(r); break;
                case GneS:
                    throw r.Error("GNE devices are user-written models that GridSim cannot represent; remove the GNE device data to read the rest of the network");
                case InductionS:
                    if (r.Code(2, "STAT", 1, 0, 1) == 0) Count("induction machine", r, false);   // out of service: counted, as before
                    else _induction.Add(r);
                    break;
                case SubstationS:
                    // A substation record, then its node, switching device and terminal data, each ended by a 0 record.
                    _substations++;
                    for (int part = 0; part < 3; part++)
                    {
                        while (true)
                        {
                            PsseRecord inner = cur.Next(key) ?? throw r.Error("the file ends inside this substation's data");
                            if (inner.IsEndOfData) throw inner.Error("the end-of-data record comes inside a substation's data");
                            if (inner.IsTerminator) break;
                        }
                    }
                    break;
                default:
                    throw r.Error($"internal error: no reader for section {key}");
            }
        }

        /// <summary>Revision 33 and earlier impedance correction table: I, then up to eleven pairs (T, F) on one line, F a real
        /// factor; trailing pairs of zeros are unused. Revision 34 and 35: triples (T, Re F, Im F) over one or more lines,
        /// the table ended by the triple 0, 0, 0. At least two points, T strictly increasing, a factor that is not zero.</summary>
        private static ImpedanceCorrectionPoint[] ReadRealTable(PsseRecord r)
        {
            var pts = new List<ImpedanceCorrectionPoint>();
            for (int k = 1; k < r.Count; k += 2)
            {
                double t = r.Real(k, $"T{(k + 1) / 2}", 0), f = r.Real(k + 1, $"F{(k + 1) / 2}", 0);
                if (t == 0 && f == 0) continue;
                pts.Add(new ImpedanceCorrectionPoint(t, f, 0));
            }
            return CheckTable(r, pts);
        }

        private static ImpedanceCorrectionPoint[] ReadComplexTable(PsseRecord first, PsseLineCursor cur)
        {
            var pts = new List<ImpedanceCorrectionPoint>();
            bool Take(PsseRecord rec, int from)
            {
                for (int k = from; k + 2 < rec.Count; k += 3)
                {
                    int n = pts.Count + 1;
                    double t = rec.Real(k, $"T{n}", 0), re = rec.Real(k + 1, $"Re(F{n})", 0), im = rec.Real(k + 2, $"Im(F{n})", 0);
                    if (t == 0 && re == 0 && im == 0) return true;
                    pts.Add(new ImpedanceCorrectionPoint(t, re, im));
                }
                return false;
            }
            if (!Take(first, 1))
                while (!Take(cur.Continuation(ImpedanceS, first, "the end of the impedance correction table (0.0, 0.0, 0.0)"), 0)) { }
            return CheckTable(first, pts);
        }

        private static ImpedanceCorrectionPoint[] CheckTable(PsseRecord r, List<ImpedanceCorrectionPoint> pts)
        {
            if (pts.Count < 2) throw r.Error($"impedance correction table has {pts.Count} point(s); it needs at least 2");
            for (int k = 0; k < pts.Count; k++)
            {
                if (pts[k].ReF == 0 && pts[k].ImF == 0) throw r.Error($"impedance correction point {k + 1} (T {pts[k].T.ToString("R", CultureInfo.InvariantCulture)}) has a zero factor, which would give the transformer no impedance");
                if (k > 0 && !(pts[k].T > pts[k - 1].T))
                    throw r.Error($"impedance correction point {k + 1} has T {pts[k].T.ToString("R", CultureInfo.InvariantCulture)}, not above the previous point's {pts[k - 1].T.ToString("R", CultureInfo.InvariantCulture)}; T must increase");
            }
            return pts.ToArray();
        }

        private void Count(string kind, PsseRecord r, bool inService)
        {
            _unmodelled.TryGetValue(kind, out var c);
            _unmodelled[kind] = (c.InService + (inService ? 1 : 0), c.Out + (inService ? 0 : 1), c.FirstLine == 0 ? r.Line : c.FirstLine);
        }

        /// <summary>A two-terminal DC converter line: IP, NB, ANMX, ANMN, RC, XC, EBAS, TR, TAP, TMX, TMN, STP, IC, [ND in
        /// revision 35], IF, IT, ID, XCAP. Returns the converter and whether it is capacitor-commutated (XCAP not 0).</summary>
        private (LccConverter Converter, bool Ccc) Converter(PsseRecord c, string end, string amx, string amn)
        {
            int bus = c.Int(0, "IP");
            if (bus < 1) throw c.Error($"the {end} bus {bus} must be 1 or above");
            int nb = c.Int(1, "NB");
            if (nb < 1) throw c.Error($"the {end} has {nb} bridges; it needs at least 1");
            double anmx = c.Real(2, amx), anmn = c.Real(3, amn);
            if (!(anmn >= 0) || !(anmx > anmn) || !(anmx < 90))
                throw c.Error($"the {end} angle limits {amn} {anmn} .. {amx} {anmx} degrees must satisfy 0 <= min < max < 90");
            double rcv = c.Real(4, "RC", 0), xcv = c.Real(5, "XC");
            if (rcv < 0 || xcv < 0) throw c.Error($"the {end}'s commutating resistance and reactance must be 0 or above");
            double ebas = c.Real(6, "EBAS");
            if (!(ebas > 0)) throw c.Error($"the {end}'s EBAS is {ebas} kV; it must be above 0");
            double tr = c.Real(7, "TR", 1.0), tap = c.Real(8, "TAP", 1.0), tmx = c.Real(9, "TMX", 1.5), tmn = c.Real(10, "TMN", 0.51), stp = c.Real(11, "STP", 0.00625);
            if (!(tr > 0) || !(tap > 0) || !(tmn > 0) || !(tmx >= tmn) || stp < 0)
                throw c.Error($"the {end}'s transformer ratio {tr}, tap {tap} and limits {tmn} .. {tmx} (step {stp}) must be positive with TMX at or above TMN");
            int xcapIdx = _rev >= 35 ? 17 : 16;
            double xcap = c.Real(xcapIdx, "XCAP", 0);
            return (new LccConverter(bus, nb, anmx, anmn, rcv, xcv, ebas, tr, tap, tmx, tmn, stp), xcap != 0);
        }

        /// <summary>A VSC converter line: IBUS, TYPE, MODE, DCSET, ACSET, ALOSS, BLOSS, MINLOSS, SMAX, IMAX, PWF, MAXQ, MINQ,
        /// REMOT (VSREG in revision 35), [NREG in revision 35], RMPCT. Returns the converter and whether it regulates a
        /// bus other than its own.</summary>
        private static (VscConverter? Converter, bool Remote) Vsc(PsseRecord c, string link)
        {
            int bus = c.Int(0, "IBUS");
            if (bus < 1) throw c.Error($"VSC link '{link}': converter bus {bus} must be 1 or above");
            int type = c.Code(1, "TYPE", 1, 1, 2), mode = c.Code(2, "MODE", 1, 1, 2);
            double dcset = c.Real(3, "DCSET"), acset = c.Real(4, "ACSET", 1.0);
            double aloss = c.Real(5, "ALOSS", 0), bloss = c.Real(6, "BLOSS", 0), minloss = c.Real(7, "MINLOSS", 0);
            double smax = c.Real(8, "SMAX", 0), imax = c.Real(9, "IMAX", 0), pwf = c.Real(10, "PWF", 1.0);
            double maxq = c.Real(11, "MAXQ", 9999), minq = c.Real(12, "MINQ", -9999);
            int remot = c.Int(13, "REMOT", 0);
            if (type == 1 && !(dcset > 0)) throw c.Error($"DCSET is {dcset}; a DC voltage converter needs a scheduled DC voltage above 0 kV");
            if (mode == 1 && !(acset > 0)) throw c.Error($"ACSET is {acset}; an AC voltage set-point must be above 0 pu");
            if (mode == 2 && (!(Math.Abs(acset) > 0) || Math.Abs(acset) > 1)) throw c.Error($"ACSET is {acset}; a power factor must be in -1..1 and not 0");
            if (aloss < 0 || bloss < 0 || minloss < 0) throw c.Error("ALOSS, BLOSS and MINLOSS must be 0 or above");
            if (smax < 0 || imax < 0) throw c.Error("SMAX and IMAX must be 0 (no limit) or above");
            if (pwf < 0 || pwf > 1) throw c.Error($"PWF is {pwf}; the power weighting fraction must be in 0..1");
            if (maxq < minq) throw c.Error($"MAXQ {maxq} is below MINQ {minq}");
            if (remot != 0 && remot != bus) return (null, true);
            return (new VscConverter(bus, type == 1 ? VscDcControl.DcVoltage : VscDcControl.Power, mode == 1 ? VscAcControl.Voltage : VscAcControl.PowerFactor,
                dcset, acset, aloss, bloss, minloss, smax, imax, pwf, maxq, minq), false);
        }

        // ---- Model ----------------------------------------------------------------------------------------------

        private sealed class BusAcc
        {
            public double? Pl, Ql, Ip, Iq, Gs, Bs;
            public static void Add(ref double? acc, double v) => acc = acc is double a ? a + v : v;
        }

        private PsseParseResult Build(string[] titles, string caseName, double freq)
        {
            // --- Buses ---------------------------------------------------------------------------------------------
            var busRec = new Dictionary<int, PsseRecord>();
            var isolated = new HashSet<int>();
            var order = new List<int>();
            var meta = new Dictionary<int, PsseBusMeta>();
            foreach (PsseRecord r in _buses)
            {
                int id = r.Int(0, "I");
                if (id < 1) throw r.Error($"bus number {id} must be 1 or above");
                if (busRec.TryGetValue(id, out PsseRecord? prior))
                    throw r.Error($"bus {id} is defined twice (also at line {prior.Line})");
                busRec[id] = r;
                int ide = r.Code(3, "IDE", 1, 1, 2, 3, 4);
                if (ide == 4) isolated.Add(id);
                order.Add(id);
            }
            if (busRec.Count == 0) throw new InvalidDataException("PSS/E RAW file has no bus data.");
            bool Live(int id) => busRec.ContainsKey(id) && !isolated.Contains(id);
            PsseRecord BusOf(PsseRecord r, int id, string field)
                => busRec.TryGetValue(id, out PsseRecord? b) ? b : throw r.Error($"{field} {id} is not a bus of this file");

            var acc = new Dictionary<int, BusAcc>();
            BusAcc Acc(int id) => acc.TryGetValue(id, out BusAcc? a) ? a : acc[id] = new BusAcc();

            // --- Revision 30: the bus record's own shunt (GL, BL: MW and MVAr at 1 pu), first, as a fixed shunt is --
            int busShunts = 0;
            if (V30)
                foreach (PsseRecord r in _buses)
                {
                    int id = r.Int(0, "I");
                    double gl = r.Real(4, "GL", 0), bl = r.Real(5, "BL", 0);
                    if (isolated.Contains(id) || (gl == 0 && bl == 0)) continue;
                    BusAcc a = Acc(id);
                    BusAcc.Add(ref a.Gs, gl);
                    BusAcc.Add(ref a.Bs, bl);
                    busShunts++;
                }
            if (busShunts > 0) _notes.Add($"{busShunts} bus record(s) carry a shunt (GL, BL; revision 30); it is the bus shunt, as a fixed shunt is.");

            // --- Loads ---------------------------------------------------------------------------------------------
            int loadOff = 0, loadIsolated = 0, loadY = 0, loadI = 0, loadDgen = 0;
            foreach (PsseRecord r in _loads)
            {
                int bus = r.Int(0, "I");
                BusOf(r, bus, "load bus");
                int status = r.Code(2, "STATUS", 1, 0, 1);
                double pl = r.Real(5, "PL", 0), ql = r.Real(6, "QL", 0), ip = r.Real(7, "IP", 0), iq = r.Real(8, "IQ", 0),
                    yp = r.Real(9, "YP", 0), yq = r.Real(10, "YQ", 0);
                if (_rev >= 34 && r.Code(16, "DGENF", 0, 0, 1) == 1)
                {
                    double dp = r.Real(14, "DGENP", 0), dq = r.Real(15, "DGENQ", 0);
                    if (dp != 0 || dq != 0) { pl -= dp; ql -= dq; if (status == 1) loadDgen++; }
                }
                if (status == 0) { loadOff++; continue; }
                if (isolated.Contains(bus)) { loadIsolated++; continue; }
                BusAcc a = Acc(bus);
                BusAcc.Add(ref a.Pl, pl);
                BusAcc.Add(ref a.Ql, ql);
                if (ip != 0 || iq != 0) { BusAcc.Add(ref a.Ip, ip); BusAcc.Add(ref a.Iq, iq); loadI++; }
                if (yp != 0 || yq != 0) { BusAcc.Add(ref a.Gs, yp); BusAcc.Add(ref a.Bs, yq); loadY++; }
            }
            if (loadOff > 0) _notes.Add($"skipped {loadOff} out-of-service load(s).");
            if (loadIsolated > 0) _notes.Add($"skipped {loadIsolated} load(s) on dropped isolated (IDE 4) bus(es).");
            if (loadY > 0) _notes.Add($"{loadY} load(s) have a constant-admittance part (YP, YQ); it is modelled as a bus shunt (Gs += YP, Bs += YQ), which is exactly a constant-admittance load.");
            if (loadI > 0) _notes.Add($"{loadI} load(s) have a constant-current part (IP, IQ); their buses are ZIP loads (P = (PL + IP)(Cp + Bp V), and likewise for Q).");
            if (loadDgen > 0) _notes.Add($"{loadDgen} in-service load(s) carry distributed generation (DGENP, DGENQ with DGENF 1); it is netted off the load.");

            // --- Fixed shunts --------------------------------------------------------------------------------------
            int shuntOff = 0;
            foreach (PsseRecord r in _fixedShunts)
            {
                int bus = r.Int(0, "I");
                BusOf(r, bus, "fixed shunt bus");
                if (r.Code(2, "STATUS", 1, 0, 1) == 0) { shuntOff++; continue; }
                if (isolated.Contains(bus)) continue;
                BusAcc a = Acc(bus);
                BusAcc.Add(ref a.Gs, r.Real(3, "GL", 0));
                BusAcc.Add(ref a.Bs, r.Real(4, "BL", 0));
            }
            if (shuntOff > 0) _notes.Add($"skipped {shuntOff} out-of-service fixed shunt(s).");

            // A bus a generator holds: a swing bus, or a PV bus with an in-service unit on it (the generator records read raw).
            bool GeneratorHeld(int id)
            {
                int ide = busRec[id].Int(3, "IDE", 1);
                if (ide == 3) return true;
                if (ide != 2) return false;
                int st = _rev >= 35 ? 15 : 14;
                return _gens.Any(g => g.Int(0, "I") == id && g.Int(st, "STAT", 1) == 1);
            }

            // --- Switched shunts: fixed at BINIT (and, with ApplyShuntControl, regulating banks) --------------------
            int swOn = 0, swOff = 0;
            var banks = new List<SwitchedShunt>();
            var svcs = new List<StaticVarCompensator>();
            var bankSkipped = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PsseRecord r in _switchedShunts)
            {
                int bus = r.Int(0, "I");
                BusOf(r, bus, "switched shunt bus");
                // Revision 30 has no ADJM or STAT field (a switched shunt is in service) and BINIT is field 8.
                int statIdx = _rev >= 35 ? 4 : 3, binitIdx = _rev >= 35 ? 11 : V30 ? 7 : 9, modswIdx = _rev >= 35 ? 2 : 1;
                int modsw = r.Code(modswIdx, "MODSW", 1, 0, 1, 2, 3, 4, 5, 6);
                if (!V30 && r.Code(statIdx, "STAT", 1, 0, 1) == 0) { swOff++; continue; }
                if (isolated.Contains(bus)) continue;
                double binit = r.Real(binitIdx, "BINIT", 0);
                BusAcc.Add(ref Acc(bus).Bs, binit);
                swOn++;
                if (_options.ApplyShuntControl && modsw == 2)
                {
                    (StaticVarCompensator? svc, string? why) = Svc(r, bus, binit);
                    if (svc is not null) svcs.Add(svc);
                    else bankSkipped[why!] = bankSkipped.GetValueOrDefault(why!) + 1;
                }
                else if (_options.ApplyShuntControl)
                {
                    (SwitchedShunt? bank, string? why) = ShuntBank(r, bus, modsw, binit, rem =>
                    {
                        // A remote regulated bus: it must be in the file; a dropped isolated one, or one a generator holds
                        // (a swing bus, or a PV bus with an in-service unit), cannot be moved by switching.
                        if (!busRec.ContainsKey(rem)) throw r.Error($"SWREM names bus {rem}, which the file does not define");
                        if (isolated.Contains(rem)) return "regulate a remote bus (SWREM) that was dropped as isolated";
                        if (GeneratorHeld(rem)) return "regulate a remote bus (SWREM) that a generator holds, so switching cannot move it";
                        return null;
                    });
                    if (bank is not null) banks.Add(bank);
                    else if (why is not null) bankSkipped[why] = bankSkipped.GetValueOrDefault(why) + 1;
                }
            }
            if (swOn > banks.Count + svcs.Count)
                _notes.Add(_options.ApplyShuntControl
                    ? $"{swOn - banks.Count - svcs.Count} switched shunt(s) are fixed at their initial susceptance BINIT; a solve that would have switched them differs from PSS/E's."
                    : $"{swOn} switched shunt(s) are fixed at their initial susceptance BINIT; GridSim does not switch shunt blocks, so a solve that would have switched them differs from PSS/E's.");
            if (banks.Count > 0)
                _notes.Add($"{banks.Count} switched shunt(s) with discrete voltage control (MODSW 1) switch whole blocks from BINIT to hold their bus in VSWLO..VSWHI.");
            if (svcs.Count > 0)
                _notes.Add($"{svcs.Count} switched shunt(s) with continuous voltage control (MODSW 2) are static var compensators: the susceptance varies continuously between all reactor blocks and all capacitor blocks in, staying at BINIT while the bus is in VSWLO..VSWHI and otherwise holding the band edge the bus crossed.");
            foreach (var kv in bankSkipped)
                _notes.Add($"{kv.Value} switched shunt(s) {kv.Key}: they stay fixed at BINIT.");
            if (swOff > 0) _notes.Add($"skipped {swOff} out-of-service switched shunt(s).");

            // --- Generators ----------------------------------------------------------------------------------------
            var gens = new List<Generator>();
            var machineKeys = new List<PsseMachineKey>();
            var machineSeen = new Dictionary<(int, string), int>();
            int genOffKept = 0, genIsolated = 0, genRemote = 0, genZeroMbase = 0;
            foreach (PsseRecord r in _gens)
            {
                int bus = r.Int(0, "I");
                BusOf(r, bus, "generator bus");
                string id = r.Str(1, "1");
                if (machineSeen.TryGetValue((bus, id), out int prior))
                    throw r.Error($"machine {id} at bus {bus} is defined twice (also at line {prior})");
                machineSeen[(bus, id)] = r.Line;
                bool v35 = _rev >= 35;
                int mbIdx = v35 ? 9 : 8, statIdx = v35 ? 15 : 14, ptIdx = v35 ? 17 : 16, pbIdx = v35 ? 18 : 17;
                bool inService = r.Code(statIdx, "STAT", 1, 0, 1) == 1;
                if (isolated.Contains(bus)) { genIsolated++; continue; }
                double qt = r.Real(4, "QT", 9999), qb = r.Real(5, "QB", -9999), pt = r.Real(ptIdx, "PT", 9999), pb = r.Real(pbIdx, "PB", -9999);
                if (qb > qt) throw r.Error($"QB {qb} is above QT {qt}");
                if (pb > pt) throw r.Error($"PB {pb} is above PT {pt}");
                double mbase = r.Real(mbIdx, "MBASE", _sbase);
                if (mbase < 0) throw r.Error($"MBASE is {mbase}; it must be 0 (the system base) or above");
                if (mbase == 0) { mbase = _sbase; genZeroMbase++; }
                int ireg = r.Int(7, "IREG", 0);
                if (ireg != 0 && ireg != bus) { BusOf(r, ireg, "IREG"); genRemote++; }
                if (!inService) genOffKept++;
                gens.Add(new Generator(
                    Bus: bus,
                    Pg: r.Real(2, "PG", 0),
                    Qg: r.Real(3, "QG", 0),
                    Vg: r.Real(6, "VS", 1.0),
                    Qmin: qb,
                    Qmax: qt,
                    Pmin: pb,
                    Pmax: pt,
                    MBase: mbase,
                    InService: inService));
                machineKeys.Add(new PsseMachineKey(bus, id));
            }
            if (genOffKept > 0) _notes.Add($"kept {genOffKept} out-of-service generator(s) in the model, marked out of service (not solved).");
            if (genIsolated > 0) _notes.Add($"skipped {genIsolated} generator(s) on dropped isolated (IDE 4) bus(es).");
            if (genZeroMbase > 0) _notes.Add($"{genZeroMbase} generator(s) give MBASE 0; their machine base is the system base ({_sbase.ToString("R", CultureInfo.InvariantCulture)} MVA).");
            if (genRemote > 0) _notes.Add($"{genRemote} generator(s) regulate a remote bus (IREG); GridSim holds the set point VS at the unit's own bus instead, as MATPOWER does, so those voltages differ from PSS/E's.");

            // --- Branches ------------------------------------------------------------------------------------------
            var branches = new List<Branch>();
            var branchKeys = new List<PsseBranchKey>();
            var skippedKeys = new List<PsseBranchKey>();   // records read but not in the model (out of service, dangling)
            int brOff = 0, brDangling = 0, zeroZ = 0;
            foreach (PsseRecord r in _branches)
            {
                int i = r.Int(0, "I"), j = Math.Abs(r.Int(1, "J"));   // a negative J marks the metered end
                BusOf(r, i, "bus I");
                BusOf(r, j, "bus J");
                if (i == j) throw r.Error($"both ends are bus {i}");
                string ckt = r.Str(2, "1");
                bool v34 = _rev >= 34;
                int rate = v34 ? 7 : 6, gi = v34 ? 19 : 9, st = v34 ? 23 : 13;
                double rr = r.Real(3, "R", 0), x = r.Real(4, "X"), b = r.Real(5, "B", 0);
                bool on = r.Code(st, "ST", 1, 0, 1) == 1;
                if (!on) { brOff++; skippedKeys.Add(new PsseBranchKey("branch", i, j, 0, ckt, 0)); continue; }
                if (!Live(i) || !Live(j)) { brDangling++; skippedKeys.Add(new PsseBranchKey("branch", i, j, 0, ckt, 0)); continue; }
                if (rr == 0 && x == 0) { x = ZeroImpedanceXPu; zeroZ++; }
                double giV = r.Real(gi, "GI", 0), biV = r.Real(gi + 1, "BI", 0), gjV = r.Real(gi + 2, "GJ", 0), bjV = r.Real(gi + 3, "BJ", 0);
                if (giV != 0 || biV != 0) { BusAcc.Add(ref Acc(i).Gs, giV * _sbase); BusAcc.Add(ref Acc(i).Bs, biV * _sbase); }
                if (gjV != 0 || bjV != 0) { BusAcc.Add(ref Acc(j).Gs, gjV * _sbase); BusAcc.Add(ref Acc(j).Bs, bjV * _sbase); }
                string? name = v34 && r.Str(6).Length > 0 ? r.Str(6) : null;
                branches.Add(new Branch(i, j, rr, x, b, RateAMva: r.Real(rate, "RATE A", 0), RateBMva: r.Real(rate + 1, "RATE B", 0),
                    RateCMva: r.Real(rate + 2, "RATE C", 0), Name: name));
                branchKeys.Add(new PsseBranchKey("branch", i, j, 0, ckt, 0));
            }

            // --- System switching devices (revision 34 and 35): zero-impedance branches -----------------------------
            int swDevOff = 0;
            foreach (PsseRecord r in _switching)
            {
                int i = r.Int(0, "I"), j = Math.Abs(r.Int(1, "J"));
                BusOf(r, i, "bus I");
                BusOf(r, j, "bus J");
                if (i == j) throw r.Error($"both ends are bus {i}");
                string ckt = r.Str(2, "1");
                double x = r.Real(3, "X", ZeroImpedanceXPu);
                if (r.Code(16, "STAT", 1, 0, 1) == 0) { swDevOff++; continue; }
                if (!Live(i) || !Live(j)) { brDangling++; continue; }
                if (x == 0) { x = ZeroImpedanceXPu; zeroZ++; }
                string name = r.Str(20);
                branches.Add(new Branch(i, j, 0, x, RateAMva: r.Real(4, "RATE1", 0), RateBMva: r.Real(5, "RATE2", 0),
                    RateCMva: r.Real(6, "RATE3", 0), Name: name.Length > 0 ? name : null));
                branchKeys.Add(new PsseBranchKey("switching device", i, j, 0, ckt, 0));
            }
            if (swDevOff > 0) _notes.Add($"skipped {swDevOff} open system switching device(s).");

            // --- Transformers --------------------------------------------------------------------------------------
            var stars = new List<Bus>();
            var starIds = new List<int>();
            int maxBus = busRec.Keys.Max();
            int starBase = 1;
            while (starBase <= maxBus) starBase *= 10;   // the next power of ten above the highest bus number
            int xfOff = 0, xfTapControl = 0, xfImpTable = 0, xfImpOutside = 0, xfImpTapped = 0, xfMag = 0, xfOtherControl = 0;
            var threeW = new List<(Branch Branch, PsseBranchKey Key, TapControl? Control, int OwnBus)>();
            // Tap control (ApplyTapControl only): each controlled winding's branch index, its control and the ratio its
            // tap limits are divided by (winding 2's ratio for a two-winding unit, whose branch tap is w1 / w2).
            var controlled = new List<(int Index, TapControl Control, double Scale, int OwnBus, string Key)>();
            foreach (PsseRecord[] t in _transformers)
            {
                PsseRecord r1 = t[0], r2 = t[1];
                int i = r1.Int(0, "I"), j = Math.Abs(r1.Int(1, "J")), k = Math.Abs(r1.Int(2, "K", 0));
                bool three = k != 0;
                BusOf(r1, i, "bus I");
                BusOf(r1, j, "bus J");
                if (three) BusOf(r1, k, "bus K");
                if (i == j || (three && (i == k || j == k))) throw r1.Error("a transformer's windings must be on different buses");
                string ckt = r1.Str(3, "1");
                int cw = r1.Code(4, "CW", 1, 1, 2, 3), cz = r1.Code(5, "CZ", 1, 1, 2, 3), cm = r1.Code(6, "CM", 1, 1, 2);
                double mag1 = r1.Real(7, "MAG1", 0), mag2 = r1.Real(8, "MAG2", 0);
                string xname = r1.Str(10);
                int stat = three ? r1.Code(11, "STAT", 1, 0, 1, 2, 3, 4) : r1.Code(11, "STAT", 1, 0, 1);
                if (stat == 0) { xfOff++; skippedKeys.Add(new PsseBranchKey(three ? "transformer3" : "transformer", i, j, k, ckt, 0)); continue; }

                int windings = three ? 3 : 2;
                int[] wbus = three ? new[] { i, j, k } : new[] { i, j };
                int codIdx = _rev >= 34 ? 15 : 6, tabIdx = _rev >= 35 ? 23 : _rev >= 34 ? 22 : 13, rateIdx = 3;
                var w = new double[windings];       // each winding's ratio in pu of its bus base
                var nomv = new double[windings];    // each winding's nominal voltage (kV), the bus base when 0
                var ang = new double[windings];
                var rates = new (double A, double B, double C)[windings];
                bool tapControl = false;
                var tapSpecs = new TapControl?[windings];   // per winding: its COD 1 control, when tap control is applied
                var tables = new ImpedanceCorrectionPoint[]?[windings];   // per winding: its impedance correction table
                var factors = new (double Re, double Im)?[windings];       // ... and the factor at the file's ratio or angle
                for (int n = 0; n < windings; n++)
                {
                    PsseRecord wr = t[2 + n];
                    double busKv = BusOf(r1, wbus[n], "winding bus").Real(2, "BASKV", 0);
                    double nv = wr.Real(1, $"NOMV{n + 1}", 0);
                    if (nv < 0) throw wr.Error($"NOMV{n + 1} is {nv}; it must be 0 (the bus base) or above");
                    nomv[n] = nv > 0 ? nv : busKv;
                    double windDefault = cw == 2 ? busKv : 1.0;
                    double windv = wr.Real(0, $"WINDV{n + 1}", windDefault);
                    if (!(windv > 0)) throw wr.Error($"WINDV{n + 1} is {windv}; a winding voltage must be above 0");
                    if (cw != 1 && !(busKv > 0))
                        throw wr.Error($"bus {wbus[n]} has no base voltage (BASKV {busKv}), so a winding voltage given with CW {cw} cannot be put in per unit");
                    w[n] = cw switch
                    {
                        1 => windv,
                        2 => windv / busKv,
                        _ => windv * nomv[n] / busKv,
                    };
                    // The two-winding winding 2 line holds only WINDV2 and NOMV2: no angle, ratings or control.
                    if (!three && n == 1) continue;
                    ang[n] = wr.Real(2, $"ANG{n + 1}", 0);
                    rates[n] = (wr.Real(rateIdx, $"RATE{n + 1} A", 0), wr.Real(rateIdx + 1, $"RATE{n + 1} B", 0), wr.Real(rateIdx + 2, $"RATE{n + 1} C", 0));
                    int cod = wr.Int(codIdx, $"COD{n + 1}", 0);
                    if (cod > 0) tapControl = true;
                    if (_options.ApplyTapControl && cod == 1) tapSpecs[n] = ReadTapControl(wr, n, cw, busKv, nomv[n], codIdx, tabIdx);
                    else if (_options.ApplyTapControl && cod is 2 or 3) tapSpecs[n] = ReadFlowControl(wr, n, cw, busKv, nomv[n], cod, tabIdx);
                    else if (_options.ApplyTapControl && cod > 3) xfOtherControl++;
                    int tab = wr.Int(tabIdx, $"TAB{n + 1}", 0);
                    if (tab != 0)
                    {
                        if (!_impedanceTables.TryGetValue(tab, out var table)) throw wr.Error($"TAB{n + 1} {tab} names an impedance correction table the file does not define");
                        // The table runs on the winding's ratio (pu of its bus base), or on its phase shift angle (degrees)
                        // for a phase shifting winding (|COD| 3). Bug hunt 2026-10-29, finding 45 (decided, documented): a
                        // reading of the PSS/E manual takes the abscissa as the ratio in pu of the winding's NOMINAL voltage
                        // (WINDV for CW 3, WINDV / NOMV for CW 2), which differs from this only when NOMV != BASKV. No PSS/E
                        // program manual is in docs/reference to settle it, so the bus-base form is kept (every pinned case
                        // has NOMV = BASKV or no table, where the two agree) [VERIFY against the PSS/E POM].
                        double tv = Math.Abs(cod) == 3 ? ang[n] : w[n];
                        if (tv < table.Points[0].T || tv > table.Points[^1].T) xfImpOutside++;
                        tables[n] = table.Points;
                        factors[n] = TapImpedanceCorrection.Factor(table.Points, tv);
                        xfImpTable++;
                    }
                }
                if (tapControl) xfTapControl++;

                // Impedances on the system base. Pair p is between windings p and p+1 (mod 3) and is given on winding p's
                // base for CZ 2 and 3.
                int pairs = three ? 3 : 1;
                var zr = new double[pairs];
                var zx = new double[pairs];
                for (int p = 0; p < pairs; p++)
                {
                    string pn = three ? new[] { "1-2", "2-3", "3-1" }[p] : "1-2";
                    double rv = r2.Real(3 * p, $"R{pn}", 0), xv = r2.Real(3 * p + 1, $"X{pn}");
                    double sb = r2.Real(3 * p + 2, $"SBASE{pn}", _sbase);
                    if (cz != 1)
                    {
                        if (!(sb > 0)) throw r2.Error($"SBASE{pn} is {sb}; it must be above 0 MVA for CZ {cz}");
                        if (cz == 3)
                        {
                            double rw = 1e-6 * rv / sb;   // load loss (W) at rated current, as pu on the winding base
                            if (xv < rw) throw r2.Error($"X{pn} (impedance magnitude {xv}) is below the resistance its load loss gives ({rw}); not a transformer");
                            xv = Math.Sqrt(xv * xv - rw * rw);
                            rv = rw;
                        }
                        double busKv = BusOf(r1, wbus[p], "winding bus").Real(2, "BASKV", 0);
                        if (!(busKv > 0)) throw r2.Error($"bus {wbus[p]} has no base voltage, so an impedance given with CZ {cz} cannot be put on the system base");
                        double zbw = nomv[p] * nomv[p] / sb, zbs = busKv * busKv / _sbase;
                        rv = rv * zbw / zbs;
                        xv = xv * zbw / zbs;
                    }
                    if (rv == 0 && xv == 0) throw r2.Error($"R{pn} and X{pn} are both 0; a transformer must have an impedance");
                    zr[p] = rv;
                    zx[p] = xv;
                }

                // Magnetising admittance on the system base.
                (double g, double bm) = (0, 0);
                if (mag1 != 0 || mag2 != 0)
                {
                    xfMag++;
                    if (cm == 1) (g, bm) = (mag1, mag2);
                    else
                    {
                        double sb = r2.Real(2, "SBASE1-2", _sbase);
                        if (!(sb > 0)) throw r2.Error($"SBASE1-2 is {sb}; it must be above 0 MVA for CM 2");
                        double gw = 1e-6 * mag1 / sb;   // no-load loss (W) as pu on the winding base
                        if (mag2 < gw) throw r1.Error($"MAG2 (exciting current {mag2}) is below the conductance the no-load loss gives ({gw})");
                        double bw = -Math.Sqrt(mag2 * mag2 - gw * gw);
                        double busKv = BusOf(r1, i, "bus I").Real(2, "BASKV", 0);
                        if (!(busKv > 0)) throw r1.Error($"bus {i} has no base voltage, so a magnetising admittance given with CM 2 cannot be put on the system base");
                        double scale = (busKv * busKv / _sbase) / (nomv[0] * nomv[0] / sb);
                        (g, bm) = (gw * scale, bw * scale);
                    }
                }

                if (!three)
                {
                    if (!Live(i) || !Live(j)) { brDangling++; skippedKeys.Add(new PsseBranchKey("transformer", i, j, 0, ckt, 0)); continue; }
                    if (g != 0 || bm != 0) { BusAcc.Add(ref Acc(i).Gs, g * _sbase); BusAcc.Add(ref Acc(i).Bs, bm * _sbase); }
                    double r12 = zr[0], x12 = zx[0];
                    if (factors[0] is (double fr2, double fi2)) (r12, x12) = (zr[0] * fr2 - zx[0] * fi2, zr[0] * fi2 + zx[0] * fr2);
                    if (tapSpecs[0] is TapControl tc2)
                    {
                        // The table runs on the ratio (scaled by winding 2's, as the branch tap is w1 / w2), or on the angle for
                        // a phase shifter (COD 3).
                        if (tables[0] is { } t2) { tc2 = tc2 with { Correction = new TapImpedanceCorrection(zr[0], zx[0], tc2.Cod == 3 ? 1.0 : w[1], t2) }; xfImpTapped++; }
                        controlled.Add((branches.Count, tc2, w[1], i, $"{i}-{j} {ckt}"));
                    }
                    branches.Add(new Branch(i, j, r12, x12, 0, TapRatio: w[0] / w[1], PhaseShiftDeg: ang[0],
                        RateAMva: rates[0].A, RateBMva: rates[0].B, RateCMva: rates[0].C, IsTransformer: true,
                        Name: xname.Length > 0 ? xname : null));
                    branchKeys.Add(new PsseBranchKey("transformer", i, j, 0, ckt, 0));
                    continue;
                }

                // Three windings: which are in service and on live buses.
                bool[] on = { stat != 4 && Live(i), stat != 2 && Live(j), stat != 3 && Live(k) };
                if (on.Count(x => x) < 2) { brDangling++; skippedKeys.Add(new PsseBranchKey("transformer3", i, j, k, ckt, 0)); continue; }
                int star = starBase + stars.Count + 1;
                PsseRecord b1 = BusOf(r1, i, "bus I");
                string label = xname.Length > 0 ? xname : $"{i}-{j}-{k} {ckt}";
                stars.Add(new Bus(star, BusType.Pq, Gs: g * _sbase, Bs: bm * _sbase, BaseKv: b1.Real(2, "BASKV", 0),
                    Vm: r2.Real(9, "VMSTAR", 1.0), VaDeg: r2.Real(10, "ANSTAR", 0), Name: $"{label} star point"));
                starIds.Add(star);
                meta[star] = MetaOf(b1);
                // Star impedances: Z1 = (Z12 + Z31 - Z23) / 2, Z2 = (Z12 + Z23 - Z31) / 2, Z3 = (Z31 + Z23 - Z12) / 2.
                double[] sr = { (zr[0] + zr[2] - zr[1]) / 2, (zr[0] + zr[1] - zr[2]) / 2, (zr[2] + zr[1] - zr[0]) / 2 };
                double[] sx = { (zx[0] + zx[2] - zx[1]) / 2, (zx[0] + zx[1] - zx[2]) / 2, (zx[2] + zx[1] - zx[0]) / 2 };
                for (int n = 0; n < 3; n++)
                {
                    if (!on[n]) continue;
                    if (sr[n] == 0 && sx[n] == 0)
                        throw r2.Error($"winding {n + 1}'s star impedance is 0 (the pair impedances cancel); not a transformer GridSim can represent");
                    double rs = sr[n], xs = sx[n];
                    if (factors[n] is (double fr3, double fi3)) (rs, xs) = (sr[n] * fr3 - sx[n] * fi3, sr[n] * fi3 + sx[n] * fr3);
                    if (tapSpecs[n] is TapControl tc3w && tables[n] is { } t3)
                    {
                        tapSpecs[n] = tc3w with { Correction = new TapImpedanceCorrection(sr[n], sx[n], 1.0, t3) };
                        xfImpTapped++;
                    }
                    threeW.Add((new Branch(wbus[n], star, rs, xs, 0, TapRatio: w[n], PhaseShiftDeg: ang[n],
                        RateAMva: rates[n].A, RateBMva: rates[n].B, RateCMva: rates[n].C, IsTransformer: true,
                        Name: xname.Length > 0 ? $"{xname} winding {n + 1}" : null),
                        new PsseBranchKey("transformer3", i, j, k, ckt, n + 1), tapSpecs[n], wbus[n]));
                }
            }
            foreach ((Branch b, PsseBranchKey key, TapControl? tc3, int ownBus) in threeW)
            {
                if (tc3 is not null) controlled.Add((branches.Count, tc3, 1.0, ownBus, $"{key.FromBus}-{key.ToBus}-{key.ThirdBus} {key.Circuit} winding {key.Winding}"));
                branches.Add(b);
                branchKeys.Add(key);
            }
            var flowControls = new List<TransformerFlowControl>();
            List<Oltc>? oltcs = _options.ApplyTapControl ? TapChangers(controlled, branches, busRec.ContainsKey, Live, flowControls) : null;
            if (brOff > 0) _notes.Add($"skipped {brOff} out-of-service branch(es).");
            if (xfOff > 0) _notes.Add($"skipped {xfOff} out-of-service transformer(s).");
            if (brDangling > 0) _notes.Add($"skipped {brDangling} branch(es) or transformer(s) connected to a dropped isolated (IDE 4) bus.");
            if (zeroZ > 0) _notes.Add($"{zeroZ} branch(es) or switching device(s) have R = X = 0; given X = {ZeroImpedanceXPu.ToString("R", CultureInfo.InvariantCulture)} pu (PSS/E's zero-impedance threshold).");
            if (xfTapControl > 0 && !_options.ApplyTapControl) _notes.Add($"{xfTapControl} transformer(s) have automatic tap or phase control (COD); it is not applied: the taps stay at the file's ratios.");
            if (xfOtherControl > 0) _notes.Add($"{xfOtherControl} transformer winding(s) have DC line or asymmetric control (COD 4 and 5); it is not applied: those taps and phase shifts stay at the file's values.");
            if (xfMag > 0) _notes.Add($"{xfMag} transformer(s) have a magnetising admittance; it is a shunt at winding 1's bus (two windings) or at the star point (three windings).");
            if (stars.Count > 0) _notes.Add($"added {stars.Count} star-point bus(es) ({starIds[0]}..{starIds[^1]}) for three-winding transformers.");
            if (xfImpTable > 0)
                _notes.Add($"{xfImpTable} transformer winding(s) use an impedance correction table (TAB): the winding's impedance (Z1-2 for a two-winding unit, its star impedance for a three-winding one) is multiplied by the table's factor at the winding's ratio, or at its phase shift angle for a phase shifter (|COD| 3), interpolated linearly." +
                    (xfImpTapped > 0 ? $" {xfImpTapped} of them are tap-controlled: the factor is recomputed at every tap the tap loop steps." : ""));
            if (xfImpOutside > 0)
                _notes.Add($"{xfImpOutside} winding(s) have a ratio or angle outside their impedance correction table; the table's end factor is used, as PSS/E does.");

            // --- Two-terminal DC links ---------------------------------------------------------------------------
            var lcc = new List<LccDcLink>();
            int lccBlocked = 0, lccDead = 0;
            var lccNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (PsseRecord[] d in _twoTerminal)
            {
                PsseRecord l = d[0];
                string name = V30 ? $"DC {l.Int(0, "I")}" : l.Str(0, "");
                if (name.Length == 0) throw l.Error("a two-terminal DC link needs a name");
                if (!lccNames.Add(name)) throw l.Error($"two-terminal DC link '{name}' is defined twice");
                int mdc = l.Code(1, "MDC", 0, 0, 1, 2);
                if (mdc == 0) { lccBlocked++; continue; }
                (LccConverter rect, bool ccr) = Converter(d[1], "rectifier", "ANMXR", "ANMNR");
                (LccConverter inv, bool cci) = Converter(d[2], "inverter", "ANMXI", "ANMNI");
                if (ccr || cci) { Count("capacitor-commutated two-terminal DC link", l, true); continue; }
                if (rect.Bus == inv.Bus) throw l.Error("the rectifier and inverter are on the same bus");
                BusOf(d[1], rect.Bus, "IPR");
                BusOf(d[2], inv.Bus, "IPI");
                if (!Live(rect.Bus) || !Live(inv.Bus)) { lccDead++; continue; }
                double rdc = l.Real(2, "RDC");
                double setvl = l.Real(3, "SETVL");
                double vschd = l.Real(4, "VSCHD");
                double vcmod = l.Real(5, "VCMOD", 0), rcomp = l.Real(6, "RCOMP", 0), delti = l.Real(7, "DELTI", 0);
                if (rdc < 0) throw l.Error($"RDC is {rdc}; a DC line resistance must be 0 or above");
                if (setvl == 0) throw l.Error("SETVL is 0; an in-service link needs a power or current order");
                if (mdc == 2 && setvl < 0) throw l.Error($"SETVL is {setvl} A; a current order must be above 0");
                if (!(vschd > 0)) throw l.Error($"VSCHD is {vschd} kV; the scheduled DC voltage must be above 0");
                if (rcomp < 0 || vcmod < 0) throw l.Error("RCOMP and VCMOD must be 0 or above");
                if (delti < 0 || delti >= 1) throw l.Error($"DELTI is {delti}; the current margin must be in 0 .. 1 (pu)");
                lcc.Add(new LccDcLink(name, mdc == 1 ? LccMode.Power : LccMode.Current, rdc, setvl, vschd, vcmod, rcomp, delti, rect, inv));
            }
            if (lcc.Count > 0)
                _notes.Add($"{lcc.Count} two-terminal DC link(s) are solved with the AC power flow: each converter's DC voltage, firing or extinction angle, tap, active and reactive power from PSS/E's two-terminal model, the converters as constant-power loads alternating with the DC solve.");
            if (lccBlocked > 0) _notes.Add($"skipped {lccBlocked} blocked two-terminal DC link(s) (MDC 0).");
            if (lccDead > 0) _notes.Add($"skipped {lccDead} two-terminal DC link(s) with a converter on a dropped isolated (IDE 4) bus.");

            // --- VSC DC links ------------------------------------------------------------------------------------
            var vscLinks = new List<VscDcLink>();
            int vscOff = 0, vscDead = 0;
            var vscNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (PsseRecord[] d in _vsc)
            {
                PsseRecord l = d[0];
                string name = l.Str(0, "");
                if (name.Length == 0) throw l.Error("a VSC DC link needs a name");
                if (!vscNames.Add(name)) throw l.Error($"VSC DC link '{name}' is defined twice");
                int mdc = l.Code(1, "MDC", 1, 0, 1);
                double rdc = l.Real(2, "RDC");
                if (rdc < 0) throw l.Error($"RDC is {rdc}; a DC line resistance must be 0 or above");
                int t1 = d[1].Code(1, "TYPE", 1, 0, 1, 2), t2 = d[2].Code(1, "TYPE", 1, 0, 1, 2);
                if (mdc == 0 || t1 == 0 || t2 == 0) { vscOff++; continue; }
                if (t1 == t2)
                    throw l.Error($"both converters of VSC link '{name}' have TYPE {t1}; a link needs one DC voltage converter (TYPE 1) and one power converter (TYPE 2)");
                (VscConverter? c1, bool remote1) = Vsc(d[1], name);
                (VscConverter? c2, bool remote2) = Vsc(d[2], name);
                BusOf(d[1], d[1].Int(0, "IBUS"), "IBUS");
                BusOf(d[2], d[2].Int(0, "IBUS"), "IBUS");
                if (remote1 || remote2) { Count("VSC converter regulating a remote bus", l, true); continue; }
                if (c1!.Bus == c2!.Bus) throw l.Error($"both converters of VSC link '{name}' are on bus {c1.Bus}");
                if (!Live(c1.Bus) || !Live(c2.Bus)) { vscDead++; continue; }
                vscLinks.Add(new VscDcLink(name, rdc, c1, c2));
            }
            if (vscLinks.Count > 0)
                _notes.Add($"{vscLinks.Count} VSC DC link(s) are solved with the AC power flow: PSS/E's VSC model, one converter holding the DC voltage and one an active power order, losses ALOSS + BLOSS x Idc (at least MINLOSS), each converter holding its AC voltage (MODE 1) or a power factor (MODE 2) within its MVA and current ratings (reduced by PWF when exceeded).");
            if (vscOff > 0) _notes.Add($"skipped {vscOff} out-of-service VSC DC link(s) (MDC 0 or a converter of TYPE 0).");
            if (vscDead > 0) _notes.Add($"skipped {vscDead} VSC DC link(s) with a converter on a dropped isolated (IDE 4) bus.");

            // --- Multi-terminal DC links --------------------------------------------------------------------------
            var mtdcLinks = new List<MtdcLink>();
            int mtdcOff = 0, mtdcDead = 0;
            foreach (PsseRecord[] d in _mtdc)
            {
                PsseRecord l = d[0];
                string name = V30 ? $"MTDC {l.Int(0, "I")}" : l.Str(0, "");
                if (name.Length == 0) throw l.Error("a multi-terminal DC link needs a name");
                int ncv = l.Int(1, "NCONV"), ndb = l.Int(2, "NDCBS"), ndl = l.Int(3, "NDCLN");
                int mdc = l.Code(4, "MDC", 0, 0, 1, 2);
                int vconv = l.Int(5, "VCONV");
                if (mdc == 0) { mtdcOff++; continue; }
                if (ncv < 2 || ndb < 2 || ndl < 1) throw l.Error($"a multi-terminal DC link needs at least 2 converters, 2 DC buses and 1 DC line (has {ncv}, {ndb}, {ndl})");
                var convRec = d.Skip(1).Take(ncv).ToArray();
                var busRecs = d.Skip(1 + ncv).Take(ndb).ToArray();
                var lineRecs = d.Skip(1 + ncv + ndb).Take(ndl).ToArray();
                var dcBuses = new List<MtdcDcBus>();
                bool twoBus = false;
                foreach (PsseRecord b in busRecs)
                {
                    int idc = b.Int(0, "IDC");
                    if (dcBuses.Any(x => x.Number == idc)) throw b.Error($"DC bus {idc} is defined twice");
                    int ib = b.Int(1, "IB", 0);
                    if (b.Int(5, "IDC2", 0) != 0) twoBus = true;
                    double rg = b.Real(6, "RGRND", 0);
                    if (rg < 0) throw b.Error($"RGRND is {rg}; a grounding resistance must be 0 or above");
                    string dn = b.Str(4, "");
                    dcBuses.Add(new MtdcDcBus(idc, ib, rg, dn.Length > 0 ? dn : null));
                }
                if (twoBus) { Count("multi-terminal DC converter connected to two DC buses (IDC2)", l, true); continue; }
                var dcLines = new List<MtdcDcLine>();
                foreach (PsseRecord dl in lineRecs)
                {
                    int i = dl.Int(0, "IDC"), j = dl.Int(1, "JDC");
                    double rdc = dl.Real(V30 ? 3 : 4, "RDC");
                    if (!dcBuses.Any(x => x.Number == i) || !dcBuses.Any(x => x.Number == j)) throw dl.Error($"DC line {i}-{j} names a DC bus the link does not define");
                    if (i == j) throw dl.Error("a DC line must join two different DC buses");
                    if (!(rdc > 0)) throw dl.Error($"RDC is {rdc}; a DC line needs a resistance above 0 ohms");
                    dcLines.Add(new MtdcDcLine(i, j, rdc));
                }
                var convs = new List<MtdcConverter>();
                bool dead = false;
                foreach (PsseRecord c in convRec)
                {
                    int ib = c.Int(0, "IB");
                    BusOf(c, ib, "IB");
                    if (!Live(ib)) dead = true;
                    int n = c.Int(1, "N");
                    if (n < 1) throw c.Error($"converter {ib} has {n} bridges; it needs at least 1");
                    double amx = c.Real(2, "ANGMX"), amn = c.Real(3, "ANGMN");
                    if (!(amn >= 0) || !(amx > amn) || !(amx < 90)) throw c.Error($"converter {ib}'s angle limits {amn} .. {amx} degrees must satisfy 0 <= min < max < 90");
                    double rcv = c.Real(4, "RC", 0), xcv = c.Real(5, "XC"), ebas = c.Real(6, "EBAS");
                    double tr = c.Real(7, "TR", 1.0), tap = c.Real(8, "TAP", 1.0), tmx = c.Real(9, "TPMX", 1.5), tmn = c.Real(10, "TPMN", 0.51), stp = c.Real(11, "TSTP", 0.00625);
                    double setvl = c.Real(12, "SETVL");
                    if (rcv < 0 || xcv < 0 || !(ebas > 0) || !(tr > 0) || !(tap > 0) || !(tmn > 0) || !(tmx >= tmn) || stp < 0)
                        throw c.Error($"converter {ib}'s commutating impedance, EBAS, ratio, tap and limits must be valid (non-negative impedance, positive base, ratio and taps, TPMX at or above TPMN)");
                    if (ib == vconv && !(setvl > 0)) throw c.Error($"the voltage converter's SETVL is {setvl}; its scheduled DC voltage must be above 0 kV");
                    if (ib != vconv && setvl == 0) throw c.Error($"converter {ib} has SETVL 0; an order-holding converter needs a power or current order");
                    var on = dcBuses.Where(x => x.AcBus == ib).ToList();
                    if (on.Count != 1) throw c.Error($"converter {ib} is on {on.Count} DC buses (DC bus records naming IB {ib}); it must be on exactly one");
                    convs.Add(new MtdcConverter(ib, n, amx, amn, rcv, xcv, ebas, tr, tap, tmx, tmn, stp, setvl, on[0].Number));
                }
                if (convs.Count(c => c.Bus == vconv) != 1) throw l.Error($"VCONV {vconv} is not one of the link's converters");
                if (convs.Select(c => c.Bus).Distinct().Count() != convs.Count) throw l.Error("two converters of the link are on the same AC bus");
                if (dead) { mtdcDead++; continue; }
                mtdcLinks.Add(new MtdcLink(name, mdc == 1 ? LccMode.Power : LccMode.Current, vconv, convs, dcBuses, dcLines));
            }
            if (mtdcLinks.Count > 0)
                _notes.Add($"{mtdcLinks.Count} multi-terminal DC link(s) are solved with the AC power flow: line-commutated converters on a resistive DC network, the voltage converter (VCONV) at its minimum angle holding the DC voltage nearest its schedule by tap, the others holding their power or current orders; VCMOD, VCONVN, MARG and DCPF are not applied (a converter that would need them fails the solve, naming it).");
            if (mtdcOff > 0) _notes.Add($"skipped {mtdcOff} blocked multi-terminal DC link(s) (MDC 0).");
            if (mtdcDead > 0) _notes.Add($"skipped {mtdcDead} multi-terminal DC link(s) with a converter on a dropped isolated (IDE 4) bus.");

            // --- FACTS devices ---------------------------------------------------------------------------------------
            var facts = new List<FactsDevice>();
            int factsDead = 0, factsSeries = 0, factsNothing = 0;
            var factsNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (PsseRecord r in _facts)
            {
                // Fields: NAME (N in revision 30), I, J, MODE, PDES, QDES, VSET, SHMX, TRMX, VTMN, VTMX, VSMX, IMX, LINX, RMPCT,
                // OWNER, SET1, SET2, VSREF, then (revision 33 on) REMOT (FCREG in 34 and 35), ...
                string name = V30 ? $"FACTS {r.Int(0, "N")}" : r.Str(0, "");
                if (name.Length == 0) throw r.Error("a FACTS device needs a name");
                if (!factsNames.Add(name)) throw r.Error($"FACTS device '{name}' is defined twice");
                int i = r.Int(1, "I"), j = r.Int(2, "J", 0);
                int mode = r.Code(3, "MODE", 1, 0, 1, 2, 3, 4, 5, 6, 7, 8);
                BusOf(r, i, "sending end bus I");
                if (j != 0) BusOf(r, j, "terminal end bus J");
                if (j == i) throw r.Error($"the terminal end bus J is the sending end bus {i}");
                double pdes = r.Real(4, "PDES", 0), qdes = r.Real(5, "QDES", 0), vset = r.Real(6, "VSET", 1.0), shmx = r.Real(7, "SHMX", 9999);
                double trmx = r.Real(8, "TRMX", 9999), vtmn = r.Real(9, "VTMN", 0.9), vtmx = r.Real(10, "VTMX", 1.1), vsmx = r.Real(11, "VSMX", 1.0);
                double imx = r.Real(12, "IMX", 0), set1 = r.Real(16, "SET1", 0), set2 = r.Real(17, "SET2", 0);
                int remot = V30 ? 0 : r.Int(19, _rev >= 34 ? "FCREG" : "REMOT", 0);
                if (shmx < 0 || trmx < 0 || imx < 0 || vsmx < 0) throw r.Error("SHMX, TRMX, IMX and VSMX must be 0 or above");
                if (shmx > 0 && !(vset > 0)) throw r.Error($"VSET is {vset}; the shunt element's voltage set-point must be above 0 pu");
                if (!(vtmn > 0) || !(vtmx >= vtmn)) throw r.Error($"VTMN {vtmn} and VTMX {vtmx} must make a band above 0 pu");
                if (mode >= 4) { Count("FACTS device in constant series voltage or IPFC mode (MODE 4 to 8)", r, true); continue; }
                if (remot != 0 && remot != i) { Count("FACTS device regulating a remote bus", r, true); continue; }
                if (j == 0 && mode == 3) throw r.Error("MODE 3 (constant series impedance) needs a terminal end bus J");
                if (!Live(i) || (j != 0 && !Live(j))) { factsDead++; continue; }
                FactsMode fm = j == 0 || mode == 2 ? FactsMode.Statcom : mode == 1 ? FactsMode.Upfc : FactsMode.SeriesImpedance;
                if (j != 0 && mode != 1)
                {
                    // The series element as a branch: bypassed (MODE 2) at PSS/E's zero-impedance reactance, or SET1 + j SET2.
                    double rs = mode == 3 ? set1 : 0, xs = mode == 3 ? set2 : 0;
                    if (rs == 0 && xs == 0) xs = ZeroImpedanceXPu;
                    branches.Add(new Branch(i, j, rs, xs, Name: $"FACTS {name} series"));
                    branchKeys.Add(new PsseBranchKey("facts", i, j, 0, "1", 0));
                    factsSeries++;
                }
                if (fm != FactsMode.Upfc && !(shmx > 0)) { factsNothing++; continue; }
                if (fm == FactsMode.Upfc && !(shmx > 0)) throw r.Error("a UPFC (MODE 1 with a terminal end bus) needs its shunt element (SHMX above 0) to supply its series element's active power");
                // RMPCT (field 15) shares the reactive power between devices holding one bus; checked by the solver only
                // when it is used (a device alone supplies everything, whatever its RMPCT).
                double rmpct = r.Real(14, "RMPCT", 100);
                facts.Add(new FactsDevice(name, fm, i, j, pdes, qdes, vset, shmx, trmx, vtmn, vtmx, vsmx, imx) { RmpctPercent = rmpct });
            }
            if (facts.Count > 0)
                _notes.Add($"{facts.Count} FACTS device(s) are solved with the AC power flow: the shunt element holds its bus at VSET within its current rating SHMX; a UPFC (MODE 1 with a terminal end bus) delivers PDES + j QDES to its terminal end bus, its sending end bus giving up PDES; the terminal voltage band and the series voltage, series current and bridge ratings are checked.");
            if (factsSeries > 0) _notes.Add($"{factsSeries} FACTS series element(s) bypassed (MODE 2) or at a constant impedance (MODE 3) are branches between their buses.");
            if (factsNothing > 0) _notes.Add($"{factsNothing} FACTS device(s) have no shunt element (SHMX 0); only their series branch, if any, is modelled.");
            if (factsDead > 0) _notes.Add($"skipped {factsDead} FACTS device(s) on a dropped isolated (IDE 4) bus.");

            // --- Induction machines ------------------------------------------------------------------------------
            // Fields: I, ID, STAT, SCODE, DCODE, AREA, ZONE, OWNER, TCODE, BCODE, MBASE, RATEKV, PCODE, PSET, H, A, B, D, E,
            // RA, XA, XM, R1, X1, R2, X2, X3, E1, SE1, E2, SE2, IA1, IA2, XAMULT. The torque terms (TCODE, H, A to E) are
            // dynamic data; the power flow needs the schedule and the equivalent circuit.
            var machines = new List<InductionMachine>();
            int imDead = 0;
            var imSeen = new Dictionary<(int, string), int>();
            foreach (PsseRecord r in _induction)
            {
                int bus = r.Int(0, "I");
                BusOf(r, bus, "induction machine bus");
                string id = r.Str(1, "1");
                if (imSeen.TryGetValue((bus, id), out int prior)) throw r.Error($"induction machine {id} at bus {bus} is defined twice (also at line {prior})");
                imSeen[(bus, id)] = r.Line;
                int dcode = r.Code(4, "DCODE", 2, 0, 1, 2, 3, 4, 5);
                int pcode = r.Code(12, "PCODE", 1, 1, 2);
                double mbase = r.Real(10, "MBASE", _sbase), ratekv = r.Real(11, "RATEKV", 0), pset = r.Real(13, "PSET", 0);
                double ra = r.Real(19, "RA", 0), xa = r.Real(20, "XA", 0), xm = r.Real(21, "XM", 2.5), r1 = r.Real(22, "R1", 999), x1 = r.Real(23, "X1", 999);
                double r2 = r.Real(24, "R2", 999), x2 = r.Real(25, "X2", 999), x3 = r.Real(26, "X3", 0);
                double se1 = r.Real(28, "SE1", 0), se2 = r.Real(30, "SE2", 0);
                if (mbase <= 0) throw r.Error($"MBASE is {mbase}; the machine base must be above 0");
                if (ratekv < 0) throw r.Error($"RATEKV is {ratekv}; it must be 0 (the bus base) or above");
                if (dcode != 0) { Count("induction machine with a standard design code (DCODE 1 to 5; its circuit comes from design tables)", r, true); continue; }
                bool secondCage = !((r2 == 0 && x2 == 0) || (r2 >= 999 && x2 >= 999)) || x3 != 0;
                if (secondCage) { Count("induction machine with a double cage (R2, X2, X3)", r, true); continue; }
                if (se1 != 0 || se2 != 0) { Count("induction machine with magnetic saturation (SE1, SE2)", r, true); continue; }
                if (ra < 0 || xa < 0 || !(xm > 0) || !(r1 > 0) || x1 < 0 || r1 >= 999 || x1 >= 999)
                    throw r.Error($"the equivalent circuit RA {ra}, XA {xa}, XM {xm}, R1 {r1}, X1 {x1} must have RA, XA, X1 at or above 0 and XM, R1 above 0 (999 means not given)");
                if (!Live(bus)) { imDead++; continue; }
                machines.Add(new InductionMachine(bus, $"induction machine {bus} {id}", pcode == 1 ? InductionPowerCode.Mechanical : InductionPowerCode.Electrical,
                    pset, mbase, ratekv, ra, xa, xm, r1, x1));
            }
            if (machines.Count > 0)
                _notes.Add($"{machines.Count} induction machine(s) are solved with the AC power flow: each at the slip on its stable branch that meets its schedule PSET (mechanical power out for PCODE 1, electrical power in for PCODE 2) at the solved voltage, from its single-cage equivalent circuit.");
            if (imDead > 0) _notes.Add($"skipped {imDead} induction machine(s) on dropped isolated (IDE 4) bus(es).");

            // --- Devices read past -------------------------------------------------------------------------------
            var refused = _unmodelled.Where(kv => kv.Value.InService > 0).ToList();
            if (refused.Count > 0 && !_options.AllowUnmodelled)
                throw new InvalidDataException("PSS/E RAW case '" + caseName + "' holds in-service devices GridSim does not model, so its power flow would differ from the file's: " +
                    string.Join("; ", refused.Select(kv => $"{kv.Value.InService} {kv.Key}(s) (first at line {kv.Value.FirstLine})")) +
                    ". Read it with unmodelled devices allowed (gridsim: --allow-unmodelled) to study the rest of the network.");
            foreach (var kv in _unmodelled)
            {
                if (kv.Value.InService > 0)
                    _notes.Add($"{kv.Value.InService} in-service {kv.Key}(s) read past and NOT modelled (unmodelled devices allowed); this power flow differs from the file's.");
                if (kv.Value.Out > 0)
                    _notes.Add($"{kv.Value.Out} out-of-service {kv.Key}(s) read past.");
            }
            if (_impedanceTables.Count > 0 && xfImpTable == 0) _notes.Add($"{_impedanceTables.Count} impedance correction table(s) read past; no in-service transformer uses one.");
            if (_multiSection > 0) _notes.Add($"{_multiSection} multi-section line grouping(s) read past (their sections are ordinary branches).");
            if (_interArea > 0) _notes.Add($"{_interArea} inter-area transfer(s) read past (scheduling data, not network).");
            if (_systemWide > 0) _notes.Add($"{_systemWide} system-wide data record(s) read past (PSS/E solution settings, not network).");
            if (_substations > 0) _notes.Add($"{_substations} node-breaker substation(s) read past; the bus-branch records are used.");

            // --- Assemble buses ----------------------------------------------------------------------------------
            var genBuses = new HashSet<int>(gens.Where(g => g.InService).Select(g => g.Bus));
            var buses = new List<Bus>();
            int slack = 0, demoted = 0, unusableLimits = 0, zipBuses = 0;
            foreach (int id in order)
            {
                PsseRecord r = busRec[id];
                meta[id] = MetaOf(r);
                if (isolated.Contains(id)) continue;
                int ide = r.Int(3, "IDE", 1);
                BusType type = ide == 3 ? BusType.Slack : ide == 2 ? BusType.Pv : BusType.Pq;
                if (type == BusType.Pv && !genBuses.Contains(id)) { type = BusType.Pq; demoted++; }
                if (type == BusType.Slack)
                {
                    slack++;
                    if (!genBuses.Contains(id)) _notes.Add($"swing (IDE 3) bus {id} has no in-service generator; it is kept as the reference.");
                }
                double baskv = r.Real(2, "BASKV", 0);
                if (baskv < 0) throw r.Error($"BASKV is {baskv}; a base voltage must be 0 or above");
                (double? vmin, double? vmax) = (null, null);
                if (!V30 && r.Has(9) && r.Has(10))
                {
                    double hi = r.Real(9, "NVHI"), lo = r.Real(10, "NVLO");
                    if (lo > 0 && hi > 0 && lo <= hi) (vmin, vmax) = (lo, hi);
                    else unusableLimits++;
                }
                acc.TryGetValue(id, out BusAcc? a);
                double pl = a?.Pl ?? 0, ql = a?.Ql ?? 0;
                double pd = pl, qd = ql;
                ZipLoad? zip = null;
                if (a?.Ip is double ip && a.Iq is double iq)
                {
                    pd = pl + ip;
                    qd = ql + iq;
                    if (ip != 0 && pd == 0) throw r.Error($"the loads' constant-current part (IP {ip}) cancels their constant-power part (PL {pl}), so the bus load cannot be written as a ZIP load");
                    if (iq != 0 && qd == 0) throw r.Error($"the loads' constant-current part (IQ {iq}) cancels their constant-power part (QL {ql}), so the bus load cannot be written as a ZIP load");
                    zip = new ZipLoad(
                        Ap: 0, Bp: ip != 0 ? ip / pd : 0, Cp: ip != 0 ? pl / pd : 1,
                        Aq: 0, Bq: iq != 0 ? iq / qd : 0, Cq: iq != 0 ? ql / qd : 1);
                    if (zip.VariesWithVoltage) zipBuses++; else zip = null;
                }
                string busName = r.Str(1);
                buses.Add(new Bus(id, type, Pd: pd, Qd: qd, Gs: a?.Gs ?? 0, Bs: a?.Bs ?? 0, BaseKv: baskv,
                    Vm: r.Real(BusVm, "VM", 1.0), VaDeg: r.Real(BusVa, "VA", 0), Name: busName.Length > 0 ? busName : null,
                    Zip: zip, VminPu: vmin, VmaxPu: vmax));
            }
            buses.AddRange(stars);
            if (slack == 0) throw new InvalidDataException($"PSS/E RAW case '{caseName}' has no swing (IDE 3) bus; the power flow would be singular.");
            if (slack > 1) _notes.Add($"{slack} swing (IDE 3) buses; treating as a multi-island system.");
            if (isolated.Count > 0) _notes.Add($"dropped {isolated.Count} isolated (IDE 4) bus(es) and any connected elements.");
            if (demoted > 0) _notes.Add($"solved {demoted} PV (IDE 2) bus(es) with no in-service generator as PQ, as PSS/E and MATPOWER do.");
            if (unusableLimits > 0) _notes.Add($"{unusableLimits} bus(es) carry NVHI / NVLO that are not usable as voltage limits; the global band applies to them.");
            if (zipBuses > 0) _notes.Add($"{zipBuses} bus(es) are ZIP loads (constant-current parts).");

            // --- Metadata ----------------------------------------------------------------------------------------
            var areaNames = new Dictionary<int, string>();
            foreach (PsseRecord r in _areas) areaNames[r.Int(0, "I")] = r.Str(4);
            var zoneNames = new Dictionary<int, string>();
            foreach (PsseRecord r in _zones) zoneNames[r.Int(0, "I")] = r.Str(1);
            var ownerNames = new Dictionary<int, string>();
            foreach (PsseRecord r in _owners) ownerNames[r.Int(0, "I")] = r.Str(1);

            var model = new GridModel
            {
                Name = caseName,
                BaseMva = _sbase,
                FrequencyHz = freq,
                Buses = buses,
                Generators = gens,
                Branches = branches,
                Oltcs = oltcs is null ? Array.Empty<Oltc>() : oltcs,
                LccLinks = lcc,
                VscLinks = vscLinks,
                MtdcLinks = mtdcLinks,
                FactsDevices = facts,
                StaticVarCompensators = svcs,
                InductionMachines = machines,
                TransformerFlowControls = flowControls,
            };
            var errors = model.Validate().Where(d => d.Severity == ValidationSeverity.Error).Select(d => d.Message).ToList();
            if (errors.Count > 0)
                throw new InvalidDataException($"PSS/E RAW case '{caseName}' is invalid:" + Environment.NewLine + "  " +
                    string.Join(Environment.NewLine + "  ", errors));

            return new PsseParseResult(model, _notes)
            {
                Revision = _rev,
                Titles = titles,
                BusMeta = meta,
                AreaNames = areaNames,
                ZoneNames = zoneNames,
                OwnerNames = ownerNames,
                MachineKeys = machineKeys,
                BranchKeys = branchKeys,
                SkippedBranchKeys = skippedKeys,
                StarPointBuses = starIds,
                SwitchedShunts = banks,
            };
        }
    }
}
