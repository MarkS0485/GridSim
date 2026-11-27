using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>
/// RC5 WP15: scripts that use none of the new STUDY verbs (STUDY RMS, STUDY SCOPF, STUDY CCT) give the run record they gave
/// before WP15. The SHA-256 values were first taken from the 2.1.108 build (dev main 4282db51) before any WP15 source edit:
/// this file was added and run against the unchanged GridSim.Scripting assembly, and the values it printed were pinned. The
/// case path is replaced by a placeholder and gridsimVersion by 2.1.108 before hashing.
/// <para>
/// Re-pinned in 2.1.130, with no change of code or record. The first normalisation missed two machine-specific parts, so
/// the pins held only in the folder they were taken in and failed on any other checkout (first seen on a second machine at
/// 2.1.128). The script names the case path, so (1) the LOAD CASE statement's text kept the path, written with the
/// script's own escaping (four backslashes per separator in the JSON), and (2) scriptSha256 and canonicalScriptSha256 hash
/// that text. Now the path is replaced in all three spellings; scriptSha256 is checked against the SHA-256 of the exact
/// bytes run and then replaced by a placeholder; canonicalScriptSha256 (the canonical form is internal to the runner) is
/// replaced by a placeholder. Every other byte is hashed as written. The new pins were taken with this normalisation from
/// the 2.1.126 build's assemblies (before 2.1.127), run from two different case folders, which gave the same values; the second machine's 2.1.128 records, normalised the same way, give them too.
/// </para>
/// </summary>
public sealed class Rc5Wp15ScriptPinTests
{
    private static string Normalised(RunResult r, byte[] script)
    {
        string dir = Path.GetDirectoryName(TestPaths.Case("case9"))!;
        string json = r.RecordJson;
        // The statement text holds the path as the script writes it (two backslashes per separator), JSON-escaped again
        // (four); an input's path is JSON-escaped once (two). Replace the longest spelling first, then the others.
        json = json.Replace(dir.Replace(@"\", @"\\\\", StringComparison.Ordinal), "<lib>", StringComparison.Ordinal)
                   .Replace(dir.Replace(@"\", @"\\", StringComparison.Ordinal), "<lib>", StringComparison.Ordinal)
                   .Replace(dir, "<lib>", StringComparison.Ordinal);
        // On a system whose path separator is not a backslash the record shows "<lib>/case9.m" where the pins (taken on
        // Windows) have the backslashes the JSON needs: four in the statement text (the script's own escaping, escaped
        // again), two in an input's path. Put those back, so only the separator is normalised and every other byte is
        // still compared as written.
        if (Path.DirectorySeparatorChar != '\\')
        {
            json = json.Replace("LOAD CASE \\\"<lib>/", "LOAD CASE \\\"<lib>" + new string('\\', 4), StringComparison.Ordinal)
                       .Replace("<lib>/", "<lib>" + new string('\\', 2), StringComparison.Ordinal);
        }
        // The script hashes cover the path too: scriptSha256 must be the hash of the bytes run; then both are placeholders.
        Match scriptSha = Regex.Match(json, "\"scriptSha256\": \"([0-9a-f]{64})\"");
        Assert.True(scriptSha.Success && scriptSha.Groups[1].Value == Sha(script), "scriptSha256 is not the SHA-256 of the script run");
        json = Regex.Replace(json, "\"scriptSha256\": \"[0-9a-f]{64}\"", "\"scriptSha256\": \"<script>\"");
        json = Regex.Replace(json, "\"canonicalScriptSha256\": \"[0-9a-f]{64}\"", "\"canonicalScriptSha256\": \"<canonical>\"");
        // The record names the build's version, which moves with every version by design; it is set to the version the
        // pins were first taken from.
        return Regex.Replace(json, "\"gridsimVersion\": \"[^\"]*\"", "\"gridsimVersion\": \"2.1.108\"");
    }

    private static string Sha(string text) => Sha(new UTF8Encoding(false).GetBytes(text));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Script(params string[] lines) => new UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n");

    private static RunResult RunScript(byte[] script) => Runner.Run(script, new RunOptions(TestPaths.TempDir()));

    /// <summary>Re-pinned in 2.1.130 (see the class remarks), and again in 2.1.155: B36 (2.1.152) changed the default Q-limit pass cap
    /// from 12 to 100, which the record reports once as maxQLimitPasses. Proof: the 2.1.155 normalised record with that one value put back
    /// to 12 hashes to the previous pin, byte for byte, so that value is the only difference and no numerical result changed.</summary>
    private const string PlainPin = "954ff805a6c6df01a4159c961d33d41061fb5996931fd88c3bf6a8ca0b73db72";

    /// <summary>Re-pinned in 2.1.130 (see the class remarks), and again in 2.1.155: B36 (2.1.152) changed the default Q-limit pass cap
    /// from 12 to 100, which the record reports once as maxQLimitPasses. Proof: the 2.1.155 normalised record with that one value put back
    /// to 12 hashes to the previous pin, byte for byte, so that value is the only difference and no numerical result changed.</summary>
    private const string StudiesPin = "f40a54903eb72b1c1739e4a579f6709f07fd8cf886eb3ec61b11b40c1a4db48e";

    [Fact]
    public void A_grammar_1_script_records_what_it_recorded_before_wp15()
    {
        byte[] script = Script("GRAMMAR 1", TestPaths.Load("case9"), "SOLVE POWERFLOW", "SET LOAD \"L5\" P 100 MW", "TRIP LINE \"4-5\"",
            "SOLVE POWERFLOW", "REPORT BUSES");
        RunResult r = RunScript(script);
        Assert.True(r.Ok, r.Error?.Message);
        string n = Normalised(r, script);
        Assert.True(Sha(n) == PlainPin, "sha " + Sha(n) + "\n" + n);
    }

    [Fact]
    public void A_studies_script_without_the_new_verbs_records_what_it_recorded_before_wp15()
    {
        byte[] script = Script("GRAMMAR 1 WITH STUDIES", TestPaths.Load("case9"), "STUDY CONTINGENCY AS a", "STUDY SHORTCIRCUIT BUS \"5\" AS sc",
            "STUDY OPF AS o", "LET x = OBJECTIVE OF STUDY o", "PRINT x", "REPORT STUDY a", "REPORT STUDY sc", "REPORT STUDY o");
        RunResult r = RunScript(script);
        Assert.True(r.Ok, r.Error?.Message);
        string n = Normalised(r, script);
        Assert.True(Sha(n) == StudiesPin, "sha " + Sha(n) + "\n" + n);
    }
}
