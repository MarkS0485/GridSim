using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-306 (November 2026): IEC 104 points were addressed by IOA alone, so two stations (common addresses, CASDU) on one
/// link with the same IOA fed one crosswalk point without a word. Now:
/// <list type="number">
/// <item>by default (crosswalk <c>"iec104Key": "ioa"</c> or absent) the names are unchanged, but an IOA seen with a second
/// common address on one link is a collision: its points are marked Invalid from then on, counted, and the first one is
/// named (the interposer prints it once on stderr);</item>
/// <item>the opt-in <c>"iec104Key": "casdu-ioa"</c> names every point <c>CA:&lt;casdu&gt;/IOA:&lt;ioa&gt;</c>, so stations
/// sharing a link never collide.</item>
/// </list>
/// </summary>
public class Gr306Iec104CommonAddressTests
{
    /// <summary>An I-format APDU holding one type 5 (step position) object: value 5, good quality.</summary>
    private static byte[] StepPosition(int casdu, int ioa)
    {
        var asdu = new List<byte> { 5, 0x01, 3, 0x00, (byte)(casdu & 0xFF), (byte)((casdu >> 8) & 0xFF),
            (byte)(ioa & 0xFF), (byte)((ioa >> 8) & 0xFF), (byte)((ioa >> 16) & 0xFF), 0x05, 0x00 };
        var apdu = new List<byte> { 0x68, (byte)(4 + asdu.Count), 0x00, 0x00, 0x00, 0x00 };
        apdu.AddRange(asdu);
        return apdu.ToArray();
    }

    private static RtuPoint One(Iec104Protocol p, int casdu, int ioa) => Assert.Single(p.Decode(StepPosition(casdu, ioa), QualityFold.Default));

    [Fact]
    public void By_default_names_are_unchanged_and_one_station_never_collides()
    {
        var p = new Iec104Protocol();
        Assert.Equal(Iec104PointKey.Ioa, p.PointKey);
        for (int i = 0; i < 3; i++)
        {
            RtuPoint pt = One(p, 1, 10);
            Assert.Equal("IOA:10", pt.Address);
            Assert.Equal(RtuQuality.Good, pt.Quality);
        }
        Assert.Equal(0, p.CommonAddressCollisions);
        Assert.Null(p.FirstCommonAddressCollision);
    }

    [Fact]
    public void An_ioa_seen_with_a_second_common_address_is_invalid_from_then_on_counted_and_named()
    {
        var p = new Iec104Protocol();
        Assert.Equal(RtuQuality.Good, One(p, 1, 10).Quality);

        RtuPoint clash = One(p, 2, 10);
        Assert.Equal("IOA:10", clash.Address);
        Assert.True(clash.Quality.HasFlag(RtuQuality.Invalid));
        Assert.Equal(1, p.CommonAddressCollisions);
        Assert.Equal("IOA 10 arrived with common address 2 after common address 1 on one link", p.FirstCommonAddressCollision);

        // The first station's point is no longer trusted either: the crosswalk cannot say which station it means.
        Assert.True(One(p, 1, 10).Quality.HasFlag(RtuQuality.Invalid));
        Assert.Equal(2, p.CommonAddressCollisions);
        // Another IOA of the second station is its own point and stays good; the first collision stays the one named.
        Assert.Equal(RtuQuality.Good, One(p, 2, 11).Quality);
        Assert.Equal("IOA 10 arrived with common address 2 after common address 1 on one link", p.FirstCommonAddressCollision);
    }

    [Fact]
    public void A_new_client_starts_a_new_link_but_keeps_the_counts()
    {
        var p = new Iec104Protocol();
        One(p, 1, 10);
        One(p, 2, 10);
        ((IRtuSessionState)p).ResetSession();
        Assert.Equal(RtuQuality.Good, One(p, 2, 10).Quality);   // the next client is another link
        Assert.Equal(1, p.CommonAddressCollisions);
    }

    [Fact]
    public void The_casdu_ioa_key_names_each_station_apart_and_never_collides()
    {
        var p = new Iec104Protocol { PointKey = Iec104PointKey.CasduIoa };
        RtuPoint a = One(p, 1, 10), b = One(p, 2, 10), c = One(p, 65535, 16777215);
        Assert.Equal("CA:1/IOA:10", a.Address);
        Assert.Equal("CA:2/IOA:10", b.Address);
        Assert.Equal("CA:65535/IOA:16777215", c.Address);
        Assert.Equal(RtuQuality.Good, a.Quality);
        Assert.Equal(RtuQuality.Good, b.Quality);
        Assert.Equal(0, p.CommonAddressCollisions);
    }

    private static string WriteMap(string? key)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gr306-{Guid.NewGuid():N}.json");
        string keyMember = key is null ? "" : $"\"iec104Key\": \"{key}\", ";
        File.WriteAllText(path, "{ \"schema\": \"gridsim-rtu-pointmap/1\", " + keyMember +
            "\"bindings\": [ { \"address\": \"CA:1/IOA:10\", \"kind\": \"VmPu\", \"bus\": 1, \"sigma\": 0.01 } ] }");
        return path;
    }

    [Theory]
    [InlineData(null, Iec104PointKey.Ioa)]
    [InlineData("ioa", Iec104PointKey.Ioa)]
    [InlineData("casdu-ioa", Iec104PointKey.CasduIoa)]
    public void The_crosswalk_carries_the_key_and_an_absent_one_is_ioa(string? key, Iec104PointKey expected)
    {
        string path = WriteMap(key);
        try
        {
            RtuPointMap m = RtuPointMap.Load(path);
            Assert.Equal(key, m.Iec104Key);
            Assert.Equal(expected, m.ResolveIec104Key());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("CASDU-IOA")]
    [InlineData("casdu")]
    [InlineData("")]
    public void An_unknown_key_is_refused_at_load(string key)
    {
        string path = WriteMap(key);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => RtuPointMap.Load(path));
            Assert.Contains("iec104Key", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_crosswalk_with_the_key_round_trips_and_one_without_it_writes_no_member()
    {
        string path = WriteMap("casdu-ioa");
        string again = Path.Combine(Path.GetTempPath(), $"gr306-{Guid.NewGuid():N}.json");
        string plain = WriteMap(null);
        string plainAgain = Path.Combine(Path.GetTempPath(), $"gr306-{Guid.NewGuid():N}.json");
        try
        {
            RtuPointMap.Load(path).Save(again);
            Assert.Equal("casdu-ioa", RtuPointMap.Load(again).Iec104Key);
            RtuPointMap.Load(plain).Save(plainAgain);
            Assert.DoesNotContain("iec104Key", File.ReadAllText(plainAgain), StringComparison.Ordinal);
        }
        finally { foreach (string f in new[] { path, again, plain, plainAgain }) if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public void The_pipeline_maps_a_casdu_ioa_crosswalk_point()
    {
        string path = WriteMap("casdu-ioa");
        try
        {
            RtuPointMap map = RtuPointMap.Load(path);
            var p = new Iec104Protocol { PointKey = map.ResolveIec104Key() };
            RtuPoint pt = One(p, 1, 10);
            Assert.True(map.TryResolve(pt.Address, out RtuPointBinding b));
            Assert.Equal(1, b.Bus);
            Assert.False(map.TryResolve(One(p, 2, 10).Address, out _));   // the other station's IOA 10 is not this point
        }
        finally { File.Delete(path); }
    }
}
