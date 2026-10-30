using System.Numerics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// N234 (October 2026, LF2; JSON v2 phase P5 opened for fault sequence data ONLY, ruled by main under Mark's
/// delegation, Ruling R1): a v2 case can carry generator X''d / X2 and transformer windings, zero-sequence impedance and
/// neutral reactance, so an earth fault from a case no longer exits 3 for want of data the case could not hold. Every
/// test here is new.
/// </summary>
public sealed partial class V2CaseLoaderTests
{
    /// <summary>A two-bus earth-fault case: a 400 kV slack machine, a 400/132 kV transformer (delta HV, earthed star LV
    /// through a neutral reactance) and a 132 kV demand. Every sequence field is present.</summary>
    private const string N234Network = """
        {
          "schema": "gridsim-network/2",
          "buses": [
            { "id": 1, "name": "HV", "baseKv": 400, "type": "slack" },
            { "id": 2, "name": "LV", "baseKv": 132 }
          ],
          "generators": [
            { "name": "G", "bus": 1, "type": "ccgt", "capacityMva": 1000, "outputMw": 0, "role": "slack",
              "subtransientXdPu": 0.18, "negativeSeqXPu": 0.22 }
          ],
          "demands": [ { "name": "D", "bus": 2, "pMw": 100, "qMvar": 20 } ],
          "transformers": [
            { "name": "T", "from": 1, "to": 2, "rPu": 0.002, "xPu": 0.05, "ratingMva": 500,
              "windings": { "from": "Delta", "to": "WyeGrounded" }, "r0Pu": 0.001, "x0Pu": 0.045, "neutralXPu": { "to": 0.01 } }
          ]
        }
        """;

    /// <summary>The same case with every sequence field removed (written out in full, so no text surgery is needed).</summary>
    private const string N234NetworkPlain = """
        {
          "schema": "gridsim-network/2",
          "buses": [
            { "id": 1, "name": "HV", "baseKv": 400, "type": "slack" },
            { "id": 2, "name": "LV", "baseKv": 132 }
          ],
          "generators": [
            { "name": "G", "bus": 1, "type": "ccgt", "capacityMva": 1000, "outputMw": 0, "role": "slack" }
          ],
          "demands": [ { "name": "D", "bus": 2, "pMw": 100, "qMvar": 20 } ],
          "transformers": [
            { "name": "T", "from": 1, "to": 2, "rPu": 0.002, "xPu": 0.05, "ratingMva": 500 }
          ]
        }
        """;

    private static string N234WithoutSequence() => N234NetworkPlain;

    private const string N234Manifest = """{ "schema": "gridsim-case/2", "name": "Earth fault", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["network.json"] }""";

    /// <summary>Normalise the raw string's line ends (the file may be checked out with CRLF), so a Replace pattern with a
    /// "\n" matches on any checkout.</summary>
    private static string N234Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void N234_the_sequence_data_reaches_the_model()
    {
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        GridModel m = f.Load().Model;
        Generator g = m.Generators.Single(x => x.Name == "G");
        Assert.Equal(0.18, g.SubtransientXdPu);
        Assert.Equal(0.22, g.NegativeSeqXPu);
        Branch t = m.Branches.Single(b => b.Name == "T");
        Assert.True(t.IsTransformer);
        Assert.Equal(WindingConnection.Delta, t.FromWinding);
        Assert.Equal(WindingConnection.WyeGrounded, t.ToWinding);
        Assert.Equal(0.001, t.R0);
        Assert.Equal(0.045, t.X0);
        Assert.Equal(0.0, t.FromNeutralXPu);
        Assert.Equal(0.01, t.ToNeutralXPu);
    }

    [Fact]
    public void N234_the_sequence_data_changes_nothing_else_in_the_model()
    {
        // The case with and without the sequence fields: once those fields are put back to their defaults, the two models
        // are equal in every field (doubles by their bits, by reflection: DeepDifferences), and their power flows are
        // equal to the bit. Sequence data is fault data only; it never reaches the power flow.
        string without = N234Lf(N234WithoutSequence());
        Assert.DoesNotContain("subtransientXdPu", without, StringComparison.Ordinal);
        Assert.DoesNotContain("windings", without, StringComparison.Ordinal);
        using var a = new Fixture(N234Manifest, N234Lf(N234Network));
        using var b = new Fixture(N234Manifest, without);
        GridModel with = a.Load().Model, plain = b.Load().Model;
        GridModel reverted = with.CopyWith(
            generators: with.AllGenerators.Select(x => x with { SubtransientXdPu = 0, NegativeSeqXPu = 0 }).ToList(),
            branches: with.Branches.Select(x => x with { R0 = 0, X0 = 0, FromWinding = null, ToWinding = null, FromNeutralXPu = 0, ToNeutralXPu = 0 }).ToList());
        List<string> diffs = DeepDifferences(plain, reverted);
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
        AssertSameSolve(SolverSelector.CreateNewton(plain).Solve(plain), SolverSelector.CreateNewton(with).Solve(with), "N234 two-bus");
    }

