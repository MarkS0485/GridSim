using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse;

/// <summary>How <see cref="PsseSeqReader"/> treats sequence data GridSim's fault networks cannot represent.</summary>
public sealed record PsseSeqOptions
{
    /// <summary>
    /// False (the default): sequence data that would change a fault current and that GridSim's sequence networks have no
    /// element for (a negative- or zero-sequence shunt load, a zero-sequence mutual coupling over part of a line, a
    /// zero-sequence switched shunt block) refuses the file, listing every kind with its count and first
    /// line. True: each is read past and counted in a note, so the rest of the data can be used; the unbalanced fault
    /// currents then differ from PSS/E's.
    /// </summary>
    public bool AllowUnmodelled { get; init; }
}

/// <summary>The RAW case's model with the sequence data applied, the notes, and what was applied.</summary>
/// <param name="Model">The model: generators with their subtransient and negative-sequence reactances, branches with their
/// zero-sequence impedances and transformer winding connections.</param>
/// <param name="Notes">What was approximated, neglected or left as GridSim's default.</param>
public sealed record PsseSeqResult(GridModel Model, IReadOnlyList<string> Notes)
{
    /// <summary>Generators given a subtransient reactance from the positive-sequence generator data.</summary>
    public int PositiveSequenceMachines { get; init; }

    /// <summary>Generators given a negative-sequence reactance.</summary>
    public int NegativeSequenceMachines { get; init; }

    /// <summary>Lines given a zero-sequence impedance.</summary>
    public int ZeroSequenceLines { get; init; }

    /// <summary>Transformers given a zero-sequence impedance and winding connections.</summary>
    public int ZeroSequenceTransformers { get; init; }
}

/// <summary>
/// Reads a PSS/E sequence data (SEQ) file in the revision 30 layout and applies it to the case read from the matching
/// RAW file, giving the IEC 60909 and fault studies their sequence data (Tranche A, October 2026).
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> The change code IC (0; a change case is refused) and, optionally, the revision, which must be 30.
/// When the file gives no revision the RAW case must be revision 30: later revisions add fields to the transformer
/// records (and sections after them) that this layout would read as other fields. Then nine sections in this order,
/// each ended by a 0 record: positive-, negative- and zero-sequence generator impedances (I, ID, R, X on the machine
/// base); negative- and zero-sequence shunt loads (I, G, B); zero-sequence non-transformer branches (I, J, ICKT, RLINZ,
/// XLINZ, BCHZ, GI, BI, GJ, BJ on the system base); zero-sequence mutual impedances; zero-sequence transformers (I, J,
/// K, ICKT, CC, RG, XG, R1, X1, R2, X2, R3, X3 on the system base); zero-sequence switched shunts (I, BZ1 ... BZ8). A
/// file may stop after any section (a Q record, or the end of the text); a record after the last section is refused.
/// <c>@!</c> heading lines and blank lines are skipped, and fields are read as the RAW reader reads them.</para>
/// <para><b>How each record becomes the model.</b></para>
/// <list type="bullet">
/// <item>Positive-sequence generator data: ZXPOS is the machine's subtransient reactance X''d on its MBASE
/// (<see cref="Generator.SubtransientXdPu"/>); it must be above 0. ZRPOS is not used (GridSim's machine fault source is a
/// reactance), with a note when it is not 0.</item>
/// <item>Negative-sequence generator data: ZXNEG is <see cref="Generator.NegativeSeqXPu"/>; above 0. ZRNEG as ZRPOS.</item>
/// <item>Zero-sequence generator data: RZERO + j XZERO (on MBASE, any grounding impedance included) is the machine's
/// zero-sequence impedance to earth at its bus (<see cref="Generator.ZeroSequenceZPu"/>, a grounded neutral), which the
/// zero-sequence network stamps as a shunt. All zero means no data (no path at the machine). A negative RZERO is
/// refused.</item>
/// <item>Zero-sequence lines: RLINZ, XLINZ are the line's <see cref="Branch.R0"/>, <see cref="Branch.X0"/>. Both 0 is
/// given X0 = <see cref="PsseRawParser.ZeroImpedanceXPu"/> (in GridSim a zero Z0 means "not supplied", which would put
/// 3 x Z1 there instead). BCHZ and the line-end shunts are neglected, as IEC 60909-0 neglects line capacitances, with a
/// note.</item>
/// <item>Zero-sequence two-winding transformers: connection code CC 1 is grounded wye on both windings (a series path,
/// Z0 = R1 + jX1), CC 2 grounded wye on winding 1 and delta on winding 2 (a path to earth at winding 1's bus), CC 3 the
/// reverse, CC 4 delta on both (no path). The grounding impedance RG + jXG of CC 2 and 3 is the neutral's own impedance,
/// which appears three times in the zero-sequence path: it is folded into the transformer's zero-sequence impedance,
/// Z0 = (R1 + 3 RG) + j(X1 + 3 XG), which for those codes is the only path through the unit, so the network is the same.
/// CC 1 with a grounding impedance, any other code, and every three-winding record are refused: GridSim's winding
/// model cannot express them.</item>
/// <item>Zero-sequence mutual impedances (I, J, ICKT1, K, L, ICKT2, RM, XM, BIJ1, BIJ2, BKL1, BKL2): a coupling over
/// the whole of both lines (BIJ1 = BKL1 = 0, BIJ2 = BKL2 = 1) is a <see cref="ZeroSequenceMutual"/> between the two model
/// lines, RM + j XM signed for the model branches' directions (negated when exactly one record line runs opposite to its
/// branch). A coupling over part of a line is unmodelled (refused unless allowed): GridSim's lines have no sections.</item>
/// <item>Matching: a generator record is the RAW machine with that bus and ID; a branch or transformer record the RAW
/// record with those buses (in either order) and circuit. A record for an element the RAW file holds but the model does
/// not (out of service, or on a dropped isolated bus) is skipped and counted; one the RAW file does not hold, or a
/// second record for the same element in one section, is refused.</item>
/// <item>Elements with no record keep GridSim's defaults, each counted in a note: a machine its synthesised X''d, a line
/// 3 x Z1, and a transformer, having no winding data, is treated as a line in the zero sequence (3 x Z1 too).</item>
/// </list>
/// </remarks>
public static class PsseSeqReader
{
    /// <summary>The one SEQ layout this reader reads.</summary>
    public const int SupportedRevision = 30;

