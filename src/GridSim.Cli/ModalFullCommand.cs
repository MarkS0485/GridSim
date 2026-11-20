using System.Globalization;
using System.Numerics;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.Dynamics.SmallSignal.Full;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-12: <c>gridsim modal-full &lt;case&gt; --models &lt;dir&gt; [--region re,im,re,im] [--count N] ...</c>. Full-model small-signal
/// analysis (Core <c>GridSim.Core.Dynamics.SmallSignal.Full</c>): machine rotor angle, speed and E'q states, the network algebra
/// eliminated by the Kron reduction, and the exciter, governor and stabiliser declarative models linearised block by block.
/// The classical <c>modal</c> verb is unchanged. <c>--models</c> names a directory holding <c>machines.json</c>
/// (<c>gridsim-modal-machines/1</c>) and any <c>&lt;id&gt;.model.json</c> declarative model documents the bindings name (a model not
/// found there is looked up in the shipped library). Exit codes: 0 COMPLETE and no right-half-plane eigenvalue; 1 the case or the
/// bindings cannot be loaded or are invalid; 2 usage; 3 INCOMPLETE (a residual-gate failure, an unconverged Arnoldi run or a
/// controller that was not linearised) or at least one right-half-plane eigenvalue among those listed.
/// </summary>
internal static class ModalFullCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Allowed = { "--models", "--region", "--count", "--method", "--dense-limit", "--residual-gate" };

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        const string verb = "modal-full";
        try
        {
            string? pos = null;
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < a.Count; i++)
            {
                string t = a[i];
                if (!t.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pos is not null) throw new UsageException($"{verb} takes one case but got: {pos} {t}.");
                    pos = t; continue;
                }
                if (Array.IndexOf(Allowed, t) < 0) throw new UsageException($"{verb} does not use {t}. {verb} accepts: {string.Join(", ", Allowed)}.");
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
                if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
            }
            if (pos is null) throw new UsageException("usage: gridsim modal-full <case|case.m|case-dir> --models <dir> [--region re,im,re,im] [--count N] [--method auto|dense|arnoldi] [--dense-limit N] [--residual-gate x]");
            string modelsDir = flags.GetValueOrDefault("--models") ?? throw new UsageException("--models <dir> is required (a directory holding machines.json; nothing is defaulted)");

            ModalRegion? region = null;
            if (flags.TryGetValue("--region", out string? rt))
            {
                string[] parts = rt.Split(',');
                double[] v = parts.Select(s => double.TryParse(s, NumberStyles.Float, Inv, out double d) && double.IsFinite(d) ? d : double.NaN).ToArray();
                if (parts.Length != 4 || v.Any(double.IsNaN)) throw new UsageException($"--region expected re,im,re,im (two opposite corners, finite numbers) but got '{rt}'");
                region = ModalRegion.FromCorners(v[0], v[1], v[2], v[3]);
            }
            int count = Int(flags, "--count", 20, 1, 100000);
            int denseLimit = Int(flags, "--dense-limit", FullModalOptions.DefaultDenseOrderLimit, 1, 100000);
            double gate = 1e-9;
            if (flags.TryGetValue("--residual-gate", out string? gt))
            {
                if (!double.TryParse(gt, NumberStyles.Float, Inv, out gate) || !(gate > 0) || !double.IsFinite(gate))
                    throw new UsageException($"--residual-gate expected a finite number above 0 but got '{gt}'");
            }
            int force = (flags.GetValueOrDefault("--method") ?? "auto").ToLowerInvariant() switch
            {
                "auto" => 0, "dense" => 1, "arnoldi" => 2,
                var m => throw new UsageException($"--method expected auto, dense or arnoldi but got '{m}'"),
            };

            GridModel model;
            try { model = load(pos); }
            catch (Exception ex) when (ex is not GateRefusedException)
            {
                Console.Error.WriteLine($"error loading case '{pos}': {ex.Message}");
                return CliExit.CaseLoadFailure(ex);
            }

            if (!Directory.Exists(modelsDir)) { Console.Error.WriteLine($"error: {verb}: --models directory '{modelsDir}' does not exist."); return CliExit.InputError; }
            string bindPath = Path.Combine(modelsDir, "machines.json");
            if (!File.Exists(bindPath)) { Console.Error.WriteLine($"error: {verb}: {bindPath} does not exist (the {FullMachineBindings.Format} binding of machines to data and models)."); return CliExit.InputError; }

            FullMachineBindings bindings;
            try
            {
                bindings = FullMachineBindings.Parse(File.ReadAllBytes(bindPath), model, id => ResolveModel(modelsDir, id));
            }
            catch (FullBindingException ex) { Console.Error.WriteLine($"error: {verb}: {bindPath}: {ex.Message}"); return CliExit.InputError; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }

            PowerFlowResult pf = SolverBackend.Select(null).NewtonSolver(model).Solve(model);   // shared engine rule (CliEngineChoiceScan)
            if (!pf.Converged) { Console.Error.WriteLine($"error: {verb}: the power flow did not converge."); return CliExit.InputError; }

            FullSmallSignalModel fm;
            try { fm = FullModelAssembler.Assemble(model, pf, bindings.Specs); }
            catch (ArgumentException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }
            catch (InvalidOperationException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.InputError; }

            var options = new FullModalOptions(region, count, denseLimit, gate, force);
            FullModalResult r = FullModalAnalysis.Analyze(fm, options);
            return Report(verb, model, bindings, fm, r);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message.StartsWith("usage:", StringComparison.Ordinal) ? ex.Message : $"error: {verb}: {ex.Message}");
            return CliExit.Usage;
        }
    }

    private static int Int(Dictionary<string, string> flags, string name, int dflt, int min, int max)
    {
        if (!flags.TryGetValue(name, out string? v)) return dflt;
        if (!int.TryParse(v, NumberStyles.Integer, Inv, out int x) || x < min || x > max)
            throw new UsageException($"{name} expected a whole number from {min} to {max} but got '{v}'");
        return x;
    }

    private static byte[]? ResolveModel(string dir, string id)
    {
        if (id.Contains('/', StringComparison.Ordinal) || id.Contains('\\', StringComparison.Ordinal) || id.Contains("..", StringComparison.Ordinal)) return null;   // a name, never a path
        string file = Path.Combine(dir, id + ".model.json");
        if (File.Exists(file)) return File.ReadAllBytes(file);
        return UserModelLibrary.Ids.Contains(id, StringComparer.Ordinal) ? UserModelLibrary.Bytes(id) : null;
    }

    private static string R(double v) => double.IsNaN(v) ? "n/a" : v.ToString("R", Inv);

    private static int Report(string verb, GridModel model, FullMachineBindings bindings, FullSmallSignalModel fm, FullModalResult r)
    {
        bool incomplete = r.Status == FullModalStatus.Incomplete;
        Console.WriteLine($"GridSim  -  FULL-MODEL SMALL-SIGNAL / MODAL  *  {model.Name}");
        Console.WriteLine(new string('=', 66));
        Console.WriteLine($"{fm.Machines.Count} dynamic machine(s), {fm.Order} state(s)  *  method: {r.Method}  *  status: {(incomplete ? "INCOMPLETE" : "COMPLETE")}");
        Console.WriteLine($"machine data and controller models: {bindings.Specs.Count} machine binding(s), binding sha256:{bindings.Sha256}");
        foreach ((string id, string hash) in bindings.ModelHashes) Console.WriteLine($"  model {id} sha256:{hash}");
        Console.WriteLine($"coverage: {r.Coverage}");
        foreach (string reason in r.IncompleteReasons) Console.WriteLine($"INCOMPLETE: {reason}");
        foreach (string note in r.Notes) Console.WriteLine($"note: {note}");
        if (r.ParticipationUnavailable) Console.WriteLine($"participation: {r.ParticipationUnavailableReason}");
        Console.WriteLine();
        Console.WriteLine($"{"sigma (1/s)",14}{"omega (rad/s)",15}{"f (Hz)",10}{"zeta",10}  {"RHP",3}  {"residual",9}  mode / dominant states");
        int shown = 0;
        foreach (FullMode m in r.Modes)
        {
            if (shown++ >= 80) { Console.WriteLine($"  ... {r.Modes.Count - 80} more mode(s) in the result file."); break; }
            string kind = m.Mode.IsOscillatory ? "oscillatory" : "real";
            string top = m.ParticipationAvailable ? TopStates(m, r.States, 3) : "participation unavailable";
            Console.WriteLine($"{m.Mode.Eigenvalue.Real,14:F4}{m.Mode.Eigenvalue.Imaginary,15:F4}{m.Mode.FrequencyHz,10:F4}{m.Mode.DampingRatio,10:F4}  {(m.InRightHalfPlane ? "yes" : "no"),3}  {m.RelativeResidual,9:E1}  {kind}; {top}");
        }
        Console.WriteLine();
        FullMode? dominant = r.Modes.FirstOrDefault(m => m.Mode.IsOscillatory);
        if (dominant is not null)
            Console.WriteLine($"Least-damped oscillatory mode listed: {dominant.Mode.FrequencyHz:F3} Hz, zeta = {dominant.Mode.DampingRatio:F4}" + (dominant.Mode.DampingRatio < 0 ? "  (UNSTABLE)" : dominant.Mode.DampingRatio < 0.05 ? "  (poorly damped)" : ""));
        Console.WriteLine($"Right-half-plane eigenvalues listed: {r.RhpCount}");
        Console.WriteLine($"Minimum oscillatory damping ratio listed: {(double.IsNaN(r.MinDampingRatio) ? "n/a" : r.MinDampingRatio.ToString("F4", Inv))}");
        Console.WriteLine($"Largest relative residual ||A v - lambda v|| / ||A||_1: {r.MaxRelativeResidual:E2} (gate {r.ResidualGate:E1})");
        foreach (NotLinearisedController nl in r.NotLinearised)
            Console.WriteLine($"NOT LINEARISED: bus {nl.Bus} {nl.Controller}: {nl.Reason}");
        Console.WriteLine("Comparator: PowerFactory modal analysis comparison is COMPARATOR-UNVERIFIED; no parity with any paid tool is claimed.");

        CliResult? rep = CliOutput.Report;
        rep?.Set("case", model.Name).Set("method", r.Method).Set("status", incomplete ? "INCOMPLETE" : "COMPLETE").Set("states", fm.Order)
            .Set("machines", fm.Machines.Count).Set("rhp_count", r.RhpCount).Set("min_damping_ratio", double.IsNaN(r.MinDampingRatio) ? null : r.MinDampingRatio)
            .Set("max_relative_residual", r.MaxRelativeResidual).Set("residual_gate", r.ResidualGate).Set("partial_spectrum", r.PartialSpectrum)
            .Set("coverage", r.Coverage).Set("restarts", r.Restarts).Set("matvecs", r.MatVecs).Set("krylov_dimension", r.KrylovDimension)
            .Set("start_seed", r.StartSeed.ToString(Inv)).Set("eigenvalue_digest", r.EigenvalueDigest).Set("machines_sha256", bindings.Sha256)
            .Set("incomplete_reasons", string.Join(" | ", r.IncompleteReasons)).Set("comparator", "COMPARATOR-UNVERIFIED");
        CliResultTable? modes = rep?.Table("modes", "mode", "sigma", "omega_rad_s", "frequency_hz", "damping_ratio", "oscillatory", "right_half_plane",
            "relative_residual", "residual_passed", "participation_available", "machine_angle", "machine_speed", "machine_flux", "exciter", "governor", "stabiliser", "dominant_states");
        CliResultTable? part = rep?.Table("participation", "mode", "state", "category", "participation");
        CliResultTable? models = rep?.Table("models", "model", "sha256");
        foreach ((string id, string hash) in bindings.ModelHashes) models?.Row(id, hash);
        CliResultTable? notLin = rep?.Table("not_linearised", "bus", "controller", "reason");
        foreach (NotLinearisedController nl in r.NotLinearised) notLin?.Row(nl.Bus, nl.Controller, nl.Reason);
        int mi = 0;
        foreach (FullMode m in r.Modes)
        {
            double[] c = m.ParticipationByCategory.ToArray();
            modes?.Row(mi, m.Mode.Eigenvalue.Real, m.Mode.Eigenvalue.Imaginary, m.Mode.FrequencyHz, m.Mode.DampingRatio, m.Mode.IsOscillatory, m.InRightHalfPlane,
                m.RelativeResidual, m.ResidualPassed, m.ParticipationAvailable, c[0], c[1], c[2], c[3], c[4], c[5], m.ParticipationAvailable ? TopStates(m, r.States, 3) : "");
            if (m.ParticipationAvailable)
                for (int k = 0; k < r.States.Count; k++)
                    if (m.Mode.ParticipationByState[k] > 1e-9) part?.Row(mi, r.States[k].Name, r.States[k].Category.ToString(), m.Mode.ParticipationByState[k]);
            mi++;
        }
        rep?.Complete();
        return incomplete || r.RhpCount > 0 ? CliExit.Verdict : CliExit.Ok;
    }

    private static string TopStates(FullMode m, IReadOnlyList<FullStateLabel> states, int take)
        => string.Join(", ", Enumerable.Range(0, states.Count).OrderByDescending(k => m.Mode.ParticipationByState[k]).ThenBy(k => k).Take(take)
            .Where(k => m.Mode.ParticipationByState[k] > 1e-6).Select(k => $"{states[k].Name} {100.0 * m.Mode.ParticipationByState[k]:F1}%"));
}
