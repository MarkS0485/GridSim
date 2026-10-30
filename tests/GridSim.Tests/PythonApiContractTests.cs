using System.Text.RegularExpressions;
using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): the Python package (<c>python/gridsim</c>) wraps exactly the verbs the CLI writes a result
/// document for. Its <c>WRITER_VERBS</c> tuple and <c>ALIASES</c> dict are read out of the Python source and compared with
/// <see cref="CliOutput.WriterVerbs"/> and <see cref="CliOutput.Canonical"/>, so a verb added to one side only, or an alias
/// that resolves differently, fails here. The package's own behaviour is tested by its unittest suite
/// (<c>python -m unittest discover -s python/tests</c>).
/// </summary>
public class PythonApiContractTests
{
    private static string Source() => File.ReadAllText(Path.Combine(CliProcess.Root(), "python", "gridsim", "_cli.py"));

    [Fact]
    public void The_python_package_wraps_exactly_the_clis_writer_verbs()
    {
        Match m = Regex.Match(Source(), @"^WRITER_VERBS = \((?<body>.*?)^\)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, "WRITER_VERBS = ( ... ) not found in python/gridsim/_cli.py");
        string[] python = Regex.Matches(m.Groups["body"].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToArray();
        string[] cli = CliOutput.WriterVerbs.Select(CliOutput.Canonical).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(cli, python.Order(StringComparer.Ordinal));
        Assert.Equal(python.Length, python.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_python_aliases_resolve_as_the_cli_resolves_them()
    {
        Match m = Regex.Match(Source(), @"^ALIASES = \{(?<body>[^}]*)\}", RegexOptions.Multiline);
        Assert.True(m.Success, "ALIASES = { ... } not found in python/gridsim/_cli.py");
        var pairs = Regex.Matches(m.Groups["body"].Value, "\"([^\"]+)\":\\s*\"([^\"]+)\"").Select(x => (Alias: x.Groups[1].Value, Name: x.Groups[2].Value)).ToArray();
        string[] cliAliases = CliOutput.WriterVerbs.Where(v => !string.Equals(CliOutput.Canonical(v), v, StringComparison.OrdinalIgnoreCase)).Select(v => v.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(cliAliases, pairs.Select(p => p.Alias).Order(StringComparer.Ordinal));
        foreach ((string alias, string name) in pairs) Assert.Equal(CliOutput.Canonical(alias), name);
    }
}
