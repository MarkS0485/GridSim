using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Matpower;

/// <summary>
/// Parses a MATPOWER <c>.m</c> case file into a <see cref="GridModel"/> with exact per-unit
/// values (no dispatch reinterpretation  -  <c>mpc.gen</c> Pg/Qg/Vg and the stored bus voltages
/// are preserved verbatim so the model reproduces the source operating point). This is the single
/// import path for the PEGASE, RTS, NESTA and the wider MATPOWER case libraries, all of which ship
/// natively in this format.
/// </summary>
/// <remarks>
/// Handles the standard <c>mpc.baseMVA</c> scalar and the <c>mpc.bus</c>, <c>mpc.gen</c> and
/// <c>mpc.branch</c> matrices. Comments (<c>%</c> to end-of-line) are stripped and <c>...</c> line
/// continuations joined before parsing. Numbers are read with the invariant culture. Bus ids need
/// not be contiguous or sorted  -  the solvers key on the bus id, and result row <c>i</c> maps to
/// <c>model.Buses[i]</c>, so file order is preserved. Isolated (type 4) buses and out-of-service
/// (status 0) branches are dropped, along with anything incident to a dropped bus, so no
/// dangling reference can reach the solver. Out-of-service (status 0) generators are KEPT, marked
/// <see cref="Generator.InService"/> false (F16): <see cref="GridModel.Generators"/> leaves them out, so no engine
/// solves them, and <see cref="GridModel.AllGenerators"/> keeps them for the writers. The bus VMAX / VMIN columns
/// are read into <see cref="Bus.VmaxPu"/> / <see cref="Bus.VminPu"/> (F17), <c>mpc.gencost</c> onto each generator
/// and <c>mpc.dcline</c> into <see cref="MatpowerParseResult.DcLines"/> (F24); none of these is used by a solver
/// unless asked for (see each member).
/// </remarks>
public static class MatpowerParser
{
    /// <summary>The nominal frequency (Hz) assumed when the caller does not give one. A MATPOWER case file
    /// carries no system frequency, so this is an assumption and is recorded in the parse notes (R269).</summary>
    public const double AssumedFrequencyHz = 50.0;

    /// <summary>Parse case text into a model. <paramref name="name"/> overrides the case name (else the
    /// <c>function mpc = &lt;name&gt;</c> declaration, else "matpower"). <paramref name="frequencyHz"/> is the
    /// system's nominal frequency; MATPOWER files do not carry one (the IEEE, WSCC and most MATPOWER cases are
    /// 60 Hz systems), so pass it when known - omitted, <see cref="AssumedFrequencyHz"/> is used and the
    /// assumption is noted (see <see cref="ParseWithDiagnostics"/>).</summary>
    /// <remarks>N165: <paramref name="units"/> defaults to <see cref="MatpowerUnitHandling.NormaliseRawFeeders"/>, so a
    /// raw ohm / kW distribution feeder comes back in per-unit / MW (see <see cref="ParseWithDiagnostics"/>).</remarks>
    public static GridModel Parse(string text, string? name = null, double? frequencyHz = null,
        MatpowerUnitHandling units = MatpowerUnitHandling.NormaliseRawFeeders)
        => ParseWithDiagnostics(text, name, frequencyHz, units).Model;

