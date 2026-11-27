using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Analysis.VoltageStability;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP25: <c>gridsim qv-modal &lt;case&gt; [--modes N] [--out &lt;file.json&gt;]</c>. QV modal analysis
/// (<see cref="QvModalAnalysis"/>) at the case's Newton power flow (the shared engine rule, 100 iterations, reactive limits
/// on, as <c>pf</c>). Prints the smallest |eigenvalue| modes of the reduced Jacobian with the ten buses that participate
/// most in each. <c>--out</c> writes the result as JSON (<c>gridsim-qv-modal/1</c>); an existing file is never
/// overwritten. Not on the shared <c>--json</c>/<c>--csv</c> writer. Exit 0 when every reported eigenvalue has a positive
/// real part (a complex pair is judged on its real part: J_R is not symmetric, so a stable state can have one); 3 when the
/// power flow does not converge or a reported eigenvalue has a real part of 0 or below; 1 for a case that cannot be loaded
/// or analysed (including a case with HVDC, FACTS, SVC or induction-machine devices); 2 for usage.
/// </summary>
internal static class QvModalCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Usage = "usage: gridsim qv-modal <case> [--modes 5] [--out <file.json>]";

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        string? casePath = null, outPath = null;
        int modes = QvModalAnalysis.DefaultModes;
        bool modesGiven = false;
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (t is "--out" or "--modes")
            {
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"error: qv-modal: {t} expected a value. " + Usage);
                    return CliExit.Usage;
                }
                string v = a[++i];
                if (t == "--out")
                {
                    if (outPath is not null) { Console.Error.WriteLine("error: qv-modal: --out was given twice. " + Usage); return CliExit.Usage; }
                    outPath = v;
                }
                else
                {
                    if (modesGiven) { Console.Error.WriteLine("error: qv-modal: --modes was given twice. " + Usage); return CliExit.Usage; }
                    modesGiven = true;
                    if (!int.TryParse(v, NumberStyles.Integer, Inv, out modes) || modes < 1)
                    {
                        Console.Error.WriteLine($"error: qv-modal: --modes expected a whole number of 1 or more but got '{v}'. " + Usage);
                        return CliExit.Usage;
                    }
                }
                continue;
            }
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"error: qv-modal does not use {t}; it accepts --modes and --out. " + Usage);
                return CliExit.Usage;
            }
            if (casePath is not null)
            {
                Console.Error.WriteLine($"error: qv-modal takes one case but got a second argument: {t}. " + Usage);
                return CliExit.Usage;
            }
            casePath = t;
        }
        if (casePath is null)
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        string? output = outPath is null ? null : Path.GetFullPath(outPath);
        if (output is not null)
        {
            CliFiles.ReplayGuard(output, "qv-modal result");
            if (File.Exists(output) || Directory.Exists(output))
            {
                Console.Error.WriteLine($"error: qv-modal: '{output}' already exists; name a new file (it is never overwritten).");
                return CliExit.Usage;
            }
        }

        GridModel model;
        try { model = load(casePath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("error: qv-modal: " + ex.Message);
            return CliExit.InputError;
        }

        PowerFlowResult pf;
        QvModalResult r;
        try
        {
            pf = SolverSelector.CreateNewton(model).Solve(model, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
            if (!pf.Converged)
            {
                Console.WriteLine($"QV modal analysis - {model.Name} ({model.BusCount.ToString(Inv)} buses)");
                Console.WriteLine($"  power flow NOT CONVERGED ({pf.Status}): no state to analyse");
                return CliExit.Verdict;
            }
            r = QvModalAnalysis.Analyse(model, pf, modes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine("error: qv-modal: " + ex.Message);
            return CliExit.InputError;
        }

        Console.WriteLine($"QV modal analysis - {model.Name} ({model.BusCount.ToString(Inv)} buses, {r.PqBusIds.Count.ToString(Inv)} PQ buses, {r.TotalModes.ToString(Inv)} modes)");
        Console.WriteLine("  eigenvalues of J_R = J_QV - J_Qtheta J_Ptheta^-1 J_PV at the Newton power flow; real part > 0 stable, < 0 unstable, near 0 close to the limit");
        bool allStable = true;
        foreach (QvMode m in r.Modes)
        {
            bool stable = m.Eigenvalue.Real > 0.0;
            allStable &= stable;
            Console.WriteLine($"  mode {m.Rank.ToString(Inv)}: lambda {Lambda(m.Eigenvalue)}{(stable ? "" : "  NOT STABLE")}");
            foreach (QvModeBus b in m.TopBuses)
                Console.WriteLine($"      bus {b.BusId.ToString(Inv),-6} participation {b.Participation.ToString("F6", Inv)}");
        }

        if (output is not null)
        {
            try
            {
                using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("schema", "gridsim-qv-modal/1");
                w.WriteString("case", model.Name);
                w.WriteNumber("buses", model.BusCount);
                w.WriteNumber("totalModes", r.TotalModes);
                w.WriteStartArray("pqBuses");
                foreach (int id in r.PqBusIds) w.WriteNumberValue(id);
                w.WriteEndArray();
                w.WriteStartArray("modes");
                foreach (QvMode m in r.Modes)
                {
                    w.WriteStartObject();
                    w.WriteNumber("rank", m.Rank);
                    Wp27CliSupport.Number(w, "lambdaRe", m.Eigenvalue.Real);
                    Wp27CliSupport.Number(w, "lambdaIm", m.Eigenvalue.Imaginary);
                    w.WriteStartArray("participation");
                    for (int k = 0; k < r.PqBusIds.Count; k++)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("bus", r.PqBusIds[k]);
                        Wp27CliSupport.Number(w, "re", m.Participation[k].Real);
                        Wp27CliSupport.Number(w, "im", m.Participation[k].Imaginary);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("error: qv-modal: cannot write the result: " + ex.Message);
                return CliExit.InputError;
            }
            Console.WriteLine($"  wrote {output}");
        }
        return allStable ? CliExit.Ok : CliExit.Verdict;
    }

    private static string Lambda(Complex z) => z.Imaginary == 0.0
        ? z.Real.ToString("R", Inv)
        : z.Real.ToString("R", Inv) + (z.Imaginary < 0 ? " - j" : " + j") + Math.Abs(z.Imaginary).ToString("R", Inv);
}
