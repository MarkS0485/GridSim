using System.Globalization;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;

namespace GridSim.Cli;

// ---------------------------------------------------------------------------------------------------------------------
// F front doors (October 2026 review, R079 and the R095 remainder; Ruling R1: Mark delegated the EE / product calls
// 2026-10-04). Several engines in Core were finished and tested but no user could reach them: no verb, no flag. This file
// holds the pure parts of the new front doors (parsing and the small model edits), with no console and no process exit,
// so the tests drive them directly (the file is linked into the test project, as VerbHelp.cs and StorageCli.cs are).
// Program.cs does the flag reading, the printing and the exit codes, exactly as every other verb does.
//
//   fit-frequency <trace.csv>       FrequencyConstantsFitter: fit the swing model's nadir / recovery constants to a
//                                   measured loss-of-infeed trace (R079). FrequencyTraceFile reads the trace,
//                                   FrequencyFitCli parses --fit and finds the pre-event frequency.
//   trip --band[=<fraction>]        FrequencySensitivitySweep: the nadir band over the unfitted swing constants (R079).
//   trip --asset-dynamics --envelope <spec>
//                                   AssetEnvelope.Sweep: the nadir / RoCoF envelope over asset-class control parameters
//                                   (R095). AssetEnvelopeSpec parses the spec against the case's class library.
//   --dyn-data on trip / transient / modal
//                                   the gridsim-dynamics/2 machine file (F23, B3) beyond stability --rms. DynDataCli applies
//                                   the parts each engine models.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Reads a measured frequency trace for <c>fit-frequency</c>: two numeric columns, <c>seconds,Hz</c>, the time in seconds
/// from the start of the window (the simulation clock: the swing model starts at 0 s). Fail loud: every bad row is an
/// <see cref="InvalidDataException"/> naming the line number and the line.
/// </summary>
internal static class FrequencyTraceFile
{
    /// <summary>
    /// The largest distance (Hz) a sample may lie from the nominal the fitter's swing model runs at. A real excursion after
    /// a loss of infeed stays within about 2 Hz of nominal (GB LFDD starts at 48.8 Hz, Grid Code CC.A.5.5.1); a sample 5 Hz
    /// or more away is not a frequency of that system (a 60 Hz trace read as 50 Hz, or a column of something else), and
    /// fitting the model to it would give a confident, meaningless answer. The same 5 Hz margin as the protection-setting
    /// plausibility check (<see cref="AssetClassSupport.MaxFrequencySettingOffsetHz"/>).
    /// </summary>
    public const double MaxOffsetFromNominalHz = AssetClassSupport.MaxFrequencySettingOffsetHz;

    /// <summary>The fewest samples the fitter accepts (<see cref="FrequencyConstantsFitter.Fit"/> refuses fewer than three).</summary>
    public const int MinimumSamples = 3;

    /// <summary>
    /// N314 (LF5, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the nominal frequencies a trace's own
    /// samples may name: the two AC system frequencies in use (50 Hz: GB, Europe; 60 Hz: North America, the WSCC / IEEE
    /// cases). They are 10 Hz apart, twice <see cref="MaxOffsetFromNominalHz"/>, so a sample can lie within the margin of
    /// at most one of them and the inference is never ambiguous. Any other nominal (a 25 Hz or 16.7 Hz railway system) is
    /// declared with <c>--nominal-hz</c>.
    /// </summary>
    public static readonly IReadOnlyList<double> StandardNominalsHz = new[] { 50.0, 60.0 };

    /// <summary>
    /// N314: the smallest nominal <c>--nominal-hz</c> accepts is more than <see cref="MaxOffsetFromNominalHz"/>, so the
    /// accepted band nominal +/- 5 Hz stays above 0 Hz (a frequency of 0 Hz or less is not a frequency).
    /// </summary>
    public static bool IsAcceptableNominal(double nominalHz) => double.IsFinite(nominalHz) && nominalHz > MaxOffsetFromNominalHz;

