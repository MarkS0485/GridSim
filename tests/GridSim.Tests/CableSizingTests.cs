using System;
using System.Linq;
using GridSim.Core.Analysis.CableRating;
using GridSim.Core.Analysis.CableRating.Sizing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-20d: cable sizing by the IEC 60364-5-52 / BS 7671 design rule. The standards' tables are paid and not held, so the fixture below is
/// SYNTHETIC (arbitrary round numbers written for these tests; none is a value from any standard). What is verified is the method: the
/// arithmetic In / (Ca Cg) against hand values, voltage drop by the circuit-theory formulae, the adiabatic equation, the OUT OF SCOPE
/// behaviour of empty or incomplete tables, and that the IEC 60287 basis is exactly the existing calculation. The IEC 60287 module itself is untouched.
/// </summary>
public class CableSizingTests
{
    private const string Fixture = """
    {
      "schema": "gridsim-cable-tables/1", "id": "synthetic-test-fixture",
      "provenance": {"source": "SYNTHETIC TEST FIXTURE - not a value from any standard", "edition": "none", "filledBy": "test", "date": "2026-11-17"},
      "ampacity": [{"method": "X1", "conductor": "copper", "insulation": "pvc", "loadedConductors": 2,
                    "sizesMm2": [1.5, 2.5, 4, 6, 10, 16], "amps": [10, 14, 19, 25, 34, 45]}],
      "factors": [{"kind": "ambient", "method": "", "insulation": "pvc", "keys": [25, 30, 35], "factors": [1.1, 1.0, 0.9]},
                  {"kind": "grouping", "method": "X1", "insulation": "", "keys": [1, 2, 3], "factors": [1.0, 0.8, 0.7]}],
      "voltDrop": [{"conductor": "copper", "insulation": "pvc", "phases": "1ph", "sizesMm2": [1.5, 2.5, 4, 6, 10, 16], "mvPerAPerM": [30, 18, 11, 7.5, 4.4, 2.8]}],
      "impedance": [{"conductor": "copper", "insulation": "pvc", "sizesMm2": [1.5, 2.5, 4, 6, 10, 16],
                     "rOhmPerKm": [12, 7.3, 4.6, 3.1, 1.8, 1.15], "xOhmPerKm": [0.08, 0.08, 0.08, 0.08, 0.08, 0.08]}],
      "adiabaticK": [{"conductor": "copper", "insulation": "pvc", "k": 100}]
    }
    """;

    private static CableTables Tables => CableTables.Parse(Fixture);

    private static SizingCircuit Circuit(double ib = 20, double? inA = null, double? amb = null, int? grp = null, double? vdLim = null,
        double? ifault = null, double? tfault = null, double v = 230, double len = 40, double pf = 0.8, int phases = 1) =>
        new(ib, inA, v, phases, len, pf, "copper", "pvc", "X1", 2, amb, grp, null, vdLim, ifault, tfault);

    [Fact]
    public void Required_tabulated_current_is_in_over_the_product_of_factors()
    {
        CableSizingResult r = CableSizing.Compute("t", Tables, Circuit(ib: 20, inA: 25, amb: 35, grp: 2));
        Assert.Equal("EVALUATED", r.Status);
        Assert.Equal(0.9 * 0.8, r.CombinedFactor!.Value, 12);
        Assert.Equal(25.0 / 0.72, r.RequiredTabulatedAmps!.Value, 12);   // 34.72 A: the 34 A size is just short
        Assert.Equal(16.0, r.SelectedSizeMm2);
        Assert.Equal(34.0 * 0.72, r.Candidates.First(c => c.SizeMm2 == 10).PermittedAmps!.Value, 12);
        Assert.False(r.Candidates.First(c => c.SizeMm2 == 10).AmpacityOk);
        // Without grouping: 25 / 0.9 = 27.8 A, so the 34 A size suffices.
        Assert.Equal(10.0, CableSizing.Compute("t", Tables, Circuit(ib: 20, inA: 25, amb: 35)).SelectedSizeMm2);
        // The design current stands in for In when no device is given (stated in the factors/inputs).
        Assert.Equal(20.0, CableSizing.Compute("t", Tables, Circuit()).DeviceCurrentA);
    }

