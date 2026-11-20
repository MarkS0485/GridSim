using System.Globalization;
using GridSim.Core.Dynamics.UserModels;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-08: <c>gridsim user-model list</c> and <c>gridsim user-model check &lt;model.json | LIBRARY-ID&gt;</c>. Reads a
/// <c>gridsim-model/1</c> declarative dynamic-model document (Core <c>GridSim.Core.Dynamics.UserModels</c>), validates it
/// against the closed block vocabulary, compiles it (deterministic order, algebraic-loop check) and prints what a model
/// coverage report records for it: id, role, source, document SHA-256, compiled-order digest, the status line
/// <c>SUPPORTED (USER MODEL, sha256:...)</c>, the parameters with units and bounds, and the blocks in evaluation order.
/// It runs no study and takes no flags. Exit codes: 0 valid; 1 the file is missing, not a valid document, or refused (every
/// refusal is printed, naming the object); 2 usage.
/// </summary>
internal static class UserModelCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(List<string> a)
    {
        if (a.Count < 2) return Usage("usage: gridsim user-model list | check <model.json | LIBRARY-ID> (see 'gridsim user-model --help')");
        string sub = a[1];
        if (a.Skip(2).Any(t => t.StartsWith("--", StringComparison.Ordinal))) return Usage("user-model takes no flags.");
        if (sub.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (a.Count != 2) return Usage("usage: gridsim user-model list");
            foreach (string id in UserModelLibrary.Ids)
            {
                ModelDocument d = UserModelLibrary.Document(id);
                Console.WriteLine($"{d.Id,-12} {d.Role,-10} sha256:{d.Sha256}  {d.Title}");
            }
            return CliExit.Ok;
        }
        if (!sub.Equals("check", StringComparison.OrdinalIgnoreCase))
            return Usage($"unknown user-model subcommand '{sub}'. Use: list, check.");
        if (a.Count != 3) return Usage("usage: gridsim user-model check <model.json | LIBRARY-ID>");
        string arg = a[2];

        ModelDocument doc;
        CompiledModel plan;
        try
        {
            byte[] bytes;
            if (File.Exists(arg)) bytes = File.ReadAllBytes(arg);
            else if (UserModelLibrary.Ids.Contains(arg, StringComparer.Ordinal)) bytes = UserModelLibrary.Bytes(arg);
            else
            {
                Console.Error.WriteLine($"error: user-model: '{arg}' is not a file and not a shipped model (shipped: {string.Join(", ", UserModelLibrary.Ids)}).");
                return CliExit.InputError;
            }
            doc = ModelDocument.Parse(bytes);
            plan = ModelCompiler.Compile(doc);
            _ = ModelInstance.Create(plan);   // the document's own parameter values must be valid
        }
        catch (UserModelException ex)
        {
            Console.Error.WriteLine($"error: user-model: {arg} was refused ({ex.Issues.Count} issue(s)):");
            foreach (ModelIssue i in ex.Issues) Console.Error.WriteLine($"  [{i.Code}] {i.Object}: {i.Message}");
            return CliExit.InputError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: user-model: {arg}: {ex.Message}");
            return CliExit.InputError;
        }

        Console.WriteLine($"User model {doc.Id} ({doc.Role}): {doc.Title}");
        Console.WriteLine($"  format:          {ModelFormat.Id}");
        Console.WriteLine($"  source:          {doc.Source}");
        Console.WriteLine($"  document sha256: {doc.Sha256}");
        Console.WriteLine($"  order digest:    {plan.OrderDigest}");
        Console.WriteLine($"  status:          SUPPORTED (USER MODEL, sha256:{doc.Sha256})");
        Console.WriteLine($"  inputs:          {string.Join(", ", doc.Inputs.Select(p => $"{p.Name} [{p.UnitText}]"))}");
        Console.WriteLine($"  outputs:         {string.Join(", ", doc.Outputs.Select(p => $"{p.Name} [{p.UnitText}] <- {p.From}"))}");
        Console.WriteLine("  parameters:");
        foreach (ModelParameter p in doc.Parameters)
            Console.WriteLine($"    {p.Name,-8} = {p.Value.ToString("R", Inv),-10} {p.UnitText,-4} {Bounds(p)}");
        foreach (ModelDerived d in doc.Derived)
            Console.WriteLine($"    {d.Name,-8} = {d.A} {d.Op} {d.B} (derived)");
        Console.WriteLine("  blocks in evaluation order:");
        int n = 1;
        foreach (string id in plan.Order)
        {
            ModelBlock b = doc.Blocks.First(x => x.Id == id);
            Console.WriteLine($"    {n++,2}. {b.Type,-10} {b.Id}  <- {string.Join(", ", b.Inputs.Select(r => r.ToString()))}");
        }
        return CliExit.Ok;
    }

    private static string Bounds(ModelParameter p)
    {
        var parts = new List<string>();
        if (p.Min is double lo) parts.Add((p.MinExclusive ? "> " : ">= ") + lo.ToString("R", Inv));
        if (p.Max is double hi) parts.Add((p.MaxExclusive ? "< " : "<= ") + hi.ToString("R", Inv));
        if (p.AllowInfinite) parts.Add("infinite allowed");
        return parts.Count == 0 ? "" : "(" + string.Join(", ", parts) + ")";
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message.StartsWith("usage:", StringComparison.Ordinal) ? message : "error: user-model: " + message);
        return CliExit.Usage;
    }
}