    [Fact]
    public void N234_an_earth_fault_on_the_loaded_case_sees_the_earthed_neutral_and_its_data()
    {
        // The earth path the case supplies: the LV star point, earthed through 0.01 pu. Delta HV / earthed-star LV puts a
        // shunt to earth at bus 2 of Z0T + 3 Xn = (0.001 + j0.045) + j0.03 (IEC 60909-0:2016, the zero-sequence system
        // of a Dyn transformer seen from its star side; PSS/E and PowerFactory model the same connection code). Bus 2's
        // zero-sequence network is that one shunt, so the driving-point Z0 is exactly its impedance (to rounding).
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        GridModel m = f.Load().Model;
        PowerFlowResult pre = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(pre.Converged);
        UnbalancedFaultResult slg = FaultAnalysis.Unbalanced(m, pre, 2, FaultType.SingleLineToGround);
        Assert.True(slg.FaultBusGrounded);
        var expectedZ0 = new Complex(0.001, 0.045 + 3.0 * 0.01);
        Assert.True(Complex.Abs(slg.Z0Pu - expectedZ0) <= 1e-14, $"Z0 {slg.Z0Pu} against {expectedZ0}");
        // Series connection of the three sequence networks: |3 I0| = 3 |Vf| / |Z1 + Z2 + Z0| (bolted fault).
        int k = pre.BusIds.ToList().IndexOf(2);
        double vf = pre.Vm[k];
        double expectedEarth = 3.0 * vf / Complex.Abs(slg.Z1Pu + slg.Z2Pu + slg.Z0Pu);
        Assert.True(Math.Abs(slg.EarthCurrentPu - expectedEarth) <= 1e-12 * expectedEarth, $"earth current {slg.EarthCurrentPu} against {expectedEarth}");
        Assert.True(slg.EarthCurrentPu > 1.0, $"a bolted earth fault behind 0.075 pu draws several pu, got {slg.EarthCurrentPu}");

        // Without the sequence fields the same bus has no earth path (the N116 missing-data case), and the X''d / X2
        // the case gives make Z1 / Z2 differ from the synthesised 0.20 pu defaults.
        using var p = new Fixture(N234Manifest, N234Lf(N234WithoutSequence()));
        GridModel plain = p.Load().Model;
        UnbalancedFaultResult none = FaultAnalysis.Unbalanced(plain, SolverSelector.CreateNewton(plain).Solve(plain), 2, FaultType.SingleLineToGround);
        Assert.False(none.FaultBusGrounded);
        Assert.Equal(0.0, none.EarthCurrentPu);
        Assert.NotEqual(none.Z1Pu, slg.Z1Pu);
        Assert.NotEqual(none.Z2Pu, slg.Z2Pu);
    }

