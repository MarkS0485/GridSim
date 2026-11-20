using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-05: the Python <c>Session</c> (<c>python/gridsim/_session.py</c>) speaks the protocol the CLI's <c>rpc</c> verb
/// speaks. Additive to <see cref="PythonApiContractTests"/>, which pins the writer-verb list and is untouched: the protocol
/// label and every operation name the Python source sends are read out of it and compared with src/GridSim.Cli/RpcCommand.cs.
/// The session's behaviour is tested by <c>python/tests/test_session.py</c>.
/// </summary>
public class R405PythonSessionContractTests
{
    private static string RpcSource() => File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "RpcCommand.cs"));

    private static string Source() => File.ReadAllText(Path.Combine(CliProcess.Root(), "python", "gridsim", "_session.py"));

    [Fact]
    public void The_python_session_speaks_the_protocol_the_cli_serves()
    {
        Match m = Regex.Match(Source(), "^PROTOCOL = \"(?<p>[^\"]+)\"", RegexOptions.Multiline);
        Assert.True(m.Success, "PROTOCOL = \"...\" not found in python/gridsim/_session.py");
        Match cli = Regex.Match(RpcSource(), "public const string Protocol = \"(?<p>[^\"]+)\";");
        Assert.True(cli.Success, "Protocol constant not found in RpcCommand.cs");
        Assert.Equal(cli.Groups["p"].Value, m.Groups["p"].Value);
    }

    [Fact]
    public void Every_operation_the_python_session_sends_is_one_the_cli_serves()
    {
        string source = Source();
        var sent = Regex.Matches(source, "self\\.request\\(\"(?<op>[a-z-]+)\"").Select(x => x.Groups["op"].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(sent);
        Match ops = Regex.Match(RpcSource(), @"Operations = \[(?<body>[^\]]*)\];");
        Assert.True(ops.Success, "Operations array not found in RpcCommand.cs");
        string[] served = Regex.Matches(ops.Groups["body"].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToArray();
        foreach (string op in sent) Assert.Contains(op, served);
        // The operations the package wraps by method; shutdown is sent by close() as a literal line.
        Assert.Equal(new[] { "hello", "line-constants", "load", "release", "run-verb", "solve", "stats" }, sent);
        Assert.Contains("\"op\":\"shutdown\"", source, StringComparison.Ordinal);
    }
}