    /// <summary>Read and parse a <c>.m</c> file from disk. The case name defaults to the file stem. A raw ohm / kW
    /// distribution feeder is converted to per-unit / MW unless <paramref name="units"/> is
    /// <see cref="MatpowerUnitHandling.AsWritten"/> (N165; see <see cref="ParseWithDiagnostics"/>).
    /// <para>N187 (D2 nominal frequency, October 2026): <paramref name="frequencyHz"/> is the system's nominal frequency,
    /// exactly as for <see cref="Parse"/>; null (the default, every existing caller) keeps the documented
    /// <see cref="AssumedFrequencyHz"/> assumption. The CLI's <c>--frequency-hz</c> flag reaches it.</para></summary>
    public static GridModel ParseFile(string path, MatpowerUnitHandling units = MatpowerUnitHandling.NormaliseRawFeeders,
        double? frequencyHz = null)
        => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), frequencyHz, units);

    /// <summary>Parse case text, returning the model together with any diagnostic notes.</summary>
    /// <remarks>
    /// <para><b>Units (N165, October 2026).</b> A few published radial feeders (case22, case33bw, case69 and case85 in
    /// data/matpower/dist) give branch impedance in ohms and demand in kW / kVAr instead of per-unit and MW. Before
    /// N165 this parser returned those numbers as written and every product path (CLI, web, desktop) wrapped the call
    /// in <see cref="MatpowerUnitNormalizer.Normalize"/>; a Core caller that forgot the wrapper got a network about
    /// 1000 times too heavily loaded, silently. The safe path is now the default: with
    /// <see cref="MatpowerUnitHandling.NormaliseRawFeeders"/> the parsed model goes through the SAME
    /// <see cref="MatpowerUnitNormalizer.Normalize"/> the product paths use, and a note says so. A case that does not
    /// look raw (every other bundled file) is returned exactly as before: the very same result object.</para>
    /// <para>Product results are bit-identical: Normalize is a no-op on its own output (checked here, fail loud), so the
    /// existing <c>Normalize(ParseFile(path))</c> call sites compute exactly the model they computed before.</para>
    /// <para><see cref="MatpowerUnitHandling.AsWritten"/> keeps the numbers exactly as the file writes them (the
    /// pre-N165 result) for a caller that wants to inspect or test the raw data; a note then says the case looks raw
    /// and was NOT converted.</para>
    /// </remarks>
    public static MatpowerParseResult ParseWithDiagnostics(string text, string? name = null, double? frequencyHz = null,
        MatpowerUnitHandling units = MatpowerUnitHandling.NormaliseRawFeeders)
    {
        // An undefined enum value is a programming error: refuse it rather than guess which handling was meant.
        if (units != MatpowerUnitHandling.NormaliseRawFeeders && units != MatpowerUnitHandling.AsWritten)
            throw new ArgumentOutOfRangeException(nameof(units), units, "Unknown MatpowerUnitHandling value.");
        return ApplyUnitHandling(ParseAsWritten(text, name, frequencyHz), units);
    }

    /// <summary>
    /// N165: apply the unit handling to an as-written parse. A case that does not look raw: the result is returned
    /// unchanged (the same object). Raw with AsWritten: a note only. Raw with NormaliseRawFeeders: the model is
    /// replaced by <see cref="MatpowerUnitNormalizer.Normalize"/>'s copy and a note names the conversion.
    /// </summary>
    private static MatpowerParseResult ApplyUnitHandling(MatpowerParseResult parsed, MatpowerUnitHandling units)
    {
        GridModel model = parsed.Model;
        if (!MatpowerUnitNormalizer.LooksRawUnits(model))
            return parsed;   // already the per-unit / MW convention: every number exactly as before

        var notes = new List<string>(parsed.Notes);
        if (units == MatpowerUnitHandling.AsWritten)
        {
            notes.Add("raw engineering units suspected (branch impedance in ohms, demand in kW / kVAr) and NOT converted, " +
                      "because the caller asked for the numbers as written; MatpowerUnitNormalizer.Normalize converts them.");
            return parsed with { Notes = notes };
        }

        GridModel normalised = MatpowerUnitNormalizer.Normalize(model);
        // Fail loud if the conversion would not be idempotent: a model that STILL looks raw after one conversion would
        // be converted a second time by a caller that also wraps Normalize (the CLI, web and desktop do), giving two
        // different networks for one file. No bundled feeder comes near this (case85, the largest load-to-base ratio,
        // reads about 2514 x base raw and 2.5 x base converted, against the 30 x trigger), so this refusal guards only
        // a pathological file, which is better refused than solved scaled twice.
        if (MatpowerUnitNormalizer.LooksRawUnits(normalised))
            throw new InvalidDataException($"MATPOWER case '{model.Name}' still looks like raw ohm / kW data after the per-unit conversion, so its units are ambiguous. Convert the file to per-unit / MW, or parse it with MatpowerUnitHandling.AsWritten and convert it yourself.");
        notes.Add("raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published " +
                  "distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of " +
                  "the from bus, Pd, Qd, Gs and Bs divided by 1000.");
        return parsed with { Model = normalised, Notes = notes, RawUnitsConverted = true };
    }

    /// <summary>The parse itself, every number exactly as the file writes it (the whole parser before N165).</summary>
    private static MatpowerParseResult ParseAsWritten(string text, string? name, double? frequencyHz)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Empty MATPOWER case text.");
        if (frequencyHz is double fGiven && !(fGiven > 0 && double.IsFinite(fGiven)))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz,
                "The nominal frequency must be a positive, finite number of hertz.");

        string clean = Clean(text);
        var notes = new List<string>();

        // The file has no frequency field (R269, October 2026): the old silent 50 Hz default skewed small-signal
        // and EMT results for the 60 Hz IEEE/WSCC/MATPOWER systems. Use the caller's value, else the documented
        // assumption, and say so in the notes so the assumption is visible wherever the notes are shown.
        double freq = frequencyHz ?? AssumedFrequencyHz;
        if (frequencyHz is null)
            // N187 (D2): the note names the CLI flag that declares it (the text before it is unchanged).
            notes.Add(FormattableString.Invariant($"nominal frequency not given (MATPOWER files carry none); assumed {AssumedFrequencyHz:0.##} Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60)."));

        string caseName = name ?? ExtractFunctionName(clean) ?? "matpower";
        CheckStatements(clean, caseName, notes);

        // A missing baseMVA is MATPOWER's 100 MVA, but it is said, not assumed silently (a commented-out line used
        // to load on 100 MVA with no word).
        double? baseGiven = ExtractScalar(clean, "baseMVA");
        if (baseGiven is double bg && !(bg > 0 && double.IsFinite(bg)))
            throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.baseMVA is {bg}; it must be a finite number above 0.");
        double baseMva = baseGiven ?? 100.0;
        if (baseGiven is null) notes.Add("mpc.baseMVA not given; assumed 100 MVA (the MATPOWER default).");

        double[][] busRows = ExtractMatrix(clean, "bus")
            ?? throw new InvalidDataException("MATPOWER case has no mpc.bus matrix.");
        double[][]? genGiven = ExtractMatrix(clean, "gen");
        double[][]? branchGiven = ExtractMatrix(clean, "branch");
        if (genGiven is null) notes.Add("no mpc.gen matrix; the case has no generators.");
        if (branchGiven is null) notes.Add("no mpc.branch matrix; the case has no branches.");
        double[][] genRows = genGiven ?? Array.Empty<double[]>();
        double[][] branchRows = branchGiven ?? Array.Empty<double[]>();

        // Every row must carry the columns this parser reads (a short row used to be padded silently with defaults:
        // a gen row that stopped before STATUS loaded in service, a branch row before BR_STATUS likewise). The bus
        // VMAX / VMIN columns (12, 13) and ZONE (11) stay optional, as before. No number in these matrices may be NaN.
        CheckRows(busRows, "bus", 10, "BUS_I ... BASE_KV", caseName);
        CheckRows(genRows, "gen", 10, "GEN_BUS ... PMIN", caseName);
        CheckRows(branchRows, "branch", 11, "F_BUS ... BR_STATUS", caseName);

        // --- Buses (skip isolated type-4) ------------------------------------------------------
        var buses = new List<Bus>(busRows.Length);
        var isolated = new HashSet<int>();
        var liveIds = new HashSet<int>();
        int slackCount = 0;
        int unusableLimits = 0;

        for (int bi = 0; bi < busRows.Length; bi++)
        {
            double[] r = busRows[bi];
            // Ids and types are whole numbers; a fractional or out-of-range value used to be truncated (and any type
            // other than 2, 3 or 4 read as PQ), so a typo changed the network instead of being refused.
            int id = WholeNumber(r[0], "bus", bi, "BUS_I", caseName);
            int mpType = WholeNumber(r[1], "bus", bi, "BUS_TYPE", caseName);
            if (mpType is < 1 or > 4)
                throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.bus row {bi + 1} has BUS_TYPE {mpType}; it must be 1 (PQ), 2 (PV), 3 (reference) or 4 (isolated).");
            if (mpType == 4) { isolated.Add(id); continue; }

            BusType type = mpType switch
            {
                3 => BusType.Slack,
                2 => BusType.Pv,
                _ => BusType.Pq,
            };
            if (type == BusType.Slack) slackCount++;

            // F17: the bus's own voltage limits, VMAX (column 12, index 11) and VMIN (column 13, index 12). They are
            // always READ into the model (so a writer or converter keeps them), but no check USES them unless the
            // caller asks for per-bus limits (PowerFlowOptions.PerBusVoltageLimits): every MATPOWER file carries
            // these columns, so using them by default would move existing contingency verdicts. A row too short to
            // hold them, or a pair that is not usable as limits (non-finite, not above 0, VMIN above VMAX), leaves
            // the bus with no per-bus limits (the global band applies) and is counted in the notes.
            (double? vmin, double? vmax) = (null, null);
            if (r.Length >= 13)
            {
                double hi = r[11], lo = r[12];
                if (double.IsFinite(lo) && double.IsFinite(hi) && lo > 0 && hi > 0 && lo <= hi) (vmin, vmax) = (lo, hi);
                else unusableLimits++;
            }

            buses.Add(new Bus(
                Id: id,
                Type: type,
                Pd: At(r, 2),
                Qd: At(r, 3),
                Gs: At(r, 4),
                Bs: At(r, 5),
                BaseKv: At(r, 9),
                Vm: At(r, 7, 1.0),
                VaDeg: At(r, 8),
                Name: null,
                VminPu: vmin,
                VmaxPu: vmax));
            liveIds.Add(id);
        }
        if (unusableLimits > 0)
            notes.Add($"{unusableLimits} bus(es) carry VMAX/VMIN columns that are not usable as voltage limits (non-finite, not above 0, or VMIN above VMAX); the global band applies to them.");

        if (slackCount == 0)
            throw new InvalidDataException($"MATPOWER case '{caseName}' has no reference (type 3) bus  -  the power flow would be singular.");
        if (slackCount > 1)
            notes.Add($"{slackCount} reference (slack) buses  -  treating as a multi-island system.");
        if (isolated.Count > 0)
            notes.Add($"dropped {isolated.Count} isolated (type 4) bus(es) and any incident elements.");

        // --- Generator costs (mpc.gencost, F24): parsed first so each unit can carry its own rows ------------------
        // Row i is the active-power cost of mpc.gen row i; when the matrix has 2 x ng rows, row ng + i is that unit's
        // reactive-power cost. Carried on the Generator, not used by any solver yet (F19). A malformed matrix is
        // refused naming the row, like every other parse error here.
        double[][]? costRows = ExtractMatrix(clean, "gencost");
        (GeneratorCost? P, GeneratorCost? Q)[] costs = ParseGenCost(costRows, genRows.Length, caseName);

        // --- Generators (keep out-of-service units, marked; skip those on dropped buses) -----------------------
        // F16 (October 2026): a status-0 unit used to be dropped here, so the model forgot it existed. It is now
        // KEPT with InService = false: GridModel.Generators (what every engine solves) still leaves it out, exactly
        // as before, while GridModel.AllGenerators and the writers keep it. A unit on a dropped or undefined bus is
        // still skipped (there is no bus to keep it on), in service or not.
        var gens = new List<Generator>(genRows.Length);
        int genOffKept = 0, genOffSkipped = 0, genDropped = 0, genUnknownBus = 0, genZeroMbase = 0;
        for (int gi = 0; gi < genRows.Length; gi++)
        {
            double[] r = genRows[gi];
            int bus = WholeNumber(r[0], "gen", gi, "GEN_BUS", caseName);
            bool inService = Status(r[7], "gen", gi, "GEN_STATUS", caseName);
            if (!liveIds.Contains(bus))
            {
                // On a dropped (isolated) bus, or on a bus id the file never defines. Both used to vanish with no
                // note and no count (R030); a generator on a non-existent bus is a data error worth seeing.
                if (!inService) genOffSkipped++;
                else if (isolated.Contains(bus)) genDropped++;
                else genUnknownBus++;
                continue;
            }
            if (!inService) genOffKept++;

            double mBase = At(r, 6);
            // A negative machine base is not data; 0 means "the system base" in MATPOWER files and is replaced, counted.
            if (mBase < 0 || double.IsNaN(mBase))
                throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.gen row {gi + 1} has mBase {mBase}; it must be 0 (use the system base) or above.");
            if (mBase == 0) genZeroMbase++;
            // GR-651: limits the wrong way round are not limits (QMIN above QMAX, PMIN above PMAX).
            if (At(r, 4, double.NegativeInfinity) > At(r, 3, double.PositiveInfinity) || At(r, 9) > At(r, 8, double.PositiveInfinity))
                throw new InvalidDataException(
                    $"MATPOWER case '{caseName}': mpc.gen row {gi + 1} has QMIN above QMAX or PMIN above PMAX " +
                    $"(Q {At(r, 4)} / {At(r, 3)}, P {At(r, 9)} / {At(r, 8)}).");
            gens.Add(new Generator(
                Bus: bus,
                Pg: At(r, 1),
                Qg: At(r, 2),
                Vg: At(r, 5, 1.0),
                Qmin: At(r, 4, double.NegativeInfinity),
                Qmax: At(r, 3, double.PositiveInfinity),
                Pmin: At(r, 9),
                Pmax: At(r, 8, double.PositiveInfinity),
                MBase: mBase > 0 ? mBase : baseMva,   // 0 (MATPOWER's "use the system base") replaced, and noted below
                H: 0,
                Name: null,
                InService: inService,
                Cost: costs.Length > 0 ? costs[gi].P : null,
                ReactiveCost: costs.Length > 0 ? costs[gi].Q : null));
        }
        if (genOffKept > 0) notes.Add($"kept {genOffKept} out-of-service generator(s) in the model, marked out of service (not solved).");
        if (genOffSkipped > 0) notes.Add($"skipped {genOffSkipped} out-of-service generator(s) on a dropped or undefined bus.");
        if (genZeroMbase > 0) notes.Add($"{genZeroMbase} generator(s) give mBase 0; their machine base is the system base ({baseMva} MVA).");
        if (genDropped > 0) notes.Add($"skipped {genDropped} in-service generator(s) on dropped isolated (type 4) bus(es).");
        if (genUnknownBus > 0) notes.Add($"skipped {genUnknownBus} in-service generator(s) whose bus id is not defined in mpc.bus.");
        if (costs.Length > 0)
            notes.Add($"read mpc.gencost ({costRows!.Length} row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.");

        // --- DC lines (mpc.dcline, F24): parsed and carried, NOT modelled ------------------------------------------
        IReadOnlyList<MatpowerDcLine> dcLines = ParseDcLines(ExtractMatrix(clean, "dcline"), caseName);
        if (dcLines.Count > 0)
            notes.Add($"read {dcLines.Count} DC line(s) (mpc.dcline); they are carried in the parse result but NOT modelled by any solver (MATPOWER adds their flows as injections at both ends), so this power flow differs from MATPOWER's for this case.");

        // A type-2 (PV) bus whose generators are ALL out of service has nothing to hold its voltage. MATPOWER's
        // bustypes() solves such a bus as PQ, and so must we (R030, October 2026): left as PV it was held at its
        // file Vm with unlimited reactive power wherever Q limits are off (the crossval and replay paths), and the
        // solution diverged from MATPOWER's. Demote it here, where the reason is known, and say so. (The Newton
        // engines also treat a generator-less PV bus as PQ, R283, so the numbers are the same either way; this
        // makes the model itself honest.) A reference bus with no in-service generator is reported, not changed:
        // MATPOWER would promote the first PV bus to reference, which is a modelling choice for the caller.
        var genBuses = new HashSet<int>();
        foreach (Generator g in gens) if (g.InService) genBuses.Add(g.Bus);   // an out-of-service unit holds no voltage (F16)
        int demoted = 0;
        for (int i = 0; i < buses.Count; i++)
        {
            Bus b = buses[i];
            if (b.Type == BusType.Pv && !genBuses.Contains(b.Id)) { buses[i] = b with { Type = BusType.Pq }; demoted++; }
            else if (b.Type == BusType.Slack && !genBuses.Contains(b.Id))
                notes.Add($"reference (type 3) bus {b.Id} has no in-service generator (MATPOWER would promote the first PV bus to reference); it is kept as the reference.");
        }
        if (demoted > 0) notes.Add($"solved {demoted} PV (type 2) bus(es) with no in-service generator as PQ, as MATPOWER does.");

        // --- Branches (skip out-of-service and those touching dropped buses) -------------------
        var branches = new List<Branch>(branchRows.Length);
        int brOff = 0, brDangling = 0, brAngleLimits = 0;
        for (int ri = 0; ri < branchRows.Length; ri++)
        {
            double[] r = branchRows[ri];
            if (!Status(r[10], "branch", ri, "BR_STATUS", caseName)) { brOff++; continue; }
            // ANGMIN / ANGMAX (columns 12, 13) are not modelled. MATPOWER reads 0 or +/-360 as "no limit"; any other
            // pair is an angle-difference limit that this model does not carry, so it is counted for the notes.
            if (r.Length >= 13 && !((r[11] <= -360 || r[11] == 0) && (r[12] >= 360 || r[12] == 0))) brAngleLimits++;
            int from = WholeNumber(r[0], "branch", ri, "F_BUS", caseName);
            int to = WholeNumber(r[1], "branch", ri, "T_BUS", caseName);
            if (!liveIds.Contains(from) || !liveIds.Contains(to)) { brDangling++; continue; }

            double ratio = At(r, 8);
            // TAP 0 is MATPOWER's "a line, not a transformer"; a negative ratio is not data.
            if (ratio < 0)
                throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.branch row {ri + 1} has TAP {ratio}; it must be 0 (a line) or above.");
            bool isXfmr = ratio != 0.0;
            branches.Add(new Branch(
                From: from,
                To: to,
                R: At(r, 2),
                X: At(r, 3),
                B: At(r, 4),
                TapRatio: isXfmr ? ratio : 1.0,
                PhaseShiftDeg: At(r, 9),
                RateAMva: At(r, 5),
                Name: null,
                IsTransformer: isXfmr,
                RateBMva: At(r, 6),     // F20: RATE_B (short-term); carried, used only by an opt-in post-fault check
                RateCMva: At(r, 7)));   // F20: RATE_C (emergency); likewise
        }
        if (brOff > 0) notes.Add($"skipped {brOff} out-of-service branch(es).");
        if (brDangling > 0) notes.Add($"skipped {brDangling} branch(es) incident to a dropped bus.");
        if (brAngleLimits > 0) notes.Add($"{brAngleLimits} in-service branch(es) carry angle-difference limits (ANGMIN / ANGMAX); they are not read, and no study here enforces them.");

        var model = new GridModel
        {
            Name = caseName,
            BaseMva = baseMva,
            FrequencyHz = freq,
            Buses = buses,
            Generators = gens,
            Branches = branches,
            // Rows with no bus to sit on: a file-order generator index no longer lines up when any were skipped.
            DroppedGeneratorRows = genOffSkipped + genDropped + genUnknownBus,
        };

        // Post-build safety net: fail at the import point on any hard structural fault
        // (duplicate bus id, no reference bus, dangling endpoint) rather than deep in the solver.
        var errors = model.Validate()
            .Where(d => d.Severity == ValidationSeverity.Error)
            .Select(d => d.Message)
            .ToList();
        if (errors.Count > 0)
            throw new InvalidDataException($"MATPOWER case '{caseName}' is invalid:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", errors));

        return new MatpowerParseResult(model, notes) { DcLines = dcLines };
    }

    // -------------------------------------------------------------------------------------------

    private static double At(double[] row, int i, double dflt = 0.0)
        => i >= 0 && i < row.Length ? row[i] : dflt;

    /// <summary>Refuse a row shorter than the columns the parser reads, and any NaN in the matrix (Inf stays legal:
    /// it is MATPOWER's open generator limit).</summary>
    private static void CheckRows(double[][] rows, string matrix, int minColumns, string columns, string caseName)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Length < minColumns)
                throw new InvalidDataException(
                    $"MATPOWER case '{caseName}': mpc.{matrix} row {i + 1} has {rows[i].Length} number(s); the {minColumns} standard columns ({columns}) are required.");
            int nan = Array.FindIndex(rows[i], double.IsNaN);
            if (nan >= 0)
                throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.{matrix} row {i + 1} column {nan + 1} is NaN.");
        }
    }

    /// <summary>An id or type column: a finite whole number in the int range, or the parse is refused.</summary>
    private static int WholeNumber(double v, string matrix, int row, string column, string caseName)
    {
        if (!double.IsFinite(v) || v != Math.Floor(v) || v < int.MinValue || v > int.MaxValue)
            throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.{matrix} row {row + 1} has {column} {v}; it must be a whole number.");
        return (int)v;
    }

    /// <summary>A status column: MATPOWER reads any value above 0 as in service. A negative or non-finite status is
    /// refused rather than read as out of service.</summary>
    private static bool Status(double v, string matrix, int row, string column, string caseName)
    {
        if (!double.IsFinite(v) || v < 0)
            throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.{matrix} row {row + 1} has {column} {v}; it must be 0 (out of service) or above.");
        return v > 0;
    }

    /// <summary>
    /// Only the first <c>mpc.&lt;field&gt; = ...</c> of each field is read; the file is not run as MATLAB. A second
    /// whole assignment of a field is ambiguous (which one is the case?) and is refused. Indexed assignments after the
    /// matrices (<c>mpc.bus(:, [PD, QD]) = ...</c>, as the published raw-unit distribution feeders use to convert their
    /// units) are not executed: they are counted in a note, so the reader knows the file asked for more than was
    /// applied. The unit conversion those feeders ask for is done by <see cref="MatpowerUnitNormalizer"/>.
    /// </summary>
    private static void CheckStatements(string text, string caseName, List<string> notes)
    {
        foreach (string field in new[] { "baseMVA", "bus", "gen", "branch", "gencost", "dcline" })
        {
            int whole = Regex.Count(text, @"\bmpc\." + field + @"\b\s*=(?!=)");
            if (whole > 1)
                throw new InvalidDataException($"MATPOWER case '{caseName}' assigns mpc.{field} {whole} times; only one assignment can be read.");
        }
        MatchCollection indexed = Regex.Matches(text, @"(?m)^\s*mpc\.(bus|gen|branch|gencost|dcline)\s*\([^;\r\n]*?\)\s*=(?!=)");
        if (indexed.Count > 0)
        {
            string fields = string.Join(", ", indexed.Select(x => "mpc." + x.Groups[1].Value).Distinct(StringComparer.Ordinal));
            notes.Add($"the file has {indexed.Count} indexed assignment(s) after the matrices ({fields}); MATLAB statements are not run, " +
                      "so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).");
        }
    }

    /// <summary>
    /// Parse <c>mpc.gencost</c> (F24) into one (active, reactive) cost pair per <c>mpc.gen</c> row. Returns an empty
    /// array when the file has no gencost matrix. MATPOWER's layout: MODEL STARTUP SHUTDOWN NCOST COST...; MODEL 1 is
    /// piecewise linear with 2 x NCOST numbers (x1 y1 x2 y2 ...), MODEL 2 a polynomial with NCOST coefficients,
    /// highest order first. Rows of different NCOST are padded with trailing numbers in a MATLAB matrix, so numbers
    /// after the curve are allowed and ignored. The matrix must have exactly ng rows (active costs) or 2 x ng (active,
    /// then reactive). Anything else is refused, naming the row (1-based, as in the file).
    /// </summary>
    private static (GeneratorCost? P, GeneratorCost? Q)[] ParseGenCost(double[][]? rows, int genCount, string caseName)
    {
        if (rows is null || rows.Length == 0) return Array.Empty<(GeneratorCost?, GeneratorCost?)>();
        if (rows.Length != genCount && rows.Length != 2 * genCount)
            throw new InvalidDataException(
                $"MATPOWER case '{caseName}': mpc.gencost has {rows.Length} row(s); it must have one per mpc.gen row ({genCount}) or two per row ({2 * genCount}: active then reactive costs).");

        var parsed = new GeneratorCost[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            double[] r = rows[i];
            string where = $"MATPOWER case '{caseName}': mpc.gencost row {i + 1}";
            if (r.Length < 4) throw new InvalidDataException($"{where} has {r.Length} number(s); at least MODEL STARTUP SHUTDOWN NCOST are required.");
            double modelCode = r[0], nCost = r[3];
            GeneratorCostModel model = modelCode switch
            {
                1.0 => GeneratorCostModel.PiecewiseLinear,
                2.0 => GeneratorCostModel.Polynomial,
                _ => throw new InvalidDataException($"{where}: MODEL is {modelCode.ToString("R", CultureInfo.InvariantCulture)}; it must be 1 (piecewise linear) or 2 (polynomial)."),
            };
            if (!(nCost >= 1) || nCost != Math.Floor(nCost) || nCost > int.MaxValue / 2)
                throw new InvalidDataException($"{where}: NCOST is {nCost.ToString("R", CultureInfo.InvariantCulture)}; it must be a whole number of at least 1.");
            int n = (int)nCost;
            int count = model == GeneratorCostModel.Polynomial ? n : 2 * n;
            if (r.Length < 4 + count)
                throw new InvalidDataException($"{where}: NCOST {n} needs {count} cost number(s) after NCOST but the row has only {r.Length - 4}.");
            for (int k = 0; k < 4 + count; k++)
                if (!double.IsFinite(r[k]))
                    throw new InvalidDataException($"{where}: column {k + 1} is {r[k].ToString("R", CultureInfo.InvariantCulture)}, which is not a finite number.");
            parsed[i] = new GeneratorCost(model, r[1], r[2], r.Skip(4).Take(count).ToArray());
        }

        var pairs = new (GeneratorCost? P, GeneratorCost? Q)[genCount];
        for (int g = 0; g < genCount; g++)
            pairs[g] = (parsed[g], rows.Length == 2 * genCount ? parsed[genCount + g] : null);
        return pairs;
    }

    /// <summary>
    /// Parse <c>mpc.dcline</c> (F24): the 17 standard MATPOWER columns per row (F_BUS T_BUS BR_STATUS PF PT QF QT VF VT
    /// PMIN PMAX QMINF QMAXF QMINT QMAXT LOSS0 LOSS1); any further columns (the OPF cost and multiplier columns) are
    /// ignored. A row shorter than 17 numbers is refused, naming the row. Empty when the file has no dcline matrix.
    /// </summary>
    private static IReadOnlyList<MatpowerDcLine> ParseDcLines(double[][]? rows, string caseName)
    {
        if (rows is null || rows.Length == 0) return Array.Empty<MatpowerDcLine>();
        var lines = new List<MatpowerDcLine>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            double[] r = rows[i];
            if (r.Length < MatpowerDcLine.ColumnCount)
                throw new InvalidDataException(
                    $"MATPOWER case '{caseName}': mpc.dcline row {i + 1} has {r.Length} number(s); the {MatpowerDcLine.ColumnCount} standard columns (F_BUS ... LOSS1) are required.");
            // GR-668: the same row rules as the other matrices: no NaN, whole-number bus ids, a status of 0 or above.
            int nan = Array.FindIndex(r, double.IsNaN);
            if (nan >= 0)
                throw new InvalidDataException($"MATPOWER case '{caseName}': mpc.dcline row {i + 1} column {nan + 1} is NaN.");
            int fromBus = WholeNumber(r[0], "dcline", i, "F_BUS", caseName);
            int toBus = WholeNumber(r[1], "dcline", i, "T_BUS", caseName);
            bool dcInService = Status(r[2], "dcline", i, "BR_STATUS", caseName);
            lines.Add(new MatpowerDcLine(
                FromBus: fromBus, ToBus: toBus, InService: dcInService,
                PfMw: r[3], PtMw: r[4], QfMvar: r[5], QtMvar: r[6], VfPu: r[7], VtPu: r[8],
                PminMw: r[9], PmaxMw: r[10], QminFMvar: r[11], QmaxFMvar: r[12], QminTMvar: r[13], QmaxTMvar: r[14],
                Loss0Mw: r[15], Loss1: r[16]));
        }
        return lines;
    }

    /// <summary>Parse a MATLAB numeric token: plain numbers, <c>Inf</c>/<c>-Inf</c> (open generator
    /// limits), and simple arithmetic expressions such as <c>135/sqrt(3)</c> that some case files
    /// (e.g. the ACTIVSg synthetic systems) use for base voltages.</summary>
    private static double ParseNum(string t)
    {
        switch (t)
        {
            case "Inf" or "inf" or "+Inf" or "Infinity": return double.PositiveInfinity;
            case "-Inf" or "-inf" or "-Infinity": return double.NegativeInfinity;
            case "NaN" or "nan": return double.NaN;
        }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
        int pos = 0;
        double result = EvalExpr(t, ref pos);
        if (pos != t.Length) throw new FormatException($"Unparseable numeric token '{t}'.");
        return result;
    }

    // A minimal recursive-descent evaluator for the arithmetic MATPOWER occasionally embeds in data
    // matrices: + - * /, parentheses, unary sign, sqrt(...), and the constant pi. Invariant culture.
    private static double EvalExpr(string s, ref int i)
    {
        double val = EvalTerm(s, ref i);
        while (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            char op = s[i++];
            double rhs = EvalTerm(s, ref i);
            val = op == '+' ? val + rhs : val - rhs;
        }
        return val;
    }

    private static double EvalTerm(string s, ref int i)
    {
        double val = EvalFactor(s, ref i);
        while (i < s.Length && (s[i] == '*' || s[i] == '/'))
        {
            char op = s[i++];
            double rhs = EvalFactor(s, ref i);
            val = op == '*' ? val * rhs : val / rhs;
        }
        return val;
    }

    private static double EvalFactor(string s, ref int i)
    {
        if (i >= s.Length) throw new FormatException($"Unexpected end of expression in '{s}'.");
        char c = s[i];
        if (c == '+') { i++; return EvalFactor(s, ref i); }
        if (c == '-') { i++; return -EvalFactor(s, ref i); }
        if (c == '(') { i++; double v = EvalExpr(s, ref i); Expect(s, ref i, ')'); return v; }
        if (Match(s, ref i, "sqrt")) { Expect(s, ref i, '('); double v = EvalExpr(s, ref i); Expect(s, ref i, ')'); return Math.Sqrt(v); }
        if (Match(s, ref i, "pi")) return Math.PI;

        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
               ((s[i] == '+' || s[i] == '-') && i > start && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
            i++;
        if (i == start) throw new FormatException($"Unexpected character '{c}' in expression '{s}'.");
        return double.Parse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static bool Match(string s, ref int i, string word)
    {
        if (i + word.Length <= s.Length && s.Substring(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
        { i += word.Length; return true; }
        return false;
    }

    private static void Expect(string s, ref int i, char c)
    {
        if (i >= s.Length || s[i] != c) throw new FormatException($"Expected '{c}' in expression '{s}'.");
        i++;
    }

    /// <summary>Strip <c>%</c> comments and join <c>...</c> line continuations.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        string pending = "";
        // Bug hunt 2026-10-29, finding 49: MATLAB block comments. A line that holds only "%{" opens a block and one that
        // holds only "%}" closes it (blocks nest, as in MATLAB); every line inside is comment. Only "%" line comments
        // used to be stripped, so the text of a commented-out block was parsed as live data (a commented-out
        // mpc.gencost became real costs without a word). Each removed line is kept as an empty line, so line numbers
        // in later messages do not move, and a block never closed is refused.
        int blockDepth = 0;
        foreach (string raw in text.Split('\n'))
        {
            string bare = raw.TrimEnd('\r').Trim();
            if (bare == "%{") { blockDepth++; sb.Append('\n'); continue; }
            if (blockDepth > 0)
            {
                if (bare == "%}") blockDepth--;
                sb.Append('\n');
                continue;
            }
            string line = StripComment(raw.TrimEnd('\r'));
            string trimmed = line.TrimEnd();
            if (trimmed.EndsWith("...", StringComparison.Ordinal))
            {
                pending += trimmed[..^3] + " ";
                continue;
            }
            sb.Append(pending).Append(line).Append('\n');
            pending = "";
        }
        if (pending.Length > 0) sb.Append(pending).Append('\n');
        if (blockDepth > 0)
            throw new InvalidDataException($"MATPOWER case: {blockDepth} block comment(s) opened with %{{ are never closed with %}}.");
        return sb.ToString();
    }

    /// <summary>Remove a trailing <c>%</c> comment, respecting single-quoted strings (bus names).</summary>
    private static string StripComment(string line)
    {
        bool inStr = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'')
            {
                if (inStr)
                {
                    // Inside a string '' is an escaped quote; a single ' closes the string.
                    if (i + 1 < line.Length && line[i + 1] == '\'') { i++; continue; }
                    inStr = false;
                }
                else
                {
                    // GR-660: an apostrophe straight after a name, a number, a closing bracket, a dot or another quote is
                    // MATLAB's transpose operator, not the start of a string (it used to open a "string" that hid the
                    // comment after it, or swallowed the rest of the line).
                    char prev = i > 0 ? line[i - 1] : ' ';
                    bool transpose = char.IsAsciiLetterOrDigit(prev) || prev is '_' or ')' or ']' or '}' or '.' or '\'';
                    if (!transpose) inStr = true;
                }
            }
            else if (c == '%' && !inStr) return line[..i];
        }
        return line;
    }

    private static string? ExtractFunctionName(string text)
    {
        Match m = Regex.Match(text, @"function\s+mpc\s*=\s*([A-Za-z0-9_]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Extract a scalar <c>mpc.&lt;field&gt; = &lt;value&gt;;</c>. The whole right-hand side, up to the
    /// terminating <c>;</c> or end of line, goes through <see cref="ParseNum"/>, the same evaluator the matrices use,
    /// so an expression such as <c>mpc.baseMVA = 50/3;</c> (case533mt_hi) is evaluated to 16.667, not truncated to
    /// its leading 50. Anything the evaluator cannot consume completely is refused (fail loud): the old regex read the
    /// leading number and silently ignored the rest of the statement.</summary>
    private static double? ExtractScalar(string text, string field)
    {
        // Whole-word field match ("mpc.baseMVA" must not also match a longer field name), then the right-hand side
        // as everything up to ';' or the end of the line.
        Match m = Regex.Match(text, @"\bmpc\." + Regex.Escape(field) + @"\b\s*=\s*([^;\r\n]*)");
        if (!m.Success) return null;

        // MATLAB allows spaces inside an expression ("50 / 3"); the evaluator does not, so drop them first.
        string rhs = Regex.Replace(m.Groups[1].Value, @"\s+", "");
        if (rhs.Length == 0)
            throw new FormatException($"mpc.{field} has no value.");
        try
        {
            return ParseNum(rhs);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"mpc.{field} = '{m.Groups[1].Value.Trim()}' is not a number or a supported " +
                                      "arithmetic expression.", ex);
        }
    }

    /// <summary>Extract the first <c>mpc.&lt;field&gt; = [ ... ];</c> matrix as rows of doubles.</summary>
    private static double[][]? ExtractMatrix(string text, string field)
    {
        // Whole-word field match so "mpc.bus" does not also match "mpc.bus_name".
        Match m = Regex.Match(text, @"\bmpc\." + Regex.Escape(field) + @"\b\s*=\s*\[", RegexOptions.None);
        if (!m.Success) return null;

        int start = m.Index + m.Length;              // just after '['
        int end = text.IndexOf(']', start);
        // An unclosed matrix is a broken file, not an absent matrix (it used to read as empty).
        if (end < 0) throw new InvalidDataException($"mpc.{field} = [ has no closing ']'.");
        // GR-647: a '[' before that ']' means this matrix was never closed and the next one has started; the first ']'
        // belongs to that next matrix, so taking it read the two as one.
        int nextOpen = text.IndexOf('[', start);
        if (nextOpen >= 0 && nextOpen < end)
            throw new InvalidDataException($"mpc.{field} = [ is not closed before the next '[' (a missing ']').");

        string body = text[start..end];
        var rows = new List<double[]>();
        // In MATLAB matrix literals a row ends at a ';' OR a newline (both are valid, and '...' line
        // continuations were already joined upstream). Splitting only on ';' and then treating '\n' as an
        // intra-row separator collapsed a newline-delimited file into ONE row of concatenated values.
        foreach (string rawRow in body.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = rawRow.Split(
                new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var vals = new double[tokens.Length];
            // GR-657: a token that is not a number is reported with its matrix, row and column (it surfaced as a bare
            // FormatException naming only the token).
            for (int i = 0; i < tokens.Length; i++)
            {
                try { vals[i] = ParseNum(tokens[i]); }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"mpc.{field} row {rows.Count + 1} column {i + 1}: '{tokens[i]}' is not a number or a supported expression.", ex);
                }
            }
            rows.Add(vals);
        }
        return rows.ToArray();
    }
}
