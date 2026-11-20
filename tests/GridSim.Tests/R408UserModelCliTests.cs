using GridSim.Core.Dynamics.UserModels;
using Xunit;

namespace GridSim.Tests;

/// <summary>R4-08: <c>gridsim user-model</c> as the built CLI: list the library, check a shipped id or a document file, refuse a bad one naming the object.</summary>
public sealed class R408UserModelCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-r408-" + Guid.NewGuid().ToString("N"));

    public R408UserModelCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void List_prints_every_shipped_model_with_its_hash()
    {
        CliProcess.Result r = CliProcess.RunBuilt("user-model", "list");
        Assert.Equal(0, r.ExitCode);
        foreach (string id in UserModelLibrary.Ids)
        {
            Assert.Contains(id, r.StdOut, StringComparison.Ordinal);
            Assert.Contains("sha256:" + UserModelLibrary.Document(id).Sha256, r.StdOut, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Check_of_a_shipped_id_prints_the_provenance_and_the_compiled_order()
    {
        CliProcess.Result r = CliProcess.RunBuilt("user-model", "check", "TGOV1");
        Assert.Equal(0, r.ExitCode);
        ModelDocument d = UserModelLibrary.Document("TGOV1");
        Assert.Contains($"document sha256: {d.Sha256}", r.StdOut, StringComparison.Ordinal);
        Assert.Contains($"SUPPORTED (USER MODEL, sha256:{d.Sha256})", r.StdOut, StringComparison.Ordinal);
        Assert.Contains($"order digest:    {ModelCompiler.Compile(d).OrderDigest}", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("lag", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_of_a_file_with_an_algebraic_loop_exits_1_and_names_the_blocks()
    {
        string path = Path.Combine(_dir, "loop.json");
        File.WriteAllText(path, """
            { "format": "gridsim-model/1", "id": "LOOP", "role": "generic", "source": "test",
              "ports": { "inputs": [ { "name": "u", "unit": "pu" } ], "outputs": [ { "name": "y", "unit": "pu", "from": "b" } ] },
              "blocks": [ { "id": "a", "type": "sum", "inputs": ["u", "b"], "signs": "++" },
                          { "id": "b", "type": "gain", "inputs": ["a"], "params": { "k": 0.5 } } ],
              "initialisation": { "inputs": [ { "name": "u0", "unit": "pu" } ], "steps": [] } }
            """);
        CliProcess.Result r = CliProcess.RunBuilt("user-model", "check", path);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("[algebraic-loop]", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("a -> b -> a", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_errors_exit_2_and_a_missing_model_exits_1()
    {
        Assert.Equal(2, CliProcess.RunBuilt("user-model").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("user-model", "frobnicate").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("user-model", "check").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("user-model", "check", "TGOV1", "--json", "x.json").ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("user-model", "check", "NOSUCHMODEL").ExitCode);
    }
}