    private const string PosGen = "POSITIVE SEQUENCE GENERATOR", NegGen = "NEGATIVE SEQUENCE GENERATOR",
        ZeroGen = "ZERO SEQUENCE GENERATOR", NegLoad = "NEGATIVE SEQUENCE SHUNT LOAD", ZeroLoad = "ZERO SEQUENCE SHUNT LOAD",
        ZeroLine = "ZERO SEQUENCE NON-TRANSFORMER BRANCH", ZeroMutual = "ZERO SEQUENCE MUTUAL IMPEDANCE",
        ZeroXfmr = "ZERO SEQUENCE TRANSFORMER", ZeroSwShunt = "ZERO SEQUENCE SWITCHED SHUNT";

    private const string SystemWide = "SYSTEM-WIDE", GeneratorsAll = "GENERATOR", LoadsAll = "LOAD", LinesAll = "ZERO SEQ. NON-TRANSFORMER BRANCH",
        MutualAll = "ZERO SEQ. MUTUAL", XfmrAll = "ZERO SEQ. TRANSFORMER", SwShuntAll = "ZERO SEQ. SWITCHED SHUNT",
        FixedAll = "ZERO SEQ. FIXED SHUNT", InductionAll = "INDUCTION MACHINE", NcsTable = "NON-CONVENTIONAL SOURCE TABLE",
        NcsMachine = "NON-CONVENTIONAL SOURCE MACHINE";

    private static readonly string[] Sections33 = { GeneratorsAll, LoadsAll, LinesAll, MutualAll, XfmrAll, SwShuntAll, FixedAll, InductionAll };
    private static readonly string[] Sections35 = { SystemWide, GeneratorsAll, LoadsAll, LinesAll, MutualAll, XfmrAll, SwShuntAll, FixedAll, InductionAll, NcsTable, NcsMachine };

    private static readonly string[] Sections = { PosGen, NegGen, ZeroGen, NegLoad, ZeroLoad, ZeroLine, ZeroMutual, ZeroXfmr, ZeroSwShunt };

    /// <summary>Read a SEQ file from disk and apply it. See <see cref="Apply"/>.</summary>
    public static PsseSeqResult ReadFile(string path, PsseParseResult raw, PsseSeqOptions? options = null)
        => Apply(GridSim.Core.IO.InputFileLimits.ReadAllText(path), raw, options);

