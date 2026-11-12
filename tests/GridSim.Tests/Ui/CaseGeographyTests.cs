using System.Globalization;
using System.Text;
using GridSim.Ui.Geography;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// The case geography reader (<see cref="CaseGeography"/>, format gda-geography/1): a valid file gives the land and
/// region rings in map world coordinates by the file's own projection, exactly as a bus at the same position is drawn
/// (x, 1 - y); a missing file is "no geography" with no problem; and every way a present file can be malformed is
/// refused whole, with a reason naming the fault, so the map can never draw part of a bad file or go blank silently.
/// All documents here are synthetic (a made-up box and a made-up land), written by the test itself.
/// </summary>
public sealed class CaseGeographyTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The synthetic projection: S 10, W 20, N 30, E 60, margin 0.05.
    private const double S = 10, W = 20, N = 30, E = 60, M = 0.05;

    // The declared affine, written out here as the file's formula states it (clamped to 0..1).
    private static (double X, double Y) Affine(double lon, double lat) =>
        (Math.Clamp(M + (1 - 2 * M) * (lon - W) / (E - W), 0.0, 1.0),
         Math.Clamp(M + (1 - 2 * M) * (lat - S) / (N - S), 0.0, 1.0));

    // One [lon, lat, x, y] point, x/y rounded to 5 decimals as GDA writes them (dx/dy shift the written x/y).
    private static string Pt(double lon, double lat, double dx = 0, double dy = 0)
    {
        (double x, double y) = Affine(lon, lat);
        return string.Create(Inv, $"[{lon}, {lat}, {Math.Round(x + dx, 5)}, {Math.Round(y + dy, 5)}]");
    }

    private static string Ring(params (double Lon, double Lat)[] pts) => "[" + string.Join(", ", pts.Select(p => Pt(p.Lon, p.Lat))) + "]";

    private static readonly string LandRing = Ring((25, 12), (55, 12), (55, 28), (25, 28));
    private static readonly string RegionA = Ring((25, 12), (40, 12), (40, 28), (25, 28));
    private static readonly string RegionB = Ring((40, 12), (55, 12), (55, 28), (40, 28));

    /// <summary>A valid document; each argument replaces one part so a test can break exactly one thing.</summary>
    private static string Doc(
        string format = "\"gda-geography/1\"",
        string? projection = null,
        string layout = "[\"lon\", \"lat\", \"x\", \"y\"]",
        string? land = null,
        string? regions = null)
    {
        projection ??= string.Create(Inv, $"{{\"kind\": \"lonlat-affine\", \"south\": {S}, \"west\": {W}, \"north\": {N}, \"east\": {E}, \"margin\": {M}}}");
        land ??= $"[{{\"name\": \"Testland\", \"rings\": [{LandRing}]}}]";
        regions ??= $"[{{\"name\": \"West\", \"iso\": \"TL-W\", \"rings\": [{RegionA}]}}, {{\"name\": \"East\", \"iso\": \"TL-E\", \"rings\": [{RegionB}]}}]";
        var sb = new StringBuilder("{");
        sb.Append("\"format\": ").Append(format).Append(", ");
        sb.Append("\"projection\": ").Append(projection).Append(", ");
        sb.Append("\"point_layout\": ").Append(layout).Append(", ");
        sb.Append("\"land\": ").Append(land);
        if (regions != "OMIT") sb.Append(", \"regions\": ").Append(regions);
        sb.Append(", \"sources\": []}");
        return sb.ToString();
    }

    private static string Refused(string json)
    {
        var ex = Assert.Throws<FormatException>(() => CaseGeography.Parse(json));
        return ex.Message;
    }

    // ---------------------------------------------------------------- a valid file

    [Fact]
    public void A_valid_file_gives_the_land_and_regions_in_world_coordinates_by_its_own_projection()
    {
        CaseGeography g = CaseGeography.Parse(Doc());

        Assert.Equal(new CaseGeography.Projection(S, W, N, E, M), g.Box);
        Assert.Equal("Testland", g.LandName);
        CaseGeography.Area land = Assert.Single(g.Land);
        WorldPoint[] ring = Assert.Single(land.Rings);
        Assert.Equal(4, ring.Length);
        Assert.Equal(4, g.LandPoints);
        Assert.Equal(8, g.RegionPoints);

        // Each world point is the declared affine of its lon/lat, y turned north-down: exactly, not approximately.
        (double Lon, double Lat)[] lonLat = { (25, 12), (55, 12), (55, 28), (25, 28) };
        for (int i = 0; i < lonLat.Length; i++)
        {
            (double x, double y) = Affine(lonLat[i].Lon, lonLat[i].Lat);
            Assert.Equal(x, ring[i].X);
            Assert.Equal(1.0 - y, ring[i].Y);
            Assert.Equal(new WorldPoint(x, 1.0 - y), g.WorldOf(lonLat[i].Lon, lonLat[i].Lat));
        }

        Assert.Equal(new[] { "West", "East" }, g.Regions.Select(r => r.Name));
        Assert.Equal(new[] { "TL-W", "TL-E" }, g.Regions.Select(r => r.Code));
    }

    [Fact]
    public void The_projection_is_the_bus_projection_corners_and_margin_land_where_the_formula_puts_them()
    {
        CaseGeography g = CaseGeography.Parse(Doc());
        // South-west corner: x = m, y = m, so world (m, 1 - m); north-east: x = 1 - m, y = 1 - m, world (1 - m, m).
        Assert.Equal(new WorldPoint(M, 1.0 - M), g.WorldOf(W, S));
        Assert.Equal(new WorldPoint(M + (1 - 2 * M) * (E - W) / (E - W), 1.0 - (M + (1 - 2 * M) * (N - S) / (N - S))), g.WorldOf(E, N));
        // Outside the box the affine is clamped to 0..1, as the file's formula states.
        Assert.Equal(new WorldPoint(0.0, 1.0), g.WorldOf(-179, -89));
        Assert.Equal(new WorldPoint(1.0, 0.0), g.WorldOf(179, 89));
    }

    [Fact]
    public void Regions_are_optional()
    {
        Assert.Empty(CaseGeography.Parse(Doc(regions: "OMIT")).Regions);
        Assert.Empty(CaseGeography.Parse(Doc(regions: "null")).Regions);
        Assert.Empty(CaseGeography.Parse(Doc(regions: "[]")).Regions);
    }

    [Fact]
    public void An_area_whose_rings_were_all_left_out_is_kept_with_nothing_to_draw()
    {
        // GDA's simplification can leave out every ring of a small island territory (India's Lakshadweep): that region
        // is written with "rings": []. It is a valid file: the region is kept with no rings, the others draw as usual.
        string regions = $"[{{\"name\": \"West\", \"iso\": \"TL-W\", \"rings\": [{RegionA}]}}, {{\"name\": \"Isles\", \"iso\": \"TL-I\", \"rings\": []}}]";
        CaseGeography g = CaseGeography.Parse(Doc(regions: regions));
        Assert.Equal(new[] { "West", "Isles" }, g.Regions.Select(r => r.Name));
        Assert.Single(g.Regions[0].Rings);
        Assert.Empty(g.Regions[1].Rings);
        Assert.Equal(4, g.RegionPoints);

        // The same for a land area, as long as some land area has a ring.
        string land = $"[{{\"name\": \"Testland\", \"rings\": [{LandRing}]}}, {{\"name\": \"Isles\", \"rings\": []}}]";
        CaseGeography l = CaseGeography.Parse(Doc(land: land));
        Assert.Equal(2, l.Land.Count);
        Assert.Empty(l.Land[1].Rings);

        // Land with no ring in any area is still refused, and so is a ring list that is not a list.
        Assert.Contains("land has no rings in any area",
            Refused(Doc(land: "[{\"name\": \"A\", \"rings\": []}, {\"name\": \"B\", \"rings\": []}]")), StringComparison.Ordinal);
        Assert.Contains("rings is not an array",
            Refused(Doc(regions: "[{\"name\": \"West\", \"rings\": {}}]")), StringComparison.Ordinal);
    }

    [Fact]
    public void Written_points_rounded_to_five_decimals_are_accepted_and_a_shift_past_the_tolerance_is_not()
    {
        // 5e-5 is half the tolerance: accepted. 2e-4 is twice it: refused, naming the point and both positions.
        string near = Ring((25, 12), (55, 12), (55, 28)).Replace(Pt(55, 28), Pt(55, 28, dx: 5e-5), StringComparison.Ordinal);
        Assert.Single(CaseGeography.Parse(Doc(land: $"[{{\"name\": \"Testland\", \"rings\": [{near}]}}]")).Land);

        string far = Ring((25, 12), (55, 12), (55, 28)).Replace(Pt(55, 28), Pt(55, 28, dy: 2e-4), StringComparison.Ordinal);
        string why = Refused(Doc(land: $"[{{\"name\": \"Testland\", \"rings\": [{far}]}}]"));
        Assert.Contains("land[0] \"Testland\" ring 0 point 2", why, StringComparison.Ordinal);
        Assert.Contains("the points and the projection disagree", why, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- malformed files are refused whole

    [Fact]
    public void A_wrong_format_or_projection_kind_is_refused()
    {
        Assert.Contains("only \"gda-geography/1\"", Refused(Doc(format: "\"gda-geography/2\"")), StringComparison.Ordinal);
        Assert.Contains("\"format\" is not a string", Refused(Doc(format: "1")), StringComparison.Ordinal);
        string merc = string.Create(Inv, $"{{\"kind\": \"mercator\", \"south\": {S}, \"west\": {W}, \"north\": {N}, \"east\": {E}, \"margin\": {M}}}");
        Assert.Contains("only \"lonlat-affine\"", Refused(Doc(projection: merc)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": 30, \"west\": 20, \"north\": 10, \"east\": 60, \"margin\": 0.05}", "is not above south")]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": 10, \"west\": 60, \"north\": 30, \"east\": 20, \"margin\": 0.05}", "is not east of west")]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": 10, \"west\": 20, \"north\": 30, \"east\": 60, \"margin\": 0.5}", "outside 0 <= m < 0.5")]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": 10, \"west\": 20, \"north\": 30, \"east\": 60, \"margin\": -0.01}", "outside 0 <= m < 0.5")]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": -91, \"west\": 20, \"north\": 30, \"east\": 60, \"margin\": 0.05}", "outside -90..90")]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": 10, \"west\": 20, \"north\": 30, \"east\": 181, \"margin\": 0.05}", "outside -180..180")]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": 10, \"west\": 20, \"north\": 30, \"margin\": 0.05}", "has no \"east\"")]
    [InlineData("{\"kind\": \"lonlat-affine\", \"south\": \"10\", \"west\": 20, \"north\": 30, \"east\": 60, \"margin\": 0.05}", "south is not a number")]
    [InlineData("[]", "projection is not an object")]
    public void A_bad_projection_is_refused(string projection, string expected)
    {
        Assert.Contains(expected, Refused(Doc(projection: projection)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[\"lat\", \"lon\", \"x\", \"y\"]")]
    [InlineData("[\"lon\", \"lat\", \"x\"]")]
    [InlineData("[\"lon\", \"lat\", \"x\", \"y\", \"z\"]")]
    [InlineData("\"lon,lat,x,y\"")]
    public void A_point_layout_other_than_lon_lat_x_y_is_refused(string layout)
    {
        Assert.Contains("point_layout must be the array", Refused(Doc(layout: layout)), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_or_empty_land_is_refused()
    {
        Assert.Contains("land has no areas", Refused(Doc(land: "[]")), StringComparison.Ordinal);
        Assert.Contains("land is not an array", Refused(Doc(land: "{}")), StringComparison.Ordinal);
        Assert.Contains("has no rings", Refused(Doc(land: "[{\"name\": \"Testland\", \"rings\": []}]")), StringComparison.Ordinal);
        Assert.Contains("has no \"name\"", Refused(Doc(land: $"[{{\"rings\": [{LandRing}]}}]")), StringComparison.Ordinal);
        string noLand = Doc().Replace("\"land\": ", "\"landx\": ", StringComparison.Ordinal);
        Assert.Contains("has no \"land\"", Refused(noLand), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_ring_or_point_is_refused_naming_where()
    {
        string two = Ring((25, 12), (55, 12));
        Assert.Contains("ring 0 has 2 point(s)", Refused(Doc(land: $"[{{\"name\": \"Testland\", \"rings\": [{two}]}}]")), StringComparison.Ordinal);

        string threeNumbers = "[[25, 12, 0.1625], " + Pt(55, 12) + ", " + Pt(55, 28) + "]";
        Assert.Contains("point 0 is not [lon, lat, x, y]",
            Refused(Doc(land: $"[{{\"name\": \"Testland\", \"rings\": [{threeNumbers}]}}]")), StringComparison.Ordinal);

        string textLon = "[[\"25\", 12, 0.1625, 0.14], " + Pt(55, 12) + ", " + Pt(55, 28) + "]";
        Assert.Contains("point 0 lon is not a number",
            Refused(Doc(land: $"[{{\"name\": \"Testland\", \"rings\": [{textLon}]}}]")), StringComparison.Ordinal);

        string offEarth = "[[200, 12, 1, 0.14], " + Pt(55, 12) + ", " + Pt(55, 28) + "]";
        Assert.Contains("is not a position on the Earth",
            Refused(Doc(land: $"[{{\"name\": \"Testland\", \"rings\": [{offEarth}]}}]")), StringComparison.Ordinal);

        // A bad region refuses the whole file too, not just that region.
        string badRegion = $"[{{\"name\": \"West\", \"rings\": [{RegionA}]}}, {{\"name\": \"East\", \"rings\": [{two}]}}]";
        Assert.Contains("regions[1] \"East\" ring 0 has 2 point(s)", Refused(Doc(regions: badRegion)), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_is_not_a_JSON_object_is_refused()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => CaseGeography.Parse("{\"format\": "));
        Assert.Contains("not a JSON object", Refused("[]"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- loading from a case folder

    [Fact]
    public void A_case_folder_without_the_file_has_no_geography_and_no_problem()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-casegeo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(CaseGeography.TryLoad(dir, out CaseGeography? g, out string? problem));
            Assert.Null(g);
            Assert.Null(problem);
            Assert.False(CaseGeography.TryLoad(null, out g, out problem));
            Assert.Null(g);
            Assert.Null(problem);
            Assert.False(CaseGeography.TryLoad("", out g, out problem));
            Assert.Null(problem);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_case_folder_with_a_valid_file_loads_it_and_a_malformed_one_gives_the_reason()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-casegeo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, CaseGeography.FileName);
            File.WriteAllText(path, Doc());
            Assert.True(CaseGeography.TryLoad(dir, out CaseGeography? g, out string? problem));
            Assert.NotNull(g);
            Assert.Null(problem);
            Assert.Equal("Testland", g!.LandName);

            File.WriteAllText(path, Doc(format: "\"gda-geography/9\""));
            Assert.False(CaseGeography.TryLoad(dir, out g, out problem));
            Assert.Null(g);
            Assert.StartsWith("geography.json: format is \"gda-geography/9\"", problem, StringComparison.Ordinal);

            File.WriteAllText(path, "{ not json");
            Assert.False(CaseGeography.TryLoad(dir, out g, out problem));
            Assert.Null(g);
            Assert.StartsWith("geography.json: ", problem, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
