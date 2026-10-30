using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug #62 (2.0.178): the GDA lake's NESO acquire step writes the published register CSVs under
/// NESO/Processors/RAWData/&lt;register&gt;, and the live lake has no NESO/RAWData at all, so `gda --fleet` never found
/// the TEC, interconnector, embedded or constraint-limit registers. The reader now falls back to the acquire tree when
/// NESO/RAWData has no folder for the register. NESO/RAWData still wins when it exists, and with neither present the
/// error is the old one (pinned by N081GdaPathsParquetOnlyRegisterTests, unchanged).
/// </summary>
public sealed class Bug62GdaPathsAcquireTreeTests
{
    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-bug62-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Register_in_the_acquire_tree_is_found_when_neso_rawdata_is_absent()
    {
        string root = NewRoot();
        try
        {
            string dir = Path.Combine(root, "NESO", "Processors", "RAWData", "Transmission Entry Capacity (TEC) register");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "tec-register-24-september-2026.csv"), "stub");
            File.WriteAllText(Path.Combine(dir, "tec-register-28-september-2026.csv"), "stub");
            var paths = new GdaPaths(root);
            Assert.Equal(Path.Combine(dir, "tec-register-28-september-2026.csv"), paths.TecRegister);
            Assert.True(paths.Has(p => p.TecRegister));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Neso_rawdata_wins_over_the_acquire_tree_when_both_exist()
    {
        string root = NewRoot();
        try
        {
            string legacy = Path.Combine(root, "NESO", "RAWData", "Interconnector Register");
            string acquired = Path.Combine(root, "NESO", "Processors", "RAWData", "Interconnector Register");
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(acquired);
            File.WriteAllText(Path.Combine(legacy, "interconnector-register-01-may-2026.csv"), "stub");
            File.WriteAllText(Path.Combine(acquired, "interconnector-register-01-october-2026.csv"), "stub");
            Assert.Equal(Path.Combine(legacy, "interconnector-register-01-may-2026.csv"), new GdaPaths(root).InterconnectorRegister);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Parquet_only_message_is_unchanged_when_the_acquire_tree_lacks_the_register()
    {
        string root = NewRoot();
        try
        {
            // The acquire tree exists but holds a different register; the Parquet-only error still names NESO/RAWData.
            Directory.CreateDirectory(Path.Combine(root, "NESO", "Processors", "RAWData", "Embedded Register"));
            Directory.CreateDirectory(Path.Combine(root, "NESO", "Parquet", "transmission_entry_capacity_tec_register"));
            var ex = Assert.Throws<ParquetOnlyDatasetException>(() => new GdaPaths(root).TecRegister);
            Assert.StartsWith("GDA dataset folder not found: " + Path.Combine(root, "NESO", "RAWData", "Transmission Entry Capacity (TEC) register"),
                ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
