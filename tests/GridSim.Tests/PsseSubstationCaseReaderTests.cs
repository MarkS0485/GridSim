using System.Security.Cryptography;
using GridSim.Core.IO.Psse.Topology;
using Xunit;

namespace GridSim.Tests;

public sealed class PsseSubstationCaseReaderTests
{
    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse", "sample-v35.raw"));

    [Fact]
    public void Reads_topology_only_when_source_bytes_match_the_expected_hash()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(Fixture)
            .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal));
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));

            PsseSubstationCase result = PsseSubstationCaseReader.Read(path, hash);

            Assert.Equal(hash, result.SourceSha256, ignoreCase: true);
            Assert.Equal(35, result.Topology.Revision);
            Assert.Single(result.Topology.Substations);
            Assert.Equal(result.Topology.Terminals.Count, result.TerminalBindings.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_a_source_hash_mismatch_before_parsing()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a RAW case", System.Text.Encoding.UTF8);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                PsseSubstationCaseReader.Read(path, new string('0', 64)));
            Assert.Contains("SHA-256 mismatch", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Detects_a_supported_unicode_bom_like_the_legacy_file_reader()
    {
        string source = File.ReadAllText(Fixture)
            .Replace("'CB 1', 2, 1, 1, 0.0,", "'CB 1', 2, 1, 1, 0.0001,", StringComparison.Ordinal);
        var encoding = new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));

            PsseSubstationCase result = PsseSubstationCaseReader.Read(path, hash);

            Assert.Equal(35, result.Topology.Revision);
        }
        finally { File.Delete(path); }
    }
}