    [Fact]
    public void Voltage_drop_from_tabulated_mv_per_a_per_m_raises_the_size()
    {
        // Ib = In = 20 A: ampacity alone gives 6 mm2 (25 A). Drop of 6 mm2: 7.5 mV x 20 A x 40 m = 6 V = 2.6087 percent of 230 V.
        CableSizingResult a = CableSizing.Compute("t", Tables, Circuit(vdLim: 3.0));
        Assert.Equal(6.0, a.SelectedSizeMm2);
        Assert.Equal(7.5 * 20 * 40 / 1000.0 / 230.0 * 100.0, a.Candidates.First(c => c.SizeMm2 == 6).VoltDropPercent!.Value, 12);
        CableSizingResult b = CableSizing.Compute("t", Tables, Circuit(vdLim: 2.0));
        Assert.Equal(10.0, b.SelectedSizeMm2);                                  // 6 mm2 gives 2.61 percent; 10 mm2 gives 1.53
        Assert.Equal(6.0, b.Checks.First(c => c.Name == "ampacity").MinimumSizeMm2);
    }

    [Fact]
    public void Voltage_drop_is_computed_from_resistance_and_reactance_when_no_mv_table_is_held()
    {
        string noMv = System.Text.RegularExpressions.Regex.Replace(Fixture, "\"voltDrop\": \\[.*?\\]\\}\\],", "\"voltDrop\": [],", System.Text.RegularExpressions.RegexOptions.Singleline);
        CableTables t = CableTables.Parse(noMv);
        Assert.Empty(t.VoltDrop);
        CableSizingResult r = CableSizing.Compute("t", t, Circuit(vdLim: 3.0));
        double sin = Math.Sqrt(1.0 - 0.64);
        double expected = 2.0 * 20 * 40 / 1000.0 * (3.1 * 0.8 + 0.08 * sin) / 230.0 * 100.0;   // single phase: K = 2
        Assert.Equal(expected, r.Candidates.First(c => c.SizeMm2 == 6).VoltDropPercent!.Value, 12);
        // Three phase: K = sqrt(3), 400 V.
        CableSizingResult r3 = CableSizing.Compute("t", t, Circuit(vdLim: 3.0, v: 400, phases: 3));
        Assert.Equal(Math.Sqrt(3.0) * 20 * 40 / 1000.0 * (3.1 * 0.8 + 0.08 * sin) / 400.0 * 100.0, r3.Candidates.First(c => c.SizeMm2 == 6).VoltDropPercent!.Value, 12);
    }

    [Fact]
    public void Adiabatic_check_sets_a_minimum_size_and_can_leave_no_size()
    {
        // S >= sqrt(I^2 t)/k : 1000 A, 0.2 s, k 100 -> 4.472 mm2 ; Ib 15 A needs 4 mm2 for ampacity, so 6 mm2 is selected.
        CableSizingResult r = CableSizing.Compute("t", Tables, Circuit(ib: 15, ifault: 1000, tfault: 0.2));
        Assert.Equal(Math.Sqrt(1000.0 * 1000.0 * 0.2) / 100.0, r.Checks.First(c => c.Name == "thermal").MinimumSizeMm2!.Value, 12);
        Assert.Equal(4.0, r.Checks.First(c => c.Name == "ampacity").MinimumSizeMm2);
        Assert.Equal(6.0, r.SelectedSizeMm2);
        // 3000 A for 0.5 s needs 21.2 mm2: nothing tabulated is large enough.
        CableSizingResult none = CableSizing.Compute("t", Tables, Circuit(ib: 15, ifault: 3000, tfault: 0.5));
        Assert.Equal("NO SIZE", none.Status);
        Assert.Null(none.SelectedSizeMm2);
    }