    /// <summary>Apply the SEQ text to <paramref name="raw"/> (the case read from the matching RAW file).</summary>
    public static PsseSeqResult Apply(string text, PsseParseResult raw, PsseSeqOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Empty PSS/E SEQ text.");
        options ??= new PsseSeqOptions();
        string[] lines = PsseText.Lines(text);
        var notes = new List<string>();

        // ---- Change code and revision ------------------------------------------------------------------------------
        var cur = new PsseLineCursor(lines, 0, "SEQ");
        PsseRecord id = cur.Next("CHANGE CODE") ?? throw new InvalidDataException("PSS/E SEQ text has no change code (IC) line.");
        int ic = id.Int(0, "IC", 0);
        if (ic != 0)
            throw id.Error($"IC is {ic}: this is a change case (data to add to sequence data already in memory), not a whole set; only IC 0 can be read");
        int rev = id.Has(1) ? id.Int(1, "REV") : 0;
        bool newLayout = rev is 33 or 34 or 35 or 36;
        if (rev != 0 && rev != SupportedRevision && !newLayout)
            throw id.Error($"the file says revision {rev}; this reader reads the sequence layouts of revisions 30, 33, 34, 35 and 36 only (other revisions would be guessed at)");
        if (newLayout && raw.Revision == SupportedRevision)
            throw id.Error($"the file says revision {rev} but the RAW case is revision {SupportedRevision}; a sequence file takes the layout of its RAW case's revision, and a revision {SupportedRevision} case reads the revision {SupportedRevision} layout");
        if (rev == 0 && raw.Revision != SupportedRevision)
            throw id.Error($"the file does not say its revision and the RAW case is revision {raw.Revision}; this reader reads the revision {SupportedRevision} sequence layout only, which a revision {raw.Revision} SEQ file does not follow");

        // ---- Sections ----------------------------------------------------------------------------------------------
        string[] sectionList = !newLayout ? Sections : rev >= 35 ? Sections35 : Sections33;
        var records = new Dictionary<string, List<PsseRecord>>(StringComparer.Ordinal);
        foreach (string s in Sections) records[s] = new List<PsseRecord>();
        var fileRecords = new Dictionary<string, List<PsseRecord>>(StringComparer.Ordinal);
        foreach (string s in sectionList) fileRecords[s] = new List<PsseRecord>();
        if (!newLayout) fileRecords = records;
        bool ended = false;
        int read = 0;
        for (int si = 0; si < sectionList.Length && !ended; si++)
        {
            while (true)
            {
                PsseRecord? r = cur.Next(sectionList[si]);
                if (r is null && fileRecords[sectionList[si]].Count > 0)
                    throw fileRecords[sectionList[si]][^1].Error($"the file ends inside the {sectionList[si]} data with no terminating 0 record; the file is truncated");
                if (r is null || r.IsEndOfData) { ended = true; break; }
                if (r.IsTerminator) { read = si + 1; break; }
                // Revision 35 and 36: the system-wide records are keyword lines. A file that starts its generator data straight
                // away (no system-wide section and no terminator for it) is read as such.
                if (newLayout && sectionList[si] == SystemWide && !char.IsLetter(r.Raw(0)?[0] ?? ' '))
                {
                    cur = new PsseLineCursor(lines, r.Line - 1, "SEQ");
                    read = si + 1;
                    break;
                }
                fileRecords[sectionList[si]].Add(r);
            }
        }
        if (!ended)
        {
            PsseRecord? after = cur.Next("END OF DATA");
            if (after is not null && !after.IsEndOfData)
                throw after.Error(newLayout
                    ? "a record follows the last section; the file should end there or with Q"
                    : "a record follows the last section (zero-sequence switched shunts); the file should end there or with Q");
        }
        else if (read < sectionList.Length)
            notes.Add($"the file stops after {read} of the {sectionList.Length} sections; the rest are taken as empty.");

        // ---- Lookups into the RAW case -----------------------------------------------------------------------------
        GridModel model = raw.Model;
        var machineIndex = new Dictionary<(int, string), int>();
        for (int k = 0; k < raw.MachineKeys.Count; k++) machineIndex[(raw.MachineKeys[k].Bus, raw.MachineKeys[k].Id)] = k;
        var modelBuses = new HashSet<int>(model.Buses.Select(b => b.Id));
        bool DroppedBus(int bus) => raw.BusMeta.ContainsKey(bus) && !modelBuses.Contains(bus);

        var branchIndex = new Dictionary<(string Kind, int I, int J, string Ckt), int>();
        for (int k = 0; k < raw.BranchKeys.Count; k++)
        {
            PsseBranchKey key = raw.BranchKeys[k];
            if (key.Kind is "branch" or "transformer") branchIndex[(key.Kind, key.FromBus, key.ToBus, key.Circuit)] = k;
        }
        var skipped = new HashSet<(string, int, int, string)>(raw.SkippedBranchKeys.Select(k => (k.Kind, k.FromBus, k.ToBus, k.Circuit)));

        // The branch a record names (either orientation): its model index and whether the record runs the other way, or
        // null for a record the RAW file holds outside the model.
        (int Index, bool Reversed)? FindBranch(PsseRecord r, string kind, int i, int j, string ckt)
        {
            if (branchIndex.TryGetValue((kind, i, j, ckt), out int k)) return (k, false);
            if (branchIndex.TryGetValue((kind, j, i, ckt), out k)) return (k, true);
            if (skipped.Contains((kind, i, j, ckt)) || skipped.Contains((kind, j, i, ckt)) || DroppedBus(i) || DroppedBus(j)) return null;
            throw r.Error($"the RAW case has no {(kind == "branch" ? "non-transformer branch" : "two-winding transformer")} {i}-{j} circuit '{ckt}'");
        }

        var gens = model.AllGenerators.ToArray();
        var branches = model.Branches.ToArray();
        var unmodelled = new Dictionary<string, (int Count, int FirstLine)>(StringComparer.Ordinal);
        void Unmodelled(string kind, PsseRecord r)
        {
            unmodelled.TryGetValue(kind, out var c);
            unmodelled[kind] = (c.Count + 1, c.FirstLine == 0 ? r.Line : c.FirstLine);
        }
        int skippedRecords = 0;
        if (newLayout) ConvertNewLayout(fileRecords, records, Unmodelled, notes);

        // ---- Generators: X''d and X2 on MBASE -------------------------------------------------------------------------
        int MachineOf(PsseRecord r, HashSet<int> seen, string what)
        {
            int bus = r.Int(0, "I");
            string mid = r.Str(1, "1");
            if (!machineIndex.TryGetValue((bus, mid), out int k))
            {
                if (DroppedBus(bus)) return -1;
                throw r.Error($"the RAW case has no machine '{mid}' at bus {bus}");
            }
            if (!seen.Add(k)) throw r.Error($"machine '{mid}' at bus {bus} has a second {what} record");
            return k;
        }
        int pos = 0, posR = 0, neg = 0, negR = 0;
        var seenPos = new HashSet<int>();
        foreach (PsseRecord r in records[PosGen])
        {
            int k = MachineOf(r, seenPos, "positive-sequence");
            double zr = r.Real(2, "ZRPOS", 0), zx = r.Real(3, "ZXPOS");
            if (!(zx > 0)) throw r.Error($"ZXPOS is {zx}; a machine's subtransient reactance must be above 0");
            if (k < 0) { skippedRecords++; continue; }
            if (zr != 0) posR++;
            gens[k] = gens[k] with { SubtransientXdPu = zx };
            pos++;
        }
        var seenNeg = new HashSet<int>();
        foreach (PsseRecord r in records[NegGen])
        {
            int k = MachineOf(r, seenNeg, "negative-sequence");
            double zr = r.Real(2, "ZRNEG", 0), zx = r.Real(3, "ZXNEG");
            if (!(zx > 0)) throw r.Error($"ZXNEG is {zx}; a machine's negative-sequence reactance must be above 0");
            if (k < 0) { skippedRecords++; continue; }
            if (zr != 0) negR++;
            gens[k] = gens[k] with { NegativeSeqXPu = zx };
            neg++;
        }
        var seenZero = new HashSet<int>();
        int zeroGenOff = 0, zeroGenNone = 0, zeroGen = 0;
        foreach (PsseRecord r in records[ZeroGen])
        {
            int k = MachineOf(r, seenZero, "zero-sequence");
            double zr = r.Real(2, "RZERO", 0), zx = r.Real(3, "XZERO", 0);
            if (zr < 0) throw r.Error($"RZERO is {zr}; a zero-sequence resistance must not be negative");
            if (k < 0) { skippedRecords++; continue; }
            if (zr == 0 && zx == 0) { zeroGenNone++; continue; }
            if (!gens[k].InService) { zeroGenOff++; continue; }
            gens[k] = gens[k] with { ZeroSequenceZPu = new MachineZeroSequence(zr, zx) };
            zeroGen++;
        }
        if (zeroGen > 0) notes.Add($"{zeroGen} machine(s) have a zero-sequence impedance (RZERO, XZERO): each is a grounded neutral, a zero-sequence shunt to earth at the machine's bus.");
        if (posR > 0 || negR > 0) notes.Add($"{posR} positive- and {negR} negative-sequence machine resistance(s) are not 0; GridSim's machine fault source is a reactance, so they are not used.");
        if (zeroGenNone > 0) notes.Add($"{zeroGenNone} machine zero-sequence record(s) are all zero: no zero-sequence path at the machine, as GridSim models it.");
        if (zeroGenOff > 0) notes.Add($"{zeroGenOff} zero-sequence record(s) of out-of-service machines read past.");

        // ---- Shunt loads: GridSim's sequence networks neglect loads (IEC 60909) -------------------------------------
        foreach ((string sec, string g, string b, string kind) in new[]
            { (NegLoad, "GNEG", "BNEG", "negative-sequence shunt load"), (ZeroLoad, "GZERO", "BZERO", "zero-sequence shunt load") })
            foreach (PsseRecord r in records[sec])
            {
                int bus = r.Int(0, "I");
                if (!raw.BusMeta.ContainsKey(bus)) throw r.Error($"bus {bus} is not a bus of the RAW case");
                if (r.Real(1, g, 0) != 0 || r.Real(2, b, 0) != 0) Unmodelled(kind, r);
            }

        // ---- Zero-sequence lines ----------------------------------------------------------------------------------
        int lines0 = 0, lineZeroZ = 0, lineCharging = 0;
        var seenLine = new HashSet<int>();
        foreach (PsseRecord r in records[ZeroLine])
        {
            int i = r.Int(0, "I"), j = r.AbsInt(1, "J");
            string ckt = r.Str(2, "1");
            double r0 = r.Real(3, "RLINZ", 0), x0 = r.Real(4, "XLINZ", 0);
            if (r0 < 0) throw r.Error($"RLINZ is {r0}; a zero-sequence resistance must not be negative");
            (int Index, bool Reversed)? at = FindBranch(r, "branch", i, j, ckt);
            if (at is null) { skippedRecords++; continue; }
            if (!seenLine.Add(at.Value.Index)) throw r.Error($"branch {i}-{j} circuit '{ckt}' has a second zero-sequence record");
            if (r0 == 0 && x0 == 0) { x0 = PsseRawParser.ZeroImpedanceXPu; lineZeroZ++; }
            if (r.Real(5, "BCHZ", 0) != 0 || r.Real(6, "GI", 0) != 0 || r.Real(7, "BI", 0) != 0 || r.Real(8, "GJ", 0) != 0 || r.Real(9, "BJ", 0) != 0)
                lineCharging++;
            branches[at.Value.Index] = branches[at.Value.Index] with { R0 = r0, X0 = x0 };
            lines0++;
        }
        if (lineZeroZ > 0) notes.Add($"{lineZeroZ} line(s) have a zero-sequence impedance of 0; given X0 = {PsseRawParser.ZeroImpedanceXPu.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} pu (PSS/E's zero-impedance threshold).");
        if (lineCharging > 0) notes.Add($"{lineCharging} line(s) carry zero-sequence charging or line-end shunts (BCHZ, GI, BI, GJ, BJ); they are neglected, as IEC 60909-0 neglects line capacitances.");

        var mutuals = new List<ZeroSequenceMutual>();
        foreach (PsseRecord r in records[ZeroMutual])
        {
            int i = r.Int(0, "I"), j = r.AbsInt(1, "J"), k = r.Int(3, "K"), l = r.AbsInt(4, "L");
            string c1 = r.Str(2, "1"), c2 = r.Str(5, "1");
            double rm = r.Real(6, "RM", 0), xm = r.Real(7, "XM", 0);
            double bij1 = r.Real(8, "BIJ1", 0), bij2 = r.Real(9, "BIJ2", 1), bkl1 = r.Real(10, "BKL1", 0), bkl2 = r.Real(11, "BKL2", 1);
            if (bij1 != 0 || bij2 != 1 || bkl1 != 0 || bkl2 != 1) { Unmodelled("zero-sequence mutual coupling over part of a line", r); continue; }
            (int Index, bool Reversed)? a = FindBranch(r, "branch", i, j, c1);
            (int Index, bool Reversed)? b = FindBranch(r, "branch", k, l, c2);
            if (a is null || b is null) { skippedRecords++; continue; }
            if (a.Value.Index == b.Value.Index) throw r.Error($"line {i}-{j} circuit '{c1}' is coupled to itself");
            double sign = a.Value.Reversed == b.Value.Reversed ? 1.0 : -1.0;
            Branch ba = branches[a.Value.Index], bb = branches[b.Value.Index];
            mutuals.Add(new ZeroSequenceMutual(a.Value.Index, ba.From, ba.To, b.Value.Index, bb.From, bb.To, sign * rm, sign * xm));
        }
        if (mutuals.Count > 0) notes.Add($"{mutuals.Count} zero-sequence mutual coupling(s) between whole lines are applied to the zero-sequence network.");

        // ---- Zero-sequence transformers -----------------------------------------------------------------------------
        int xf0 = 0, xfFolded = 0;
        var seenXf = new HashSet<int>();
        foreach (PsseRecord r in records[ZeroXfmr])
        {
            int i = r.Int(0, "I"), j = r.AbsInt(1, "J"), k = r.AbsInt(2, "K", 0);
            string ckt = r.Str(3, "1");
            if (k != 0)
                throw r.Error("a three-winding transformer's zero-sequence record; GridSim's winding model covers two-winding units only, so the sequence data cannot be applied");
            int cc = r.Code(4, "CC", 1, 1, 2, 3, 4, 5, 6, 7, 8, 9);
            if (cc > 4) throw r.Error($"connection code CC {cc} is not modelled; codes 1 to 4 (wye-wye, wye-delta, delta-wye, delta-delta) are");
            double rg = r.Real(5, "RG", 0), xg = r.Real(6, "XG", 0), r1 = r.Real(7, "R1", 0), x1 = r.Real(8, "X1", 0);
            if (rg < 0 || r1 < 0) throw r.Error("RG and R1 must not be negative");
            if (cc == 1 && (rg != 0 || xg != 0))
                throw r.Error("CC 1 (grounded wye on both windings) with a grounding impedance RG, XG; GridSim's winding model has no place for it");
            (int Index, bool Reversed)? at = FindBranch(r, "transformer", i, j, ckt);
            if (at is null) { skippedRecords++; continue; }
            if (!seenXf.Add(at.Value.Index)) throw r.Error($"transformer {i}-{j} circuit '{ckt}' has a second zero-sequence record");
            double r0 = r1, x0 = x1;
            if ((cc == 2 || cc == 3) && (rg != 0 || xg != 0)) { r0 = r1 + 3.0 * rg; x0 = x1 + 3.0 * xg; xfFolded++; }
            if (cc != 4 && r0 == 0 && x0 == 0) throw r.Error("the zero-sequence path impedance is 0 (R1, X1 and the grounding impedance are all 0)");
            // Winding 1 is the record's bus I; the model branch's from end is RAW bus I, which the record may name second.
            WindingConnection w1 = cc is 1 or 2 ? WindingConnection.WyeGrounded : WindingConnection.Delta;
            WindingConnection w2 = cc is 1 or 3 ? WindingConnection.WyeGrounded : WindingConnection.Delta;
            (WindingConnection from, WindingConnection to) = at.Value.Reversed ? (w2, w1) : (w1, w2);
            branches[at.Value.Index] = branches[at.Value.Index] with { R0 = r0, X0 = x0, FromWinding = from, ToWinding = to };
            xf0++;
        }
        if (xfFolded > 0) notes.Add($"{xfFolded} transformer(s) have a neutral grounding impedance (RG, XG); it is folded into the zero-sequence impedance as 3 (RG + jXG), the path to earth through the grounded winding.");

        foreach (PsseRecord r in records[ZeroSwShunt])
        {
            int bus = r.Int(0, "I");
            if (!raw.BusMeta.ContainsKey(bus)) throw r.Error($"bus {bus} is not a bus of the RAW case");
            for (int b = 1; b < r.Count; b++)
                if (r.Real(b, $"BZ{b}", 0) != 0) { Unmodelled("zero-sequence switched shunt block", r); break; }
        }

        // ---- Refuse or note what GridSim cannot represent ----------------------------------------------------------
        if (unmodelled.Count > 0 && !options.AllowUnmodelled)
            throw new InvalidDataException("PSS/E SEQ data for case '" + model.Name + "' holds sequence data GridSim's fault networks do not model, so its unbalanced fault currents would differ from the file's: " +
                string.Join("; ", unmodelled.Select(kv => $"{kv.Value.Count} {kv.Key}(s) (first at line {kv.Value.FirstLine})")) +
                ". Read it with unmodelled data allowed (gridsim: --allow-unmodelled) to use the rest.");
        foreach (var kv in unmodelled)
            notes.Add($"{kv.Value.Count} {kv.Key}(s) read past and NOT modelled (unmodelled data allowed); unbalanced fault currents differ from the file's.");
        if (skippedRecords > 0) notes.Add($"skipped {skippedRecords} record(s) for elements the RAW file holds outside the model (out of service, or on a dropped isolated bus).");

        // ---- What keeps GridSim's defaults ----------------------------------------------------------------------------
        int genNoPos = 0;
        for (int k = 0; k < gens.Length; k++) if (gens[k].InService && !seenPos.Contains(k)) genNoPos++;
        if (genNoPos > 0) notes.Add($"{genNoPos} in-service machine(s) have no positive-sequence record; they keep GridSim's synthesised subtransient reactance.");
        int lineNo = 0, xfNo = 0;
        for (int k = 0; k < branches.Length; k++)
        {
            string kind = raw.BranchKeys[k].Kind;
            if (kind == "branch" && !seenLine.Contains(k)) lineNo++;
            else if (kind is "transformer" or "transformer3" && !seenXf.Contains(k)) xfNo++;
        }
        if (lineNo > 0) notes.Add($"{lineNo} line(s) have no zero-sequence record; GridSim uses 3 x Z1 for them.");
        if (xfNo > 0) notes.Add($"{xfNo} transformer branch(es) have no zero-sequence record; with no winding data GridSim treats them as it treats a line in the zero sequence, a series path at 3 x Z1.");

        GridModel applied = model.CopyWith(generators: gens, branches: branches, zeroSequenceMutuals: mutuals.Count > 0 ? mutuals : null);
        return new PsseSeqResult(applied, notes)
        {
            PositiveSequenceMachines = pos,
            NegativeSequenceMachines = neg,
            ZeroSequenceLines = lines0,
            ZeroSequenceTransformers = xf0,
        };
    }

