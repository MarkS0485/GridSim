using System.Globalization;
using System.Text;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// Writes a <see cref="GridModel"/> as a PSS/E RAW file (revision 33, 34 or 35) that <see cref="PsseRawParser"/> reads
/// back to the same power flow (Tranche A, October 2026). Used by <c>gridsim convert</c> (an output ending in .raw), to
/// make the reader's round-trip fixtures from the MATPOWER cases and to prove each revision's layout.
/// </summary>
/// <remarks>
/// <para><b>Exactness.</b> Every number is written in round-trip form ("R", invariant culture). Impedances are on the
/// system base (CZ 1), ratios in per unit of the bus base (CW 1, WINDV2 = 1), so the reader gives back the same doubles.
/// Buses and generators keep model order; branches are written lines first, then transformers (the file has separate
/// sections), then the series elements of FACTS devices (written in their FACTS records), and
/// <see cref="PsseWriteResult.BranchOrder"/> says which model branch each read-back branch is. Each bus gets at most one
/// load (Pd, Qd) and one fixed shunt (Gs, Bs).</para>
/// <para><b>Devices (export pack, October 2026).</b> The devices the reader models are written in their own sections so
/// that the reader (with tap and shunt control applied, as <c>gridsim convert</c> reads its output back) rebuilds the same
/// records: two-terminal LCC, VSC and multi-terminal DC links, FACTS devices (a STATCOM as MODE 1 with no terminal bus or
/// MODE 2 with a bypassed series element, a UPFC as MODE 1, a constant series impedance as MODE 3 with SET1, SET2 from its
/// series branch), static var compensators (a MODSW 2 switched shunt whose BINIT the reader adds back to the bus shunt),
/// induction machines (DCODE 0, single cage), on-load tap changers (COD 1) and transformer flow controls (COD 2 and 3).
/// A tap changer or flow control is written only when the reader's formulas give back its numbers to the bit:
/// target (VMA + VMI) / 2 and deadband (VMA - VMI) / 2, step (RMA - RMI) / (NTP - 1) with a whole NTP, a transformer
/// with a name of its own and no impedance correction table. One that cannot be is left at its present tap and listed
/// in the notes, so the read-back solve shows the difference rather than hiding it. A static var compensator whose bus
/// shunt cannot be split into a fixed part and BINIT exactly is listed likewise.</para>
/// <para><b>What RAW cannot carry exactly is refused</b>, naming the element: a varying ZIP load, a transformer with
/// line charging and a ratio other than 1 or a phase shift, a branch with R = X = 0 (the reader would give it the
/// zero-impedance reactance), an infinite generator limit, a name holding both kinds of quote, zero-sequence data (RAW
/// has no sequence data). A name holding a single quote is written in double quotes. A transformer with ratio 1, no
/// phase shift and line charging is written as a line (PSS/E transformers carry no line charging; its admittance stamp
/// is the same), with a note. What the format has no place for (generator dynamics and fault data, boundaries, layout,
/// the asset bundles) is listed in the notes.</para>
/// </remarks>
public static class PsseRawWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The revisions this writer writes. Revision 30 is read (<see cref="PsseRawParser.SupportedRevisions"/>) but
    /// not written: the writer exists to make round-trip fixtures and prove layouts, and 33 is the oldest layout a
    /// current PSS/E reads.</summary>
    public static readonly IReadOnlyList<int> WritableRevisions = new[] { 33, 34, 35 };

    /// <summary>Write <paramref name="model"/> as RAW text in <paramref name="revision"/>'s layout.</summary>
    public static PsseWriteResult Write(GridModel model, int revision = 33)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.ZeroSequenceMutuals.Count > 0 || model.AllGenerators.Any(g => g.ZeroSequenceZPu is not null))
            throw new InvalidDataException(
                $"PSS/E RAW writer: the model has zero-sequence mutual couplings ({model.ZeroSequenceMutuals.Count}) or machine zero-sequence impedances ({model.AllGenerators.Count(g => g.ZeroSequenceZPu is not null)}), " +
                "which a RAW file cannot hold (sequence data belongs in a SEQ file); the written case's fault currents would differ from the model's.");
        if (!WritableRevisions.Contains(revision))
            throw new ArgumentOutOfRangeException(nameof(revision), revision, $"PSS/E revision {revision} is not supported; supported: {string.Join(", ", WritableRevisions)}.");
        bool v34 = revision >= 34, v35 = revision >= 35;
        var notes = new List<string>();
        var sb = new StringBuilder();

        // Bug hunt 2026-10-29, finding 47: the reader trims every name (PsseRecord.Str), so a name written with a leading
        // or trailing blank reads back without it, and a comparison by name after the round trip (InFileOrder, the OLTC
        // transformer lookup) can miss. The name is still written as given; the asymmetry is now stated in the notes
        // instead of being silent.
        static bool OuterBlank(string? s) => !string.IsNullOrEmpty(s) && s.Trim().Length != s.Length;
        int outerBlankNames = model.Buses.Count(x => OuterBlank(x.Name)) + model.Branches.Count(x => OuterBlank(x.Name))
            + model.LccLinks.Count(x => OuterBlank(x.Name)) + model.VscLinks.Count(x => OuterBlank(x.Name))
            + model.MtdcLinks.Count(x => OuterBlank(x.Name)) + model.FactsDevices.Count(x => OuterBlank(x.Name));
        if (outerBlankNames > 0)
            notes.Add($"{outerBlankNames} name(s) begin or end with a blank; the reader trims names, so they read back without the outer blanks.");

        // FACTS series elements: the branches the reader makes for a MODE 2 or 3 device with a terminal bus. They are
        // written in the FACTS record, not as lines.
        var factsSeries = new Dictionary<FactsDevice, int>();
        var seriesIndex = new HashSet<int>();
        foreach (FactsDevice d in model.FactsDevices)
        {
            if (d.TerminalBus == 0 || d.Mode == FactsMode.Upfc) continue;
            string sname = $"FACTS {d.Name} series";
            int[] hits = Enumerable.Range(0, model.Branches.Count).Where(k => model.Branches[k].Name == sname).ToArray();
            if (hits.Length != 1)
                throw new InvalidDataException($"PSS/E RAW writer: FACTS device '{d.Name}' (terminal bus {d.TerminalBus}) needs exactly one series branch named '{sname}'; the model has {hits.Length}.");
            Branch s = model.Branches[hits[0]];
            if (s.From != d.SendingBus || s.To != d.TerminalBus || s.B != 0 || s.IsTransformer || !s.InService
                || (s.TapRatio != 0 && s.TapRatio != 1) || s.PhaseShiftDeg != 0 || s.RateAMva != 0 || s.RateBMva != 0 || s.RateCMva != 0)
                throw new InvalidDataException($"PSS/E RAW writer: FACTS device '{d.Name}''s series branch '{sname}' is not a plain in-service series impedance from bus {d.SendingBus} to bus {d.TerminalBus}, so its FACTS record cannot carry it.");
            if (d.Mode == FactsMode.Statcom && !(s.R == 0 && s.X == PsseRawParser.ZeroImpedanceXPu))
                throw new InvalidDataException($"PSS/E RAW writer: FACTS device '{d.Name}' is a STATCOM with a series element of impedance {N(s.R, "R")} + j{N(s.X, "X")}; MODE 2 bypasses it at the zero-impedance reactance, so it cannot be written.");
            factsSeries[d] = hits[0];
            seriesIndex.Add(hits[0]);
        }

        // Static var compensators: the reader adds each one's BINIT to its bus shunt after the fixed shunts, so the fixed
        // shunt written is the bus shunt less the BINITs, when that gives the bus shunt back to the bit.
        var svcByBus = model.StaticVarCompensators.GroupBy(c => c.Bus).ToDictionary(g => g.Key, g => g.ToList());
        var fixedBs = new Dictionary<int, double>();
        var svcWritten = new List<StaticVarCompensator>();
        int svcInexact = 0;
        foreach (Bus b in model.Buses)
        {
            if (!svcByBus.TryGetValue(b.Id, out List<StaticVarCompensator>? list)) continue;
            double rest = b.Bs;
            foreach (StaticVarCompensator c in list) rest -= c.BinitMvar;
            // The reader: Bs = (fixed shunt, if one is written) + BINIT1 + BINIT2 ...
            bool writeFixed = rest != 0 || b.Gs != 0;
            double? back = writeFixed ? rest : null;
            foreach (StaticVarCompensator c in list) back = back is double a ? a + c.BinitMvar : c.BinitMvar;
            if (BitConverter.DoubleToInt64Bits(back!.Value) == BitConverter.DoubleToInt64Bits(b.Bs)) { fixedBs[b.Id] = rest; svcWritten.AddRange(list); }
            else svcInexact += list.Count;
        }
        if (svcInexact > 0) notes.Add($"{svcInexact} static var compensator(s) NOT written: their bus shunt cannot be split into a fixed part and BINIT that the reader adds back to the bit.");

        sb.Append("0, ").Append(N(model.BaseMva, "the system base")).Append(", ").Append(revision).Append(", 0, 1, ")
          .Append(N(model.FrequencyHz, "the nominal frequency")).Append("     / PSS/E-").Append(revision).Append(" RAW written by GridSim\n");
        sb.Append(OneLine(model.Name)).Append('\n');
        sb.Append("Written by GridSim (PsseRawWriter)\n");
        if (v35) sb.Append("0 / END OF SYSTEM-WIDE DATA, BEGIN BUS DATA\n");

        // --- Buses -------------------------------------------------------------------------------------------------
        int partialLimits = 0;
        foreach (Bus b in model.Buses)
        {
            if (b.Zip is { VariesWithVoltage: true })
                throw new InvalidDataException($"bus {b.Id} has a voltage-dependent (ZIP) load; this writer does not express it as PSS/E constant-current and constant-admittance parts.");
            int ide = b.Type switch { BusType.Slack => 3, BusType.Pv => 2, _ => 1 };
            sb.Append(b.Id).Append(", ").Append(Q(b.Name ?? "", $"bus {b.Id}'s name")).Append(", ").Append(N(b.BaseKv, $"bus {b.Id} base kV"))
              .Append(", ").Append(ide).Append(", 1, 1, 1, ").Append(N(b.Vm, $"bus {b.Id} Vm")).Append(", ").Append(N(b.VaDeg, $"bus {b.Id} Va"));
            if (b.VmaxPu is double hi && b.VminPu is double lo)
                sb.Append(", ").Append(N(hi, "NVHI")).Append(", ").Append(N(lo, "NVLO")).Append(", ").Append(N(hi, "EVHI")).Append(", ").Append(N(lo, "EVLO"));
            else if (b.VmaxPu is not null || b.VminPu is not null) partialLimits++;
            sb.Append('\n');
        }
        if (partialLimits > 0) notes.Add($"{partialLimits} bus(es) have only one of the two per-bus voltage limits; RAW holds them as a pair, so neither was written.");
        Terminator(sb, "BUS", "LOAD");

        // --- Loads: one per bus with demand ------------------------------------------------------------------------
        foreach (Bus b in model.Buses)
        {
            if (b.Pd == 0 && b.Qd == 0) continue;
            sb.Append(b.Id).Append(", '1', 1, 1, 1, ").Append(N(b.Pd, $"bus {b.Id} Pd")).Append(", ").Append(N(b.Qd, $"bus {b.Id} Qd"))
              .Append(", 0, 0, 0, 0, 1, 1, 0");
            if (v34) sb.Append(", 0, 0, 0");
            if (v35) sb.Append(", ''");
            sb.Append('\n');
        }
        Terminator(sb, "LOAD", "FIXED SHUNT");

        // --- Fixed shunts: one per bus with a shunt (less any static var compensator's BINIT) -----------------------
        foreach (Bus b in model.Buses)
        {
            double bs = fixedBs.TryGetValue(b.Id, out double rest) ? rest : b.Bs;
            if (b.Gs == 0 && bs == 0) continue;
            sb.Append(b.Id).Append(", '1', 1, ").Append(N(b.Gs, $"bus {b.Id} Gs")).Append(", ").Append(N(bs, $"bus {b.Id} Bs")).Append('\n');
        }
        Terminator(sb, "FIXED SHUNT", "GENERATOR");

        // --- Generators: the whole fleet, machine IDs 1, 2, ... per bus ----------------------------------------------
        var perBus = new Dictionary<int, int>();
        int dynamics = 0;
        foreach (Generator g in model.AllGenerators)
        {
            int n = perBus.TryGetValue(g.Bus, out int c) ? c + 1 : 1;
            perBus[g.Bus] = n;
            string who = $"generator {n} at bus {g.Bus}";
            if (g.H != 0 || g.SubtransientXdPu > 0 || g.DynamicsClass is not null || g.Type.Length > 0) dynamics++;
            sb.Append(g.Bus).Append(", '").Append(n).Append("', ").Append(N(g.Pg, who + " PG")).Append(", ").Append(N(g.Qg, who + " QG"))
              .Append(", ").Append(F(g.Qmax, who + " QT")).Append(", ").Append(F(g.Qmin, who + " QB")).Append(", ").Append(N(g.Vg, who + " VS"))
              .Append(", 0, ");
            if (v35) sb.Append("0, ");
            sb.Append(N(g.MBase, who + " MBASE")).Append(", 0, 1, 0, 0, 1, ").Append(g.InService ? 1 : 0).Append(", 100, ")
              .Append(F(g.Pmax, who + " PT")).Append(", ").Append(F(g.Pmin, who + " PB"));
            if (v35) sb.Append(", 0");
            sb.Append(", 1, 1, 0, 1, 0, 1, 0, 1, 0, 1\n");
        }
        if (dynamics > 0) notes.Add($"{dynamics} generator(s) carry dynamics or fault data (inertia, type, X''d, a dynamics class); RAW holds the power-flow record only.");
        Terminator(sb, "GENERATOR", "BRANCH");

        // --- Branches: lines (and ratio-1 transformers with charging), then transformers --------------------------------
        var lines = new List<int>();
        var xfmrs = new List<int>();
        int chargedAsLine = 0;
        for (int k = 0; k < model.Branches.Count; k++)
        {
            if (seriesIndex.Contains(k)) continue;   // a FACTS series element, written in its FACTS record
            Branch br = model.Branches[k];
            double tap = br.TapRatio == 0 ? 1.0 : br.TapRatio;
            if (br.R == 0 && br.X == 0)
                throw new InvalidDataException($"branch {k + 1} ({br.From}-{br.To}) has R = X = 0; the reader would give it the zero-impedance reactance, so it is not written.");
            bool plain = tap == 1.0 && br.PhaseShiftDeg == 0.0;
            if (!br.IsTransformer && plain) lines.Add(k);
            else if (plain && br.B != 0) { lines.Add(k); chargedAsLine++; }
            else if (br.B != 0)
                throw new InvalidDataException($"transformer {k + 1} ({br.From}-{br.To}) has line charging B = {N(br.B, "B")} with a ratio or phase shift; a PSS/E transformer has no line charging.");
            else xfmrs.Add(k);
        }
        if (chargedAsLine > 0) notes.Add($"{chargedAsLine} transformer(s) with ratio 1, no phase shift and line charging written as lines (PSS/E transformers carry no line charging); the admittance is the same, but they read back as lines.");

        // Tap changers and flow controls, by transformer name, written when the reader gives their numbers back exactly.
        (Dictionary<int, string> control, List<string> controlNotes) = Controls(model, xfmrs);
        notes.AddRange(controlNotes);

        var circuits = new Dictionary<(int, int), int>();
        string Ckt(Branch br)
        {
            (int, int) key = br.From < br.To ? (br.From, br.To) : (br.To, br.From);
            int n = circuits.TryGetValue(key, out int c) ? c + 1 : 1;
            circuits[key] = n;
            return n.ToString(Inv);
        }
        int open = 0;
        foreach (int k in lines)
        {
            Branch br = model.Branches[k];
            string who = $"branch {k + 1}";
            if (!br.InService) open++;
            sb.Append(br.From).Append(", ").Append(br.To).Append(", '").Append(Ckt(br)).Append("', ").Append(N(br.R, who + " R")).Append(", ")
              .Append(N(br.X, who + " X")).Append(", ").Append(N(br.B, who + " B")).Append(", ");
            if (v34) sb.Append(Q(br.Name ?? "", who + " name")).Append(", ");
            sb.Append(Ratings(br, who, v34)).Append(", 0, 0, 0, 0, ").Append(br.InService ? 1 : 0).Append(", 1, 0, 1, 1\n");
        }
        Terminator(sb, "BRANCH", v34 ? "SYSTEM SWITCHING DEVICE" : "TRANSFORMER");
        if (v34) Terminator(sb, "SYSTEM SWITCHING DEVICE", "TRANSFORMER");

        foreach (int k in xfmrs)
        {
            Branch br = model.Branches[k];
            string who = $"transformer {k + 1}";
            if (!br.InService) open++;
            double tap = br.TapRatio == 0 ? 1.0 : br.TapRatio;
            sb.Append(br.From).Append(", ").Append(br.To).Append(", 0, '").Append(Ckt(br)).Append("', 1, 1, 1, 0, 0, 2, ")
              .Append(Q(br.Name ?? "", who + " name")).Append(", ").Append(br.InService ? 1 : 0).Append(", 1, 1, 0, 1, 0, 1, 0, 1, ''\n");
            sb.Append(N(br.R, who + " R")).Append(", ").Append(N(br.X, who + " X")).Append(", ").Append(N(model.BaseMva, "SBASE1-2")).Append('\n');
            sb.Append(N(tap, who + " ratio")).Append(", 0, ").Append(N(br.PhaseShiftDeg, who + " phase shift")).Append(", ")
              .Append(Ratings(br, who, v34)).Append(", ");
            // COD, CONT, [NODE in 35], RMA, RMI, VMA, VMI, NTP: a control when one is written, else none.
            if (control.TryGetValue(k, out string? cod))
            {
                string[] f = cod.Split(", ");   // COD, CONT, RMA, RMI, VMA, VMI, NTP
                sb.Append(f[0]).Append(", ").Append(f[1]).Append(", ");
                if (v35) sb.Append("0, ");
                sb.Append(string.Join(", ", f.Skip(2))).Append(", 0, 0, 0, 0\n");
            }
            else
            {
                sb.Append("0, 0, ");
                if (v35) sb.Append("0, ");
                sb.Append("1.1, 0.9, 1.1, 0.9, 33, 0, 0, 0, 0\n");
            }
            sb.Append("1, 0\n");
        }
        if (open > 0) notes.Add($"{open} out-of-service branch(es) written with status 0; the reader skips them.");
        Terminator(sb, "TRANSFORMER", "AREA");
        Terminator(sb, "AREA", "TWO-TERMINAL DC");

        // --- Two-terminal DC links ---------------------------------------------------------------------------------
        foreach (LccDcLink l in model.LccLinks)
        {
            string who = $"two-terminal DC link '{l.Name}'";
            int mdc = l.Mode switch
            {
                LccMode.Power => 1,
                LccMode.Current => 2,
                _ => throw new InvalidDataException($"PSS/E RAW writer: {who} is blocked; the reader skips a blocked link, so it is not written."),
            };
            sb.Append(Q(l.Name, who + " name")).Append(", ").Append(mdc).Append(", ").Append(N(l.ResistanceOhms, who + " RDC")).Append(", ")
              .Append(N(l.Setpoint, who + " SETVL")).Append(", ").Append(N(l.ScheduledDcKv, who + " VSCHD")).Append(", ")
              .Append(N(l.ModeSwitchDcKv, who + " VCMOD")).Append(", ").Append(N(l.CompoundingOhms, who + " RCOMP")).Append(", ")
              .Append(N(l.MarginPu, who + " DELTI")).Append(", 'I', 0, 20, 1\n");
            LccLine(sb, l.Rectifier, who + " rectifier", v35);
            LccLine(sb, l.Inverter, who + " inverter", v35);
        }
        Terminator(sb, "TWO-TERMINAL DC", "VOLTAGE SOURCE CONVERTER");

        // --- VSC DC links ------------------------------------------------------------------------------------------
        foreach (VscDcLink l in model.VscLinks)
        {
            string who = $"VSC DC link '{l.Name}'";
            sb.Append(Q(l.Name, who + " name")).Append(", 1, ").Append(N(l.ResistanceOhms, who + " RDC")).Append('\n');
            VscLine(sb, l.First, who + " first converter", v35);
            VscLine(sb, l.Second, who + " second converter", v35);
        }
        Terminator(sb, "VOLTAGE SOURCE CONVERTER", "IMPEDANCE CORRECTION");
        Terminator(sb, "IMPEDANCE CORRECTION", "MULTI-TERMINAL DC");

        // --- Multi-terminal DC links -------------------------------------------------------------------------------
        foreach (MtdcLink l in model.MtdcLinks)
        {
            string who = $"multi-terminal DC link '{l.Name}'";
            int mdc = l.Mode switch
            {
                LccMode.Power => 1,
                LccMode.Current => 2,
                _ => throw new InvalidDataException($"PSS/E RAW writer: {who} is blocked; the reader skips a blocked link, so it is not written."),
            };
            sb.Append(Q(l.Name, who + " name")).Append(", ").Append(l.Converters.Count).Append(", ").Append(l.DcBuses.Count).Append(", ")
              .Append(l.Lines.Count).Append(", ").Append(mdc).Append(", ").Append(l.VoltageConverterBus).Append(", 0, 0\n");
            foreach (MtdcConverter c in l.Converters)
            {
                string cw = $"{who} converter at bus {c.Bus}";
                if (l.DcBuses.Count(b => b.AcBus == c.Bus) != 1 || l.DcBuses.First(b => b.AcBus == c.Bus).Number != c.DcBus)
                    throw new InvalidDataException($"PSS/E RAW writer: {cw} must sit on exactly one DC bus naming its AC bus, and that DC bus must be its DC bus {c.DcBus}.");
                sb.Append(c.Bus).Append(", ").Append(c.Bridges).Append(", ").Append(N(c.AngleMaxDeg, cw + " ANGMX")).Append(", ").Append(N(c.AngleMinDeg, cw + " ANGMN"))
                  .Append(", ").Append(N(c.RcOhms, cw + " RC")).Append(", ").Append(N(c.XcOhms, cw + " XC")).Append(", ").Append(N(c.BaseKv, cw + " EBAS"))
                  .Append(", ").Append(N(c.TransformerRatio, cw + " TR")).Append(", ").Append(N(c.Tap, cw + " TAP")).Append(", ").Append(N(c.TapMax, cw + " TPMX"))
                  .Append(", ").Append(N(c.TapMin, cw + " TPMN")).Append(", ").Append(N(c.TapStep, cw + " TSTP")).Append(", ").Append(N(c.Setpoint, cw + " SETVL"))
                  .Append(", 1, 0, 1\n");
            }
            foreach (MtdcDcBus b in l.DcBuses)
                sb.Append(b.Number).Append(", ").Append(b.AcBus).Append(", 1, 1, ").Append(Q(b.Name ?? "", $"{who} DC bus {b.Number} name")).Append(", 0, ")
                  .Append(N(b.GroundOhms, $"{who} DC bus {b.Number} RGRND")).Append(", 1\n");
            var dcCkt = new Dictionary<(int, int), int>();
            foreach (MtdcDcLine dl in l.Lines)
            {
                (int, int) key = dl.FromDcBus < dl.ToDcBus ? (dl.FromDcBus, dl.ToDcBus) : (dl.ToDcBus, dl.FromDcBus);
                int n = dcCkt.TryGetValue(key, out int c) ? c + 1 : 1;
                dcCkt[key] = n;
                sb.Append(dl.FromDcBus).Append(", ").Append(dl.ToDcBus).Append(", '").Append(n).Append("', 1, ")
                  .Append(N(dl.ResistanceOhms, $"{who} DC line {dl.FromDcBus}-{dl.ToDcBus} RDC")).Append(", 0\n");
            }
        }
        Terminator(sb, "MULTI-TERMINAL DC", "MULTI-SECTION LINE");
        Terminator(sb, "MULTI-SECTION LINE", "ZONE");
        Terminator(sb, "ZONE", "INTER-AREA TRANSFER");
        Terminator(sb, "INTER-AREA TRANSFER", "OWNER");
        Terminator(sb, "OWNER", "FACTS CONTROL DEVICE");

        // --- FACTS devices -----------------------------------------------------------------------------------------
        var seriesOrder = new List<int>();
        foreach (FactsDevice d in model.FactsDevices)
        {
            string who = $"FACTS device '{d.Name}'";
            int mode;
            double set1 = 0, set2 = 0;
            if (d.Mode == FactsMode.Upfc) mode = 1;
            else if (d.TerminalBus == 0) mode = 1;   // a STATCOM with no terminal bus: any mode reads as a STATCOM
            else
            {
                Branch s = model.Branches[factsSeries[d]];
                seriesOrder.Add(factsSeries[d]);
                if (d.Mode == FactsMode.Statcom) mode = 2;
                else { mode = 3; set1 = s.R; set2 = s.X; }
            }
            sb.Append(Q(d.Name, who + " name")).Append(", ").Append(d.SendingBus).Append(", ").Append(d.TerminalBus).Append(", ").Append(mode).Append(", ")
              .Append(N(d.PdesMw, who + " PDES")).Append(", ").Append(N(d.QdesMvar, who + " QDES")).Append(", ").Append(N(d.VsetPu, who + " VSET")).Append(", ")
              .Append(N(d.ShuntMaxMva, who + " SHMX")).Append(", ").Append(N(d.BridgeMaxMw, who + " TRMX")).Append(", ").Append(N(d.TerminalVminPu, who + " VTMN")).Append(", ")
              .Append(N(d.TerminalVmaxPu, who + " VTMX")).Append(", ").Append(N(d.SeriesVmaxPu, who + " VSMX")).Append(", ").Append(N(d.SeriesImaxMva, who + " IMX"))
              .Append(", 0.05, ").Append(N(d.RmpctPercent, who + " RMPCT")).Append(", 1, ").Append(N(set1, who + " SET1")).Append(", ").Append(N(set2, who + " SET2")).Append(", 0, 0, ");
            if (v35) sb.Append("0, ");
            sb.Append("''\n");
        }
        Terminator(sb, "FACTS CONTROL DEVICE", "SWITCHED SHUNT");

        // --- Switched shunts: the static var compensators (MODSW 2) -------------------------------------------------
        var svcIds = new Dictionary<int, int>();
        int svcRenamed = 0;
        foreach (StaticVarCompensator c in svcWritten)
        {
            string who = $"static var compensator '{c.Name}'";
            int n = svcIds.TryGetValue(c.Bus, out int k) ? k + 1 : 1;
            svcIds[c.Bus] = n;
            string expected = v35 ? $"SVC {c.Bus} {n}" : $"SVC {c.Bus}";
            if (c.Name != expected) svcRenamed++;
            sb.Append(c.Bus).Append(", ");
            if (v35) sb.Append('\'').Append(n).Append("', ");
            sb.Append("2, 0, 1, ").Append(N(c.VhiPu, who + " VSWHI")).Append(", ").Append(N(c.VloPu, who + " VSWLO")).Append(", 0, ");
            if (v35) sb.Append("0, ");
            sb.Append(N(c.RmpctPercent, who + " RMPCT")).Append(", '', ").Append(N(c.BinitMvar, who + " BINIT"));
            if (c.BminMvar < 0) { sb.Append(", "); if (v35) sb.Append("1, "); sb.Append("1, ").Append(N(c.BminMvar, who + " Bmin")); }
            if (c.BmaxMvar > 0) { sb.Append(", "); if (v35) sb.Append("1, "); sb.Append("1, ").Append(N(c.BmaxMvar, who + " Bmax")); }
            sb.Append('\n');
        }
        if (svcRenamed > 0) notes.Add($"{svcRenamed} static var compensator(s) read back under the reader's name ('SVC <bus>'); names are not part of the power flow.");
        Terminator(sb, "SWITCHED SHUNT", "GNE DEVICE");
        Terminator(sb, "GNE DEVICE", "INDUCTION MACHINE");

        // --- Induction machines (custom design, single cage) --------------------------------------------------------
        var imIds = new Dictionary<int, int>();
        int imRenamed = 0;
        foreach (InductionMachine m in model.InductionMachines)
        {
            string who = $"induction machine '{m.Name}'";
            int n = imIds.TryGetValue(m.Bus, out int k) ? k + 1 : 1;
            imIds[m.Bus] = n;
            if (m.Name != $"induction machine {m.Bus} {n}") imRenamed++;
            sb.Append(m.Bus).Append(", '").Append(n).Append("', 1, 1, 0, 1, 1, 1, 1, 1, ").Append(N(m.MBase, who + " MBASE")).Append(", ")
              .Append(N(m.RatedKv, who + " RATEKV")).Append(", ").Append(m.PowerCode == InductionPowerCode.Mechanical ? 1 : 2).Append(", ")
              .Append(N(m.ScheduledMw, who + " PSET")).Append(", 1, 1, 0, 0, 0, ").Append(N(m.RaPu, who + " RA")).Append(", ").Append(N(m.XaPu, who + " XA"))
              .Append(", ").Append(N(m.XmPu, who + " XM")).Append(", ").Append(N(m.R1Pu, who + " R1")).Append(", ").Append(N(m.X1Pu, who + " X1"))
              .Append(", 0, 0, 0, 1, 0, 1.2, 0, 0, 0, 1\n");
        }
        if (imRenamed > 0) notes.Add($"{imRenamed} induction machine(s) read back under the reader's name ('induction machine <bus> <id>'); names are not part of the power flow.");
        Terminator(sb, "INDUCTION MACHINE", v35 ? "SUBSTATION" : null);
        if (v35) Terminator(sb, "SUBSTATION", null);
        sb.Append("Q\n");

        if (model.Boundaries.Count > 0) notes.Add($"{model.Boundaries.Count} boundary(ies) not written: RAW has no boundary element.");
        if (model.AssetDynamics is not null || model.Protections is not null) notes.Add("the asset-dynamics and protection bundles are not written: RAW has no place for them.");

        var order = new List<int>(lines);
        order.AddRange(xfmrs);
        order.AddRange(seriesOrder);
        return new PsseWriteResult(sb.ToString(), notes, order);
    }

    /// <summary>
    /// <paramref name="model"/> in the order <paramref name="written"/>'s file holds it, the model the reader rebuilds from
    /// that file: branches in <see cref="PsseWriteResult.BranchOrder"/> (lines, transformers, FACTS series elements),
    /// tap changers and flow controls in the order of their transformers there, and static var compensators in bus order
    /// (the switched shunt section's order). The same network with the same records; only the order differs, and the
    /// order of the branches can change the last bits of a solve (the admittance sums run in branch order), so a
    /// read-back is compared with this, not with <paramref name="model"/> as given.
    /// </summary>
    public static GridModel InFileOrder(GridModel model, PsseWriteResult written)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(written);
        List<Branch> branches = written.BranchOrder.Select(k => model.Branches[k]).ToList();
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < branches.Count; k++)
            if (branches[k].Name is string nm) position.TryAdd(nm, k);
        int At(string name) => position.TryGetValue(name, out int k) ? k : int.MaxValue;
        var busPos = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) busPos.TryAdd(model.Buses[i].Id, i);
        return model.CopyWith(
            branches: branches,
            oltcs: model.Oltcs.Select((o, i) => (o, i)).OrderBy(t => At(t.o.Transformer)).ThenBy(t => t.i).Select(t => t.o).ToList(),
            transformerFlowControls: model.TransformerFlowControls.Select((f, i) => (f, i)).OrderBy(t => At(t.f.Transformer)).ThenBy(t => t.i).Select(t => t.f).ToList(),
            staticVarCompensators: model.StaticVarCompensators.Select((c, i) => (c, i)).OrderBy(t => busPos.GetValueOrDefault(t.c.Bus, int.MaxValue)).ThenBy(t => t.i).Select(t => t.c).ToList());
    }

    /// <summary>
    /// The COD fields ("COD, CONT, RMA, RMI, VMA, VMI, NTP") of each transformer (model branch index) whose tap changer or
    /// flow control the reader rebuilds to the bit, and notes listing the ones it cannot (left at their present tap).
    /// The reader (two-winding unit, CW 1, WINDV2 1): an OLTC's target is (VMA + VMI) / 2, its deadband (VMA - VMI) / 2,
    /// its limits RMI and RMA and its step (RMA - RMI) / (NTP - 1); a flow control's band VMI..VMA, its limits RMI..RMA and
    /// the same step. Each is checked by recomputing it here with the reader's arithmetic.
    /// </summary>
    private static (Dictionary<int, string> Fields, List<string> Notes) Controls(GridModel model, List<int> xfmrs)
    {
        var fields = new Dictionary<int, string>();
        var notes = new List<string>();
        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int k = 0; k < model.Branches.Count; k++)
            if (model.Branches[k].Name is string nm) (byName.TryGetValue(nm, out List<int>? l) ? l : byName[nm] = new List<int>()).Add(k);
        var xf = new HashSet<int>(xfmrs);
        var why = new Dictionary<string, int>(StringComparer.Ordinal);
        void Skip(string reason) => why[reason] = why.GetValueOrDefault(reason) + 1;

        int? Target(string transformer)
        {
            if (!byName.TryGetValue(transformer, out List<int>? hits)) { Skip("name no branch"); return null; }
            if (hits.Count != 1) { Skip("name a transformer whose name another branch shares (the reader would rename it)"); return null; }
            if (!xf.Contains(hits[0])) { Skip("are on a branch written as a line (ratio 1, no phase shift, line charging), which has no tap control in RAW"); return null; }
            if (fields.ContainsKey(hits[0])) { Skip("share a transformer with another control (a RAW winding holds one COD)"); return null; }
            return hits[0];
        }
        static string R(double v) => v.ToString("R", Inv);
        static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        static int? Positions(double min, double max, double step)
        {
            if (!(step > 0) || !(max > min)) return null;
            double n = Math.Round((max - min) / step);
            if (!(n >= 1) || n > 100000) return null;
            int ntp = (int)n + 1;
            return Same((max - min) / (ntp - 1), step) ? ntp : null;
        }

        foreach (Oltc o in model.Oltcs)
        {
            if (o.ImpedanceCorrection is not null) { Skip("carry an impedance correction table (TAB), which this writer does not write"); continue; }
            if (Target(o.Transformer) is not int k) continue;
            double vma = o.TargetVpu + o.DeadbandPu, vmi = o.TargetVpu - o.DeadbandPu;
            if (!(vma > vmi) || !(vmi > 0) || !Same((vma + vmi) / 2, o.TargetVpu) || !Same((vma - vmi) / 2, o.DeadbandPu))
            { Skip("have a target and deadband that VMA and VMI cannot give back to the bit ((VMA + VMI) / 2 and (VMA - VMI) / 2)"); continue; }
            if (!(o.MinTap > 0) || Positions(o.MinTap, o.MaxTap, o.StepPu) is not int ntp)
            { Skip("have a step that (RMA - RMI) / (NTP - 1) cannot give back to the bit for any whole NTP"); continue; }
            // Bug hunt 2026-10-29, finding 46: PSS/E reads a POSITIVE CONT as regulated from the winding 2 side. An OLTC
            // that regulates the transformer's own winding-1 bus (the branch's from bus) is stepped by GridSim as the
            // near side, which PSS/E writes as a NEGATIVE CONT; it used to be written positive, which PSS/E steps the
            // other way (and the reader now refuses). Every other controlled bus is written as before.
            int cont = o.ControlsBus == model.Branches[k].From ? -o.ControlsBus : o.ControlsBus;
            fields[k] = $"1, {cont}, {R(o.MaxTap)}, {R(o.MinTap)}, {R(vma)}, {R(vmi)}, {ntp}";
        }
        int oltcWritten = fields.Count;
        foreach (TransformerFlowControl f in model.TransformerFlowControls)
        {
            if (f.ImpedanceCorrection is not null) { Skip("carry an impedance correction table (TAB), which this writer does not write"); continue; }
            if (Target(f.Transformer) is not int k) continue;
            if (model.Branches[k].From != f.FromBus) { Skip("measure the flow at the to end of their transformer (a RAW winding 1 is the from end)"); continue; }
            if (!(f.FlowMax > f.FlowMin) || Positions(f.Min, f.Max, f.Step) is not int ntp || (f.Kind == TransformerFlowKind.ReactivePower && !(f.Min > 0)))
            { Skip("have limits or a step that RMA, RMI and NTP cannot give back to the bit"); continue; }
            fields[k] = $"{(f.Kind == TransformerFlowKind.ReactivePower ? 2 : 3)}, 0, {R(f.Max)}, {R(f.Min)}, {R(f.FlowMax)}, {R(f.FlowMin)}, {ntp}";
        }
        int flowWritten = fields.Count - oltcWritten;
        if (oltcWritten > 0) notes.Add($"{oltcWritten} on-load tap changer(s) written as voltage control (COD 1); read back with tap control applied.");
        if (flowWritten > 0) notes.Add($"{flowWritten} transformer flow control(s) written (COD 2 or 3); read back with tap control applied.");
        foreach (var kv in why)
            notes.Add($"{kv.Value} tap changer(s) or flow control(s) NOT written (left at the present tap, so a solve that moves them differs): they {kv.Key}.");
        return (fields, notes);
    }

    private static void LccLine(StringBuilder sb, LccConverter c, string who, bool v35)
    {
        sb.Append(c.Bus).Append(", ").Append(c.Bridges).Append(", ").Append(N(c.AngleMaxDeg, who + " angle max")).Append(", ").Append(N(c.AngleMinDeg, who + " angle min"))
          .Append(", ").Append(N(c.RcOhms, who + " RC")).Append(", ").Append(N(c.XcOhms, who + " XC")).Append(", ").Append(N(c.BaseKv, who + " EBAS"))
          .Append(", ").Append(N(c.TransformerRatio, who + " TR")).Append(", ").Append(N(c.Tap, who + " TAP")).Append(", ").Append(N(c.TapMax, who + " TMX"))
          .Append(", ").Append(N(c.TapMin, who + " TMN")).Append(", ").Append(N(c.TapStep, who + " STP")).Append(", 0, ");
        if (v35) sb.Append("0, ");
        sb.Append("0, 0, '1', 0\n");
    }

    private static void VscLine(StringBuilder sb, VscConverter c, string who, bool v35)
    {
        sb.Append(c.Bus).Append(", ").Append(c.DcControl == VscDcControl.DcVoltage ? 1 : 2).Append(", ").Append(c.AcControl == VscAcControl.Voltage ? 1 : 2)
          .Append(", ").Append(N(c.DcSetpoint, who + " DCSET")).Append(", ").Append(N(c.AcSetpoint, who + " ACSET")).Append(", ").Append(N(c.LossAKw, who + " ALOSS"))
          .Append(", ").Append(N(c.LossBKwPerA, who + " BLOSS")).Append(", ").Append(N(c.MinLossKw, who + " MINLOSS")).Append(", ").Append(N(c.SmaxMva, who + " SMAX"))
          .Append(", ").Append(N(c.ImaxA, who + " IMAX")).Append(", ").Append(N(c.PowerWeight, who + " PWF")).Append(", ").Append(N(c.QmaxMvar, who + " MAXQ"))
          .Append(", ").Append(N(c.QminMvar, who + " MINQ")).Append(", 0, ");
        if (v35) sb.Append("0, ");
        sb.Append("100\n");
    }

    private static string Ratings(Branch br, string who, bool twelve)
    {
        string abc = N(br.RateAMva, who + " rate A") + ", " + N(br.RateBMva, who + " rate B") + ", " + N(br.RateCMva, who + " rate C");
        return twelve ? abc + ", 0, 0, 0, 0, 0, 0, 0, 0, 0" : abc;
    }

    private static void Terminator(StringBuilder sb, string ends, string? begins)
    {
        sb.Append("0 / END OF ").Append(ends).Append(" DATA");
        if (begins is not null) sb.Append(", BEGIN ").Append(begins).Append(" DATA");
        sb.Append('\n');
    }

    /// <summary>A number in round-trip form; NaN is refused.</summary>
    private static string N(double v, string what)
    {
        if (double.IsNaN(v)) throw new InvalidDataException($"{what} is NaN.");
        if (!double.IsFinite(v)) throw new InvalidDataException($"{what} is {v}; RAW needs a finite number.");
        return v.ToString("R", Inv);
    }

    /// <summary>A limit: finite, or refused (RAW has no unlimited value).</summary>
    private static string F(double v, string what) => N(v, what);

    /// <summary>A quoted name: in single quotes, or in double quotes when it holds a single quote (the reader takes
    /// either). A name holding both kinds of quote, or a line break, cannot be written.</summary>
    private static string Q(string s, string what)
    {
        if (s.Contains('\n') || s.Contains('\r') || (s.Contains('\'') && s.Contains('"')))
            throw new InvalidDataException($"{what} '{s}' holds both kinds of quote or a line break, which a RAW name cannot.");
        return s.Contains('\'') ? "\"" + s + "\"" : "'" + s + "'";
    }

    private static string OneLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ');
}
