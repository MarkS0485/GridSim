using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N081 (October 2026): `gda --fleet` reads the NESO registers as CSVs under NESO/RAWData; the full GDA lake holds
/// them only as Parquet (NESO/Parquet/&lt;dataset&gt;). The lookup is unchanged, but when the CSV folder is missing
/// and the Parquet dataset is present the error now says so (a ParquetOnlyDatasetException, a DirectoryNotFoundException
/// subtype, since GR-795); a lake with the CSVs resolves as
/// before; a lake with neither keeps the old message.
/// </summary>
public sealed class N081GdaPathsParquetOnlyRegisterTests
{
    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n081-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Parquet_only_register_is_named_in_the_error()
    {
        string root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "NESO", "Parquet", "transmission_entry_capacity_tec_register"));
            var paths = new GdaPaths(root);
            // Test changed 2026-10-19: since 1.95.403 (GR-795) the Parquet-only case throws ParquetOnlyDatasetException
            // (still a DirectoryNotFoundException, so callers that catch the base type are unchanged), and Has no longer
            // degrades to false on it: a Parquet-only lake is a fault the user must see, not an absent dataset.
            var ex = Assert.Throws<ParquetOnlyDatasetException>(() => paths.TecRegister);
            Assert.IsAssignableFrom<DirectoryNotFoundException>(ex);
            Assert.StartsWith("GDA dataset folder not found: " + Path.Combine(root, "NESO", "RAWData", "Transmission Entry Capacity (TEC) register"),
                ex.Message, StringComparison.Ordinal);
            Assert.Contains("only as Parquet", ex.Message, StringComparison.Ordinal);
            Assert.Contains("does not read the Parquet conversion", ex.Message, StringComparison.Ordinal);
            Assert.Throws<ParquetOnlyDatasetException>(() => paths.Has(p => p.TecRegister));   // GR-795: propagates
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Neither_csv_nor_parquet_keeps_the_old_message()
    {
        string root = NewRoot();
        try
        {
            var paths = new GdaPaths(root);
            var ex = Assert.Throws<DirectoryNotFoundException>(() => paths.InterconnectorRegister);
            Assert.Equal("GDA dataset folder not found: " + Path.Combine(root, "NESO", "RAWData", "Interconnector Register"), ex.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Csv_register_still_resolves_to_the_newest_file()
    {
        string root = NewRoot();
        try
        {
            string dir = Path.Combine(root, "NESO", "RAWData", "Embedded Register");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(root, "NESO", "Parquet", "embedded_register"));   // ignored when CSVs exist
            File.WriteAllText(Path.Combine(dir, "embedded-register-05-may-2026.csv"), "stub");
            File.WriteAllText(Path.Combine(dir, "embedded-register-12-january-2027.csv"), "stub");
            Assert.Equal("embedded-register-12-january-2027.csv", Path.GetFileName(new GdaPaths(root).EmbeddedRegister));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