    [Fact]
    public void Empty_or_incomplete_tables_are_out_of_scope_and_nothing_is_guessed()
    {
        CableSizingResult e = CableSizing.Compute("t", CableTables.Empty("empty-file"), Circuit(amb: 30, grp: 2, vdLim: 3.0, ifault: 100, tfault: 1.0));
        Assert.Equal("OUT OF SCOPE", e.Status);
        Assert.Equal("EMPTY", e.TablesStatus);
        Assert.Null(e.SelectedSizeMm2);
        Assert.All(e.Checks, c => Assert.Equal("OUT OF SCOPE", c.Status));
        Assert.Contains(e.OutOfScope, s => s.Contains("empty-file"));
        // Ambient 32 C is not tabulated: no interpolation, so the quantity is OUT OF SCOPE.
        CableSizingResult i = CableSizing.Compute("t", Tables, Circuit(amb: 32));
        Assert.Equal("OUT OF SCOPE", i.Status);
        Assert.Contains(i.OutOfScope, s => s.Contains("no interpolation"));
        // An unknown installation method has no ampacity table.
        CableSizingResult m = CableSizing.Compute("t", Tables, Circuit() with { InstallationMethod = "Z9" });
        Assert.Equal("OUT OF SCOPE", m.Status);
        Assert.NotEmpty(m.Awaiting);
    }

    [Fact]
    public void Invalid_inputs_are_refused_and_filled_tables_need_provenance()
    {
        Assert.Equal("OUT OF SCOPE", CableSizing.Compute("t", Tables, Circuit(ib: 20, inA: 10)).Status);              // In below Ib
        Assert.Equal("OUT OF SCOPE", CableSizing.Compute("t", Tables, Circuit(ifault: 100)).Status);                  // fault without duration
        Assert.Throws<FormatException>(() => CableTables.Parse(Fixture.Replace("\"date\": \"2026-11-17\"", "\"date\": \"\"")));
        Assert.Throws<FormatException>(() => CableTables.Parse(Fixture.Replace("\"sizesMm2\": [1.5, 2.5, 4, 6, 10, 16], \"amps\": [10,", "\"sizesMm2\": [1.5, 2.5, 4, 6, 10], \"amps\": [10,")));
        Assert.Throws<FormatException>(() => CableTables.Parse("{\"schema\":\"gridsim-cable-tables/1\",\"id\":\"x\",\"bogus\":1}"));
        Assert.True(CableTables.Parse("{\"schema\":\"gridsim-cable-tables/1\",\"id\":\"x\"}").IsEmpty);
    }

    [Fact]
    public void Iec60287_basis_uses_the_existing_calculation_exactly()
    {
        CableConstruction Cab(double rdc, double dc, double di, double ds, double de) =>
            new(ConductorMaterial.Copper, rdc, 1.0, dc, di, ds, de, 3.5, 3.5, 2.5, 0.004, 90.0);
        var small = new Iec60287Candidate(400, Cab(0.0470, 23.3, 42.5, 44.5, 50.5));
        var large = new Iec60287Candidate(630, Cab(0.0283, 30.0, 50.0, 52.0, 58.0));
        var system = new CableSystem(18.0, 50.0);
        var inst = new BuriedInstallation(0.8, 1.0, 20.0);
        var screen = new ScreenLossInput(0.0, "test");
        double rA = Iec60287CableRating.Calculate(new CableRatingRequest("a", small.Cable, system, inst, screen, null, RatingSelection.Calculated)).CalculatedRatingA!.Value;
        double rB = Iec60287CableRating.Calculate(new CableRatingRequest("b", large.Cable, system, inst, screen, null, RatingSelection.Calculated)).CalculatedRatingA!.Value;
        Assert.True(rB > rA);
        var basis = new Iec60287Basis(new[] { large, small }, system, inst, screen);   // order given is not the order used
        var named = new[] { new NamedFactor("test group factor", 0.8, "test") };
        SizingCircuit Circ(double inA) => new(inA * 0.9, inA, 18000.0, 3, 100.0, 0.9, "copper", "xlpe", "buried", 3, null, null, named, null, null, null, basis);
        CableSizingResult hi = CableSizing.Compute("t", CableTables.Empty("none"), Circ(rA * 0.85));   // needs 1.0625 rA: the small size is short
        Assert.Equal("EVALUATED", hi.Status);
        Assert.Equal(630.0, hi.SelectedSizeMm2);
        Assert.Equal(rB * 0.8, hi.Candidates.First(c => c.SizeMm2 == 630).PermittedAmps!.Value, 9);
        Assert.Equal(rA, hi.Candidates.First(c => c.SizeMm2 == 400).TabulatedAmps!.Value, 9);
        Assert.Equal(400.0, CableSizing.Compute("t", CableTables.Empty("none"), Circ(rA * 0.7)).SelectedSizeMm2);
    }
}
