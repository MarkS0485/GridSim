using System.Security.Cryptography;
using System.Text;
using GridSim.Core.IO.Psse;

namespace GridSim.Core.IO.Psse.Topology;

/// <summary>A hash-bound, parsed PSS/E v35 case with its native substation graph and terminal identity report.</summary>
public sealed record PsseSubstationCase(string SourceSha256, string SourcePath, PsseParseResult Parsed,
    PsseSubstationTopology Topology, IReadOnlyList<PsseTerminalBinding> TerminalBindings,
    IReadOnlyList<PsseTerminalSetAudit> TerminalSetAudits)
{
    /// <summary>The decoded source text whose bytes produced <see cref="SourceSha256"/>. The topology model builder and the
    /// topology-aware writer work from this text so they never re-read a file that may have changed on disk.</summary>
    public string SourceText { get; init; } = "";
}

/// <summary>Explicit file entry point for the additive node-breaker workflow.</summary>
public static class PsseSubstationCaseReader
{
    public static PsseSubstationCase Read(string path, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        string expected = expectedSha256.Trim();
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.", nameof(expectedSha256));

        string fullPath = Path.GetFullPath(path);
        byte[] bytes = GridSim.Core.IO.InputFileLimits.ReadAllBytes(fullPath);
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"PSS/E source SHA-256 mismatch. Expected {expected.ToLowerInvariant()}, received {actual}.");

        // Match PsseRawParser.ParseFile decoding: UTF-8 by default with BOM detection for UTF-8/16/32.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        PsseParseResult parsed = PsseRawParser.ParseWithDiagnostics(text);
        if (parsed.Revision != 35)
            throw new InvalidDataException($"The topology case reader requires PSS/E revision 35; the parsed revision was {parsed.Revision}.");
        PsseSubstationTopology topology = PsseSubstationTopologyReader.Read(parsed);
        IReadOnlyList<PsseTerminalBinding> bindings = PsseTerminalIdentityBinder.Bind(topology, parsed);
        IReadOnlyList<PsseTerminalSetAudit> terminalSets = PsseTerminalSetAuditor.Audit(topology, parsed, bindings);
        return new PsseSubstationCase(actual, fullPath, parsed, topology, bindings, terminalSets) { SourceText = text };
    }
}