    /// <summary>
    /// The revision 33 to 36 sequence records (one generator section, one load section, a transformer record with its
    /// grounding data on each winding) as the revision 30 section records the rest of the reader applies, with what
    /// GridSim has no element for counted through <paramref name="unmodelled"/>. Codes the reader cannot place are refused,
    /// never guessed: a transformer zero-sequence data code CZ0 or grounding code CZG other than 1, and a connection code
    /// other than 1 to 4 (revisions 33 and 34) or 11 to 14 (revisions 35 and 36; the same four earth paths).
    /// </summary>
    private static void ConvertNewLayout(Dictionary<string, List<PsseRecord>> file, Dictionary<string, List<PsseRecord>> records,
        Action<string, PsseRecord> unmodelled, List<string> notes)
    {
        static PsseRecord Make(PsseRecord src, string section, params string?[] fields) => new(fields, src.Line, section, src.Comment, "SEQ");
        static string Num(string? v) => string.IsNullOrEmpty(v) ? "" : v;
        static bool NonZero(PsseRecord r, int index, string name) => r.Real(index, name, 0) != 0;
        static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        foreach (PsseRecord r in file[GeneratorsAll])
        {
            string? id1 = r.Raw(0), id2 = r.Raw(1);
            if (NonZero(r, 3, "ZXPPDV") || NonZero(r, 2, "ZRPOS")) records[PosGen].Add(Make(r, PosGen, id1, id2 ?? "1", Num(r.Raw(2)), Num(r.Raw(3))));
            if (NonZero(r, 7, "ZXNEGDV") || NonZero(r, 6, "ZRNEG")) records[NegGen].Add(Make(r, NegGen, id1, id2 ?? "1", Num(r.Raw(6)), Num(r.Raw(7))));
            if (NonZero(r, 8, "ZR0") || NonZero(r, 9, "ZX0DV")) records[ZeroGen].Add(Make(r, ZeroGen, id1, id2 ?? "1", Num(r.Raw(8)), Num(r.Raw(9))));
            if (NonZero(r, 11, "ZRG") || NonZero(r, 12, "ZXG")) unmodelled("generator grounding impedance (ZRG, ZXG)", r);
        }
        foreach (PsseRecord r in file[LoadsAll])
        {
            // The revision 33 and later load record (PNEG, QNEG in MW and Mvar, then a grounding flag before the zero-sequence
            // fields) is not confirmed against the published layout, and mapping it onto the revision 30 GNEG, BNEG, GZERO and
            // BZERO fields would shift the zero-sequence data and mis-state the units. It is refused, not guessed at.
            throw r.Error("the LOAD section of a revision 33 or later sequence file is not read: its field layout and units are not confirmed. "
                + "Remove the section's records (an empty LOAD section is accepted) or convert the load data to a revision 30 file");
        }
        records[ZeroLine].AddRange(file[LinesAll]);
        records[ZeroMutual].AddRange(file[MutualAll]);
        foreach (PsseRecord r in file[XfmrAll])
        {
            int k = r.Int(2, "K", 0);
            int cz0 = r.Int(4, "CZ0", 1), czg = r.Int(5, "CZG", 1);
            if (k == 0 && cz0 != 1)
                throw r.Error($"CZ0 is {cz0}; only 1 (zero-sequence impedance in pu on the system base) is read, other data codes are not guessed at");
            if (k == 0 && czg != 1)
                throw r.Error($"CZG is {czg}; only 1 (grounding impedance in pu on the system base) is read, other data codes are not guessed at");
            int cc = r.Int(6, "CC", 1);
            int cc30 = cc is >= 11 and <= 14 ? cc - 10 : cc;
            if (k == 0 && (cc30 < 1 || cc30 > 4))
                throw r.Error($"connection code CC {cc} is not modelled; codes 1 to 4 (revisions 33, 34) or 11 to 14 (revisions 35, 36) are");
            double rg1 = r.Real(7, "RG1", 0), xg1 = r.Real(8, "XG1", 0), rg2 = r.Real(11, "RG2", 0), xg2 = r.Real(12, "XG2", 0);
            // The 30 layout has one grounding impedance: the grounded winding's (CC 2: winding 1, CC 3: winding 2); CC 1 carries
            // both so that any of them refuses, as the 30 layout does.
            double rg = cc30 == 3 ? rg2 : cc30 == 2 ? rg1 : rg1 + rg2, xg = cc30 == 3 ? xg2 : cc30 == 2 ? xg1 : xg1 + xg2;
            records[ZeroXfmr].Add(Make(r, ZeroXfmr, r.Raw(0), r.Raw(1), r.Raw(2) ?? "0", r.Raw(3) ?? "1",
                cc30.ToString(System.Globalization.CultureInfo.InvariantCulture), R(rg), R(xg), Num(r.Raw(9)), Num(r.Raw(10)), Num(r.Raw(13)), Num(r.Raw(14))));
            if (k == 0 && (NonZero(r, 15, "RNUTRL") || NonZero(r, 16, "XNUTRL"))) unmodelled("transformer neutral impedance (RNUTRL, XNUTRL)", r);
        }
        foreach (PsseRecord r in file[SwShuntAll])
            for (int b = 2; b < r.Count; b++)
                if (r.Real(b, "BZ", 0) != 0) { unmodelled("zero-sequence switched shunt block", r); break; }
        foreach (PsseRecord r in file[FixedAll])
            if (NonZero(r, 2, "GZERO") || NonZero(r, 3, "BZERO")) unmodelled("zero-sequence fixed shunt", r);
        foreach (PsseRecord r in file[InductionAll]) unmodelled("induction machine sequence record", r);
        if (file.TryGetValue(NcsTable, out List<PsseRecord>? t1)) foreach (PsseRecord r in t1) unmodelled("non-conventional source fault contribution record", r);
        if (file.TryGetValue(NcsMachine, out List<PsseRecord>? t2)) foreach (PsseRecord r in t2) unmodelled("non-conventional source machine record", r);
        notes.Add("sequence data read in the revision 33 to 36 layout: machine X''d, X2 and Z0 from ZXPPDV, ZXNEGDV and ZR0, ZX0DV; the transformer zero-sequence impedance from R01, X01 and the winding grounding impedance RG1 or RG2.");
    }
}
