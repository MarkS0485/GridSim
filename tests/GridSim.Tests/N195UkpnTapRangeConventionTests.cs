using GridSim.Core.Model;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Ukpn;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N195 (October 2026): UKPN's LTDS Table 2a publishes a transformer's tap range as the ratio at each end tap in
/// percent OF the nominal ratio (90 / 110), while the shared GDA builder expects an OFFSET from nominal (-10 / +10),
/// which is what NGED and NPg publish. Read unconverted, 2,170 of the 2,171 UKPN units on the full lake were refused
/// as "range excludes the nominal 0 % tap" and got no tap changer. <see cref="UkpnSource"/> now subtracts 100.
/// <para>
/// These tests build small UKPN lakes from CSV text (the same path the full CSVs take) and check: the exact offsets
/// handed to the builder; that a published 90 / 110 gives exactly the 0.9 / 1.1 limits an NGED-style -10 / +10 unit
/// gets (bit for bit); that 100 / 100 is a fixed-ratio unit (no OLTC, counted); that an asymmetric range keeps its
/// asymmetry; that a range touching nominal (100 / 112) is accepted; and that a blank cell is refused loudly instead
/// of being read as 0 % of nominal.
/// </para>
/// </summary>
public sealed class N195UkpnTapRangeConventionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-n195-{Guid.NewGuid():N}");

    public N195UkpnTapRangeConventionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Write a minimal UKPN RAWData lake: one 132 kV grid node feeding, through one 132/33 kV transformer per entry
    /// of <paramref name="taps"/>, its own 33 kV node, each carrying 10 MW of demand. The tap cells are written as
    /// given (an empty string is a blank cell). Column order follows the real Table 2a header (Max before Min).
    /// </summary>
    private string Lake(params (string Min, string Max)[] taps)
    {
        string root = Path.Combine(_root, "lake-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "UkPowerNetworks", "RAWData");
        Directory.CreateDirectory(raw);

        // A circuit is needed so the reader sees a 132 kV node (circuits are where UKPN nodes come from too).
        File.WriteAllText(Path.Combine(raw, "ltds-table-1-circuit-data.csv"),
            "LicenceArea,GridSupplyPoint,FromNode,ToNode,FromSubstation,ToSubstation,OperatingVoltage (kV),LineName," +
            "Positive_Sequence_Impedance_R_Percent,Positive_Sequence_Impedance_X_Percent,Susceptance_B_Percent," +
            "Rating_Amps_Winter,Rating_Amps_Summer,Circuit_Length (km)\n" +
            "EPN,GSP1,G1,G2,GRID,GRID2,132,L1,0.1,0.5,0,1000,900,5\n");

        var tx = new System.Text.StringBuilder(
            "GridSupplyPoint,HV_Node,HV_Substation,Voltage_HV (kV),LV_Node,LV_Substation,Voltage_LV (kV),Vector_Group," +
            "Positive_Sequence_Impedance_R_Percent,Positive_Sequence_Impedance_X_Percent,Zero_Sequence_Impedance_X_Percent," +
            "Tap_Range_Max_Percent,Tap_Range_Min_Percent,Transformer_Rating_MVA_Winter,Transformer_Rating_MVA_Summer," +
            "LicenceArea\n");
        var demand = new System.Text.StringBuilder("Substation,GridSupplyPoint,Maximum_Demand_25_26_MW,Maximum_Demand_25_26_PF,Firm_Capacity_MW\n");
        for (int i = 0; i < taps.Length; i++)
        {
            tx.Append($"GSP1,G1,GRID,132,P{i},PRIM{i},33,YNd1,0.5,20,18,{taps[i].Max},{taps[i].Min},90,80,EPN\n");
            demand.Append($"PRIM{i},GSP1,10,0.95,20\n");
        }
        File.WriteAllText(Path.Combine(raw, "ltds-table-2a-transformer-2w.csv"), tx.ToString());
        File.WriteAllText(Path.Combine(raw, "ltds-table-3a-load-data-observed.csv"), demand.ToString());
        return root;
    }

    [Fact]
    public void Percent_of_nominal_becomes_an_exact_offset_from_nominal()
    {
        LtdsDataset ds = UkpnSource.Load(Lake(("90", "110"), ("79", "110"), ("100", "100"), ("100", "112"), ("64", "120")));
        Assert.Equal(5, ds.Transformers2.Count);
        // Exact: whole-number percents minus 100 are exact in binary.
        Assert.Equal(-10.0, ds.Transformers2[0].MinTap);
        Assert.Equal(10.0, ds.Transformers2[0].MaxTap);
        Assert.Equal(-21.0, ds.Transformers2[1].MinTap);
        Assert.Equal(10.0, ds.Transformers2[1].MaxTap);
        Assert.Equal(0.0, ds.Transformers2[2].MinTap);
        Assert.Equal(0.0, ds.Transformers2[2].MaxTap);
        Assert.Equal(0.0, ds.Transformers2[3].MinTap);
        Assert.Equal(12.0, ds.Transformers2[3].MaxTap);
        Assert.Equal(-36.0, ds.Transformers2[4].MinTap);   // the lake's widest range (64 / 120)
        Assert.Equal(20.0, ds.Transformers2[4].MaxTap);
    }

    [Fact]
    public void A_published_90_110_unit_gets_exactly_the_limits_of_an_offset_minus_10_plus_10_unit()
    {
        GdaBuildResult r = GdaModelBuilder.Build(UkpnSource.Load(Lake(("90", "110"))), new GdaBuildOptions());
        Oltc o = Assert.Single(r.Model.Oltcs);
        // Bit-identical to the 0.9 / 1.1 the builder gives an NGED / NPg unit publishing -10 / +10.
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0 + -10.0 / 100.0), BitConverter.DoubleToInt64Bits(o.MinTap));
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0 + 10.0 / 100.0), BitConverter.DoubleToInt64Bits(o.MaxTap));
        Assert.Equal(0.9, o.MinTap);
        Assert.Equal(1.1, o.MaxTap);
        Assert.Equal(0, r.Diagnostics.OltcsWithInvalidTapRange);
        Assert.Equal(0, r.Diagnostics.OltcsWithoutTapRange);
        Assert.DoesNotContain(r.Diagnostics.Notes, n => n.Contains("WARNING OLTC", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_range_bracketing_100_gets_a_tap_changer_and_100_100_is_a_fixed_ratio_unit()
    {
        GdaBuildResult r = GdaModelBuilder.Build(
            UkpnSource.Load(Lake(("90", "110"), ("79", "110"), ("100", "100"), ("100", "112"))), new GdaBuildOptions());
        // Before N195 all four were refused or skipped: 90/110, 79/110 and 100/112 as "range excludes the nominal
        // 0 % tap" (their minimum, read as an offset, was above 0), and 100/100 the same way.
        Assert.Equal(3, r.Model.Oltcs.Count);
        Assert.Equal(1, r.Diagnostics.OltcsWithoutTapRange);     // 100 / 100
        Assert.Equal(0, r.Diagnostics.OltcsWithInvalidTapRange);

        // The asymmetric 79 / 110 range keeps its asymmetry: 0.79 .. 1.1, computed exactly as the builder does.
        Oltc asym = r.Model.Oltcs.Single(o => o.MinTap < 0.85);
        Assert.Equal(1.0 + -21.0 / 100.0, asym.MinTap);
        Assert.Equal(1.0 + 10.0 / 100.0, asym.MaxTap);

        // A range that ends at nominal (100 / 112) starts at its own limit 1.0 and may only rise.
        Oltc edge = r.Model.Oltcs.Single(o => o.MinTap == 1.0);
        Assert.Equal(1.0 + 12.0 / 100.0, edge.MaxTap);
    }

    [Fact]
    public void A_blank_tap_cell_is_refused_loudly_not_read_as_zero_percent()
    {
        GdaBuildResult r = GdaModelBuilder.Build(UkpnSource.Load(Lake(("", "110"), ("90", "110"))), new GdaBuildOptions());
        // A blank must not become 0 % of nominal (-100 % offset, a ratio of 0) or a silent 0 / 0 "no tap changer":
        // it is a unit whose range is unknown, so it gets no OLTC and is named in a WARNING note.
        Assert.Single(r.Model.Oltcs);
        Assert.Equal("TX G1/P1 132/33", r.Model.Oltcs[0].Transformer);
        Assert.Equal(1, r.Diagnostics.OltcsWithInvalidTapRange);
        Assert.Equal(0, r.Diagnostics.OltcsWithoutTapRange);
        string warn = Assert.Single(r.Diagnostics.Notes, n => n.StartsWith("WARNING OLTC", StringComparison.Ordinal));
        Assert.Contains("not a finite number", warn, StringComparison.Ordinal);
        Assert.Contains("'TX G1/P0 132/33'", warn, StringComparison.Ordinal);   // the unit with the blank cell is named
    }

    [Fact]
    public void Ltds_dataset_from_ukpn_keeps_nan_for_a_blank_cell()
    {
        LtdsDataset ds = UkpnSource.Load(Lake(("", "")));
        Assert.True(double.IsNaN(ds.Transformers2[0].MinTap));
        Assert.True(double.IsNaN(ds.Transformers2[0].MaxTap));
    }
}
