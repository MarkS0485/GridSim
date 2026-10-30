using GridSim.Core.Model;
using GridSim.Gda.Geo;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// Tests for <see cref="GeoLocator"/> using a SYNTHETIC substations CSV written to a
/// temp directory  -  no real grid data. Verifies the name-normalisation join, the
/// unit-square GB projection (north-up), and that out-of-GB / unmatched buses are
/// left alone.
/// </summary>
public class GeoLocatorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _csv;

    public GeoLocatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gridsim-geo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _csv = Path.Combine(_dir, "substations.csv");

        // name carries a trailing voltage token (must be normalised away); a southern
        // and a northern substation; one row with coordinates outside the GB box that
        // must be skipped; plus extra columns to mirror the real schema shape.
        File.WriteAllText(_csv,
            "substationID,name,voltages,longitude,latitude,GSP\n" +
            "1,Lands End 132kV,132kV,-5.7129,50.0664,GSP_A\n" +     // far SW, low Y
            "2,Thurso 33KV,33kV,-3.5276,58.5934,GSP_B\n" +          // far north, high Y
            "3,Out Of Bounds 400 KV,400kV,10.0,60.5,GSP_C\n");      // outside GB -> skipped
    }

    private static Bus NamedBus(int id, string name) =>
        new(id, BusType.Pq, Name: name, X: 0.42, Y: 0.42);

    private static GridModel ModelWith(params Bus[] buses) => new()
    {
        BaseMva = 100,
        Buses = buses,
        Generators = Array.Empty<Generator>(),
        Branches = Array.Empty<Branch>(),
    };

    [Fact]
    public void Normalize_strips_trailing_voltage_token_and_collapses_whitespace()
    {
        Assert.Equal("LANDS END", GeoLocator.Normalize("Lands End 132kV"));
        Assert.Equal("THURSO", GeoLocator.Normalize("Thurso 33KV"));
        Assert.Equal("EXETER", GeoLocator.Normalize("  Exeter   400 KV "));
        Assert.Equal("RYEFORD", GeoLocator.Normalize("Ryeford 132/33/11KV"));
    }

    [Fact]
    public void Matched_bus_gets_plausible_coordinates_inside_unit_square()
    {
        // Bus name has a *different* voltage token from the CSV row, proving the join
        // is on the normalised (token-stripped) name, not the raw text.
        var model = ModelWith(NamedBus(0, "Lands End 33kV"));
        (GridModel located, int matched, int total) = GeoLocator.Apply(model, _csv);

        Assert.Equal(1, matched);
        Assert.Equal(1, total);
        Bus b = located.Buses[0];
        Assert.InRange(b.X, 0.0, 1.0);
        Assert.InRange(b.Y, 0.0, 1.0);
        Assert.NotEqual(0.42, b.X);   // was relocated off the placeholder
    }

    [Fact]
    public void Northern_substation_gets_higher_Y_than_southern()
    {
        var model = ModelWith(NamedBus(0, "Lands End"), NamedBus(1, "Thurso"));
        (GridModel located, int matched, _) = GeoLocator.Apply(model, _csv);

        Assert.Equal(2, matched);
        Bus south = located.Buses[0];
        Bus north = located.Buses[1];
        Assert.True(north.Y > south.Y, $"north Y {north.Y} should exceed south Y {south.Y}");
    }

    [Fact]
    public void Unmatched_bus_keeps_existing_coordinates()
    {
        var model = ModelWith(NamedBus(0, "Nowhere Special"));
        (GridModel located, int matched, _) = GeoLocator.Apply(model, _csv);

        Assert.Equal(0, matched);
        Assert.Equal(0.42, located.Buses[0].X);
        Assert.Equal(0.42, located.Buses[0].Y);
    }

    [Fact]
    public void Out_of_gb_substation_is_skipped_so_its_name_does_not_match()
    {
        var model = ModelWith(NamedBus(0, "Out Of Bounds"));
        (GridModel located, int matched, _) = GeoLocator.Apply(model, _csv);

        Assert.Equal(0, matched);
        Assert.Equal(0.42, located.Buses[0].X);   // untouched
    }

    [Fact]
    public void Project_maps_corners_of_gb_box_to_unit_square_corners()
    {
        (double x0, double y0) = GeoLocator.Project(GeoLocator.LonMin, GeoLocator.LatMin);
        (double x1, double y1) = GeoLocator.Project(GeoLocator.LonMax, GeoLocator.LatMax);
        Assert.Equal(0.0, x0, 6);
        Assert.Equal(0.0, y0, 6);
        Assert.Equal(1.0, x1, 6);
        Assert.Equal(1.0, y1, 6);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
