using GridSim.Core.Interpose;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N080 (October 2026): an RTU point-map (the interposer's crosswalk, gridsim-rtu-pointmap/1) may bind each native
/// address once. Two bindings for one address used to load silently and the last one won, so one of the two
/// measurements was dropped without a word. Load now refuses the file, naming it and every repeated address.
/// </summary>
public class RtuPointMapDuplicateAddressTests
{
    private static string Write(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"pm-n080-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Two_bindings_for_one_address_are_refused_naming_the_address()
    {
        string path = Write("""
            { "schema": "gridsim-rtu-pointmap/1", "bindings": [
              { "address": "IOA:1001", "kind": "VmPu", "bus": 4 },
              { "address": "IOA:1002", "kind": "VmPu", "bus": 5 },
              { "address": "IOA:1001", "kind": "VmPu", "bus": 7 } ] }
            """);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => RtuPointMap.Load(path));
            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.Contains("1 address(es) bound more than once", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'IOA:1001' (2 bindings)", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("IOA:1002", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unique_addresses_still_load_and_resolve()
    {
        string path = Write("""
            { "schema": "gridsim-rtu-pointmap/1", "bindings": [
              { "address": "IOA:1001", "kind": "VmPu", "bus": 4 },
              { "address": "IOA:1002", "kind": "VmPu", "bus": 5 } ] }
            """);
        try
        {
            RtuPointMap m = RtuPointMap.Load(path);
            Assert.True(m.TryResolve("IOA:1002", out RtuPointBinding b));
            Assert.Equal(5, b.Bus);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Addresses_differing_only_in_case_are_different_addresses()
    {
        // The index compares exactly (ordinal), so the check does too: no false refusal.
        RtuPointMap.ValidateUniqueAddresses(new[]
        {
            new RtuPointBinding { Address = "mb:U1:3:1" },
            new RtuPointBinding { Address = "MB:U1:3:1" },
        }, "in-memory");
    }
}