    /// <summary>
    /// N314: the standard nominal (<see cref="StandardNominalsHz"/>) the frequency <paramref name="hz"/> lies within
    /// <see cref="MaxOffsetFromNominalHz"/> of, or null when it lies near neither.
    /// </summary>
    public static double? StandardNominalFor(double hz)
    {
        foreach (double nominal in StandardNominalsHz)
            if (Math.Abs(hz - nominal) < MaxOffsetFromNominalHz) return nominal;
        return null;
    }

    /// <summary>Read the trace file at <paramref name="path"/> as a 50 Hz trace (the original rule; see <see cref="Parse(IReadOnlyList{string}, string)"/>).</summary>
    public static IReadOnlyList<FrequencyTraceSample> Read(string path) => Read(path, FrequencyLimits.NominalHz, out _);

    /// <summary>
    /// N314: read the trace file at <paramref name="path"/> at the nominal <paramref name="nominalHz"/>, or, when it is
    /// null, at the nominal the trace's first data row names (see the four-argument <c>Parse</c>). A missing file throws
    /// <see cref="FileNotFoundException"/>.
    /// </summary>
    public static IReadOnlyList<FrequencyTraceSample> Read(string path, double? nominalHz, out double usedNominalHz)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"trace file '{path}' was not found.", path);
        return Parse(File.ReadAllLines(path), path, nominalHz, out usedNominalHz);
    }

    /// <summary>
    /// Parse the lines of a 50 Hz trace (the F stream's original rule, kept exactly: every sample within
    /// <see cref="MaxOffsetFromNominalHz"/> of 50 Hz). See the four-argument form for the rules.
    /// </summary>
    public static IReadOnlyList<FrequencyTraceSample> Parse(IReadOnlyList<string> lines, string source)
        => Parse(lines, source, FrequencyLimits.NominalHz, out _);

    /// <summary>
    /// Parse the lines of a trace. Rules (each broken rule throws <see cref="InvalidDataException"/> naming the line):
    /// blank lines are skipped; the first non-blank line may be a header (it is a header when its first field is not a
    /// number); every other line is exactly two fields, a finite time of 0 s or more and a finite frequency within
    /// <see cref="MaxOffsetFromNominalHz"/> of the nominal; times strictly increase; at least <see cref="MinimumSamples"/> rows.
    ///
    /// <para>The nominal (N314, LF5, October 2026; Ruling R1): <paramref name="nominalHz"/> when given (checked with
    /// <see cref="IsAcceptableNominal"/>; <c>--nominal-hz</c>). When it is null, the trace names its own: the standard
    /// nominal (50 or 60 Hz) the FIRST data row lies within 5 Hz of, and every later row is then held to that nominal. EE
    /// call: the first row is the pre-disturbance state of a loss-of-infeed window (the fitter's own input shape), the
    /// sample least likely to be far from nominal; a first row near neither 50 nor 60 Hz is refused naming the line and
    /// pointing at --nominal-hz, never guessed. The nominal used is returned in <paramref name="usedNominalHz"/>.</para>
    /// </summary>
    public static IReadOnlyList<FrequencyTraceSample> Parse(IReadOnlyList<string> lines, string source, double? nominalHz, out double usedNominalHz)
    {
        if (nominalHz is double given && !IsAcceptableNominal(given))
            throw new ArgumentOutOfRangeException(nameof(nominalHz), given, $"the nominal must be finite and above {MaxOffsetFromNominalHz} Hz.");
        var samples = new List<FrequencyTraceSample>();
        double? nominal = nominalHz;
        bool firstNonBlank = true;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            string[] fields = line.Split(',');
            bool timeParses = double.TryParse(fields[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t);
            if (firstNonBlank)
            {
                firstNonBlank = false;
                if (!timeParses) continue;   // a header such as "seconds,Hz"
            }
            if (fields.Length != 2)
                throw new InvalidDataException($"{source}: line {i + 1} has {fields.Length} field(s), expected two (seconds,Hz): '{line}'.");
            if (!timeParses || !double.IsFinite(t) || t < 0.0)
                throw new InvalidDataException($"{source}: line {i + 1}: the time is not a finite number of seconds, 0 or more: '{line}'.");
            if (!double.TryParse(fields[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double hz) || !double.IsFinite(hz))
                throw new InvalidDataException($"{source}: line {i + 1}: the frequency is not a finite number: '{line}'.");
            // N314: no nominal given, so the first data row names it (50 or 60 Hz), or the trace is refused here.
            nominal ??= StandardNominalFor(hz)
                ?? throw new InvalidDataException($"{source}: line {i + 1}: {hz.ToString("R", CultureInfo.InvariantCulture)} Hz, the first sample, "
                    + $"is not within {MaxOffsetFromNominalHz.ToString("R", CultureInfo.InvariantCulture)} Hz of a standard nominal (50 or 60 Hz), "
                    + "so the trace does not say which system it was recorded on; give the nominal with --nominal-hz <Hz>: '" + line + "'.");
            double nom = nominal.Value;
            if (Math.Abs(hz - nom) >= MaxOffsetFromNominalHz)
                throw new InvalidDataException($"{source}: line {i + 1}: {hz.ToString("R", CultureInfo.InvariantCulture)} Hz is "
                    + $"{MaxOffsetFromNominalHz.ToString("R", CultureInfo.InvariantCulture)} Hz or more from the {nom.ToString("R", CultureInfo.InvariantCulture)} Hz "
                    + $"nominal the fitter's swing model runs at; this is not a {nom.ToString("R", CultureInfo.InvariantCulture)} Hz loss-of-infeed trace: '" + line + "'.");
            if (samples.Count > 0 && !(t > samples[^1].Seconds))
                throw new InvalidDataException($"{source}: line {i + 1}: the time {t.ToString("R", CultureInfo.InvariantCulture)} s does not "
                    + $"increase on the previous row ({samples[^1].Seconds.ToString("R", CultureInfo.InvariantCulture)} s): '{line}'.");
            samples.Add(new FrequencyTraceSample(t, hz));
        }
        if (samples.Count < MinimumSamples)
            throw new InvalidDataException($"{source}: {samples.Count} data row(s); the fit needs at least {MinimumSamples} (seconds,Hz) rows.");
        usedNominalHz = nominal!.Value;   // at least three data rows were read, so the nominal is known
        return samples;
    }
}