    [Fact]
    public void N234_iec60909_earth_fault_from_a_v2_case_with_windings_no_longer_exits_3()
    {
        // The CLI behaviour the ruling changes: before N234 the only way to load this case was without its windings, and
        // the earth fault exited 3 ("carries no transformer winding connections at all"). With the windings it runs.
        using var f = new Fixture(N234Manifest, N234Lf(N234Network));
        CliProcess.Result r = CliProcess.RunBuilt("iec60909", f.ManifestPath, "--bus", "2", "--type", "slg");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("winding connections", r.StdErr, StringComparison.Ordinal);

        // And the same case without them still fails loud, exactly as N116 pinned for the bundled cases.
        using var p = new Fixture(N234Manifest, N234Lf(N234WithoutSequence()));
        CliProcess.Result q = CliProcess.RunBuilt("iec60909", p.ManifestPath, "--bus", "2", "--type", "slg");
        Assert.True(q.ExitCode == 3, q.ToString());
        Assert.Contains("carries no transformer winding connections at all", q.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void N234_sequence_data_on_a_converter_unit_is_refused()
    {
        // The fixture's wind unit (type wind-onshore: a converter type) given an X''d, and a unit declared
        // converter-interfaced given an X2: neither value would ever be read (IEC 60909-0:2016 models a converter as a
        // current source), so both are refused, each named.
        string network = Network
            .Replace("\"role\": \"pq\" }", "\"role\": \"pq\", \"subtransientXdPu\": 0.2 }", StringComparison.Ordinal)
            .Replace("\"role\": \"slack\" }", "\"role\": \"slack\", \"converter\": { \"interfaced\": true }, \"negativeSeqXPu\": 0.2 }", StringComparison.Ordinal);
        using var f = new Fixture(network: network);
        string msg = f.Refusal();
        Assert.Contains("2 problem(s)", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'Wind' is converter-interfaced (its type 'wind-onshore' is a converter type)", msg, StringComparison.Ordinal);
        Assert.Contains("unit 'Slack' is converter-interfaced, so the fault studies", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void N234_inconsistent_sequence_data_is_refused_and_every_problem_is_listed()
    {
        // Three transformers, each wrong in its own way; every problem is listed in one refusal.
        string network = Network.Replace("\"ratingMva\": 1000 } ]", """
            "ratingMva": 1000 } ],
              "transformers": [
                { "name": "NoWindings", "from": 1, "to": 2, "rPu": 0.001, "xPu": 0.05, "x0Pu": 0.04 },
                { "name": "R0Alone", "from": 1, "to": 2, "rPu": 0.001, "xPu": 0.05, "windings": { "from": "WyeGrounded", "to": "Delta" }, "r0Pu": 0.001 },
                { "name": "DeltaNeutral", "from": 1, "to": 2, "rPu": 0.001, "xPu": 0.05, "windings": { "from": "WyeGrounded", "to": "Delta" }, "neutralXPu": { "to": 0.02 } } ]
            """, StringComparison.Ordinal);
        using var f = new Fixture(network: network);
        string msg = f.Refusal();
        Assert.Contains("3 problem(s)", msg, StringComparison.Ordinal);
        Assert.Contains("/transformers/0: r0Pu / x0Pu / neutralXPu are given without windings", msg, StringComparison.Ordinal);
        Assert.Contains("/transformers/1/r0Pu: given without x0Pu", msg, StringComparison.Ordinal);
        Assert.Contains("/transformers/2/neutralXPu/to: the to winding is Delta, which has no earthed neutral", msg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"x0Pu\": 0")]
    [InlineData("\"x0Pu\": -0.01")]
    [InlineData("\"x0Pu\": 0.04, \"r0Pu\": -0.001")]
    public void N234_a_zero_sequence_impedance_out_of_range_is_refused_by_the_schema(string members)
    {
        // The schema itself bounds them now (x0Pu > 0, r0Pu >= 0), so `validate` reports it too.
        string network = Network.Replace("\"ratingMva\": 1000 } ]", $$"""
            "ratingMva": 1000 } ],
              "transformers": [ { "name": "T", "from": 1, "to": 2, "rPu": 0.001, "xPu": 0.05, "windings": { "from": "WyeGrounded", "to": "Delta" }, {{members}} } ]
            """, StringComparison.Ordinal);
        using var f = new Fixture(network: network);
        string msg = f.Refusal();
        Assert.Contains("schema checks", msg, StringComparison.Ordinal);
        Assert.Contains("/transformers/0", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void N234_an_out_of_service_unit_before_the_data_keeps_the_fleet_order_and_the_match()
    {
        // The data is matched by position in the network file and checked by name. An out-of-service unit written FIRST
        // is the case that would expose an off-by-one (the dispatch re-appends out-of-service units at the end): the X''d
        // must land on 'G' and the fleet keep the file order.
        string network = N234Lf(N234Network).Replace("\"generators\": [", """
            "generators": [
                { "name": "Spare", "bus": 2, "type": "ocgt", "capacityMva": 50, "outputMw": 0, "inService": false },
            """, StringComparison.Ordinal);
        using var f = new Fixture(N234Manifest, network);
        GridModel m = f.Load().Model;
        Generator g = m.AllGenerators.Single(x => x.Name == "G");
        Generator spare = m.AllGenerators.Single(x => x.Name == "Spare");
        Assert.Equal(0.18, g.SubtransientXdPu);
        Assert.Equal(0.0, spare.SubtransientXdPu);
        Assert.False(spare.InService);
    }

    [Fact]
    public void N234_the_out_of_service_refusal_names_the_phase_still_to_come()
    {
        // The P5 reason text now says P5 is open for sequence data only.
        using var f = new Fixture(network: Network.Replace("\"ratingMva\": 1000 }", "\"ratingMva\": 1000, \"inService\": false }", StringComparison.Ordinal));
        string msg = f.Refusal();
        Assert.Contains("an out-of-service line", msg, StringComparison.Ordinal);
        // Moved 2026-10 (DOC-17 D-DOC-162, Ruling R1, Mark delegated 2026-10-04): the reason quoted plan wording. Before:
        // "(network fields other than fault sequence data reach the model in a later, Mark-gated phase of JSON v2 (P5 is
        // open for sequence data only))". After: "(of the network fields v1 cannot hold, the v2 loader reads only fault
        // sequence data so far)". Same fact (sequence data is the one part open), said for a user.
        Assert.Contains("(of the network fields v1 cannot hold, the v2 loader reads only fault sequence data so far)", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark-gated", msg, StringComparison.Ordinal);
    }
}