/// <summary>The pure parts of <c>fit-frequency</c>: the <c>--fit</c> list, the pre-event frequency and the seed config.</summary>
internal static class FrequencyFitCli
{
    /// <summary>The <c>--fit</c> words, in the order they are documented: damping, responsive, governor.</summary>
    public static readonly IReadOnlyList<(string Word, FrequencyFitParam Param)> Words = new[]
    {
        ("damping", FrequencyFitParam.Damping),
        ("responsive", FrequencyFitParam.ResponsiveFraction),
        ("governor", FrequencyFitParam.GovernorTimeConstant),
    };

    /// <summary>
    /// Parse a <c>--fit</c> value: a comma list of damping, responsive and governor, each at most once. Null for any
    /// other word, an empty item or a repeat (the caller refuses it, exit 2). The order given is the order fitted.
    /// </summary>
    public static FrequencyFitParam[]? ParseFit(string spec)
    {
        var list = new List<FrequencyFitParam>();
        foreach (string raw in spec.Split(','))
        {
            string word = raw.Trim().ToLowerInvariant();
            int k = -1;
            for (int i = 0; i < Words.Count; i++) if (Words[i].Word == word) k = i;
            if (k < 0 || list.Contains(Words[k].Param)) return null;
            list.Add(Words[k].Param);
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    /// <summary>The <c>--fit</c> word for a parameter (for the printed report).</summary>
    public static string WordFor(FrequencyFitParam p) => Words.First(w => w.Param == p).Word;

    /// <summary>
    /// The recorded pre-event frequency: the last sample strictly before the loss instant, or null when the trace starts
    /// at or after the loss (the model then starts at nominal, its historical behaviour). EE call (Ruling R1): the swing
    /// model's services and governor respond to the ABSOLUTE frequency, so a trace that was already 0.05 Hz low before the
    /// loss is fitted from where it really started (<see cref="DynamicsConfig.InitialFrequencyHz"/>, the replay's own rule,
    /// gb-2023-12-22), not from 50 Hz with the offset blamed on the damping.
    /// </summary>
    public static double? PreEventHz(IReadOnlyList<FrequencyTraceSample> trace, double lossAtSeconds)
    {
        double? last = null;
        foreach (FrequencyTraceSample s in trace)
        {
            if (!(s.Seconds < lossAtSeconds)) break;   // times strictly increase (FrequencyTraceFile)
            last = s.Hz;
        }
        return last;
    }

    /// <summary>The seed configuration: the shipped defaults (the literature-typical, unfitted constants are the
    /// starting point of the search), started at the pre-event frequency when the trace gives one.</summary>
    public static DynamicsConfig Seed(double? preEventHz) =>
        preEventHz is null ? new DynamicsConfig() : new DynamicsConfig { InitialFrequencyHz = preEventHz };

    /// <summary>
    /// N314 (LF5, October 2026): the seed at the trace's nominal <paramref name="nominalHz"/> (inferred from the trace or
    /// given with --nominal-hz): the swing model then runs about that nominal (RoCoF = f0 dP / 2E, the governor and
    /// services measure their deviation from it). At 50 Hz this is exactly <see cref="Seed(double?)"/>: the config's
    /// default nominal is the same 50.0, so a 50 Hz fit is bit for bit what it was.
    /// </summary>
    public static DynamicsConfig Seed(double? preEventHz, double nominalHz) => Seed(preEventHz) with { NominalHz = nominalHz };
}

/// <summary>
/// Parses <c>trip --envelope</c>: a comma list of <c>&lt;class&gt;:&lt;param&gt;=&lt;low&gt;..&lt;high&gt;</c>, each one asset-class
/// control parameter and its plausible range, checked against the case's asset-dynamics class library before any run.
/// </summary>
internal static class AssetEnvelopeSpec
{
    /// <summary>The most parameters <see cref="AssetEnvelope.Sweep"/> takes (2^12 = 4096 corners).</summary>
    public const int MaxParameters = 12;

    /// <summary>
    /// Parse <paramref name="spec"/> against <paramref name="library"/>. Returns the sweep, or null with
    /// <paramref name="error"/> set to a one-line reason (the caller prints it and exits 2). Rules: the class must be in
    /// the library; the parameter must be one a class of that kind is simulated with (the same accepted-key table the
    /// library loader applies, <see cref="AssetClassSupport.AcceptedKeys"/>); low and high are finite with low &lt; high;
    /// no (class, parameter) twice; at most <see cref="MaxParameters"/> items.
    /// </summary>
    public static IReadOnlyList<AssetSweepParam>? Parse(string spec, AssetClassLibrary library, out string? error)
    {
        error = null;
        var sweep = new List<AssetSweepParam>();
        foreach (string raw in spec.Split(','))
        {
            string item = raw.Trim();
            int colon = item.IndexOf(':');
            int eq = item.IndexOf('=');
            int dots = item.IndexOf("..", StringComparison.Ordinal);
            if (colon <= 0 || eq <= colon + 1 || dots <= eq + 1 || dots + 2 >= item.Length)
            {
                error = $"--envelope item '{item}' is not <class>:<param>=<low>..<high> (for example embedded-dg:rocofTripHzPerS=0.5..1.0).";
                return null;
            }
            string cls = item[..colon].Trim();
            string param = item[(colon + 1)..eq].Trim();
            string lowText = item[(eq + 1)..dots].Trim(), highText = item[(dots + 2)..].Trim();
            if (!library.Contains(cls))
            {
                error = $"--envelope: asset class '{cls}' is not in this case's asset-classes.json (classes: "
                    + string.Join(", ", library.Classes.Select(k => k.Name).OrderBy(n => n, StringComparer.Ordinal)) + ").";
                return null;
            }
            AssetClass c = library.Get(cls);
            IReadOnlySet<string> accepted = AssetClassSupport.AcceptedKeys(c.Kind);
            if (!accepted.Contains(param))
            {
                error = $"--envelope: '{param}' is not a parameter a {c.Kind} class is simulated with (accepted: "
                    + string.Join(", ", accepted.OrderBy(key => key, StringComparer.Ordinal)) + ").";
                return null;
            }
            if (!double.TryParse(lowText, NumberStyles.Float, CultureInfo.InvariantCulture, out double low) || !double.IsFinite(low)
                || !double.TryParse(highText, NumberStyles.Float, CultureInfo.InvariantCulture, out double high) || !double.IsFinite(high))
            {
                error = $"--envelope item '{item}': the range must be two finite numbers, <low>..<high>.";
                return null;
            }
            if (!(low < high))
            {
                error = $"--envelope item '{item}': the low end must be below the high end.";
                return null;
            }
            // The class name as the library spells it (the lookup ignores case; the sweep's override matches it exactly).
            if (sweep.Any(s => string.Equals(s.ClassName, c.Name, StringComparison.Ordinal) && s.ParamName == param))
            {
                error = $"--envelope names {c.Name}:{param} twice.";
                return null;
            }
            sweep.Add(new AssetSweepParam(c.Name, param, low, high));
        }
        if (sweep.Count > MaxParameters)
        {
            error = $"--envelope names {sweep.Count} parameters; at most {MaxParameters} ({1 << MaxParameters} corners).";
            return null;
        }
        return sweep;
    }
}

/// <summary>
/// The machine dynamic data file (<c>gridsim-dynamics/2</c>, F23) on the dynamic verbs other than <c>stability --rms</c>.
/// Each engine takes the part of the file it models and no more; what it does not model is said, never silently dropped.
/// </summary>
internal static class DynDataCli
{
    /// <summary>
    /// A copy of <paramref name="model"/> in which every generator the file binds carries the file's inertia constant H
    /// (on its own MBase, the unit the case uses; the loader already put the case's own H in for a template that gives
    /// none). Every other property and generator is carried unchanged (<see cref="GridModel.CopyWith"/>), so a file whose
    /// H values equal the case's gives a model every study treats bit for bit like the original.
    /// </summary>
    public static GridModel WithFileInertia(GridModel model, MachineDynamicsSet dyn)
    {
        Generator[] gens = model.Generators.ToArray();
        foreach (MachineDynamicsEntry e in dyn.Entries)
            gens[e.GeneratorIndex] = gens[e.GeneratorIndex] with { H = e.Data.H };
        return model.CopyWith(generators: gens);
    }

    /// <summary>How many of the bound machines carry an H that differs from the case's (for the printed line).</summary>
    public static int InertiaChangedCount(GridModel model, MachineDynamicsSet dyn) =>
        dyn.Entries.Count(e => !e.Data.H.Equals(model.Generators[e.GeneratorIndex].H));

    /// <summary>
    /// The per-generator transient reactance X'd (machine base) for the classical small-signal model: the file's X'd for a
    /// bound machine, otherwise <paramref name="baseXdp"/> (the verb's own map) or, when that is null, the classical
    /// default <see cref="GridSim.Core.Dynamics.Distributed.ClassicalInit.DefaultTransientReactancePu"/> the assembler
    /// would have used. Aligned to <c>model.Generators</c>.
    /// </summary>
    public static double[] TransientReactance(GridModel model, double[]? baseXdp, MachineDynamicsSet dyn)
    {
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++)
            xdp[i] = dyn.For(i) is { } e ? e.Data.Xdp
                : baseXdp?[i] ?? GridSim.Core.Dynamics.Distributed.ClassicalInit.DefaultTransientReactancePu;
        return xdp;
    }

    /// <summary>
    /// The per-generator damping D for the small-signal model: the file's damping for a bound machine (the same quantity
    /// as the RMS study's: per unit on the system base, in <c>d omega / dt = (Pm - Pe - D omega) / 2H</c>, the form both
    /// engines integrate), otherwise <paramref name="baseDamping"/> (--damping) or 0. Aligned to <c>model.Generators</c>.
    /// </summary>
    public static double[] Damping(GridModel model, double[]? baseDamping, MachineDynamicsSet dyn)
    {
        var d = new double[model.Generators.Count];
        for (int i = 0; i < d.Length; i++)
            d[i] = dyn.For(i) is { } e ? e.Data.Damping : baseDamping?[i] ?? 0.0;
        return d;
    }
}
