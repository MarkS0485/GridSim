using System.Globalization;
using System.Text;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (run journal), the claim check. A change that matches a breaking-change ID is EXPLAINED only when it lies within
/// what that change claims: a numeric bound, an exact transform, or a qualitative transition. Outside it, EXCEEDS-CLAIM
/// (so a second regression in the same field cannot hide behind the ID); claims of several IDs on one field add when
/// they are all bounds and are AMBIGUOUS otherwise; a field no claim covers is UNEXPLAINED.
/// </summary>
public sealed class BreakingChangeClaimTests
{
    private const string Map = """
        {"schema":"gridsim-breaking-changes/1","rule":"test","entries":[
         {"id":"B1","version":"2.1.080","kind":"bug-fix","changelog":"2.1.080","title":"angles","magnitude":"",
          "scope":{"verbs":["dynamics-study"],"options":[],"inputFormats":[],"condition":""},
          "claims":[{"field":"*angle*","kind":"bound","maxAbs":1e-4},{"field":"*p_mw*","kind":"bound","maxRel":0.01}]},
         {"id":"B2","version":"2.1.081","kind":"bug-fix","changelog":"2.1.081","title":"angles again","magnitude":"",
          "scope":{"verbs":["dynamics-study"],"options":[],"inputFormats":[],"condition":""},
          "claims":[{"field":"*angle*","kind":"bound","maxAbs":2e-4}]},
         {"id":"B3","version":"2.1.082","kind":"bug-fix","changelog":"2.1.082","title":"sign","magnitude":"",
          "scope":{"verbs":["pf","dcpf"],"options":[],"inputFormats":[],"condition":""},
          "claims":[{"field":"*vsc_mw*","kind":"transform","factor":-1},{"field":"*x_pu*","kind":"transform","factorMin":0.81,"factorMax":1.21}]},
         {"id":"B4","version":"2.1.083","kind":"bug-fix","changelog":"2.1.083","title":"status","magnitude":"",
          "scope":{"verbs":["acscopf","dcpf"],"options":[],"inputFormats":[],"condition":""},
          "claims":[{"field":"*status*","kind":"qualitative","from":"Optimal","to":"SecuredOutageFailsScreen"},
                    {"field":"(exit code)","kind":"qualitative","from":"0","to":"3"},
                    {"field":"*vsc_mw*","kind":"bound","maxAbs":1000}]}]}
        """;

    private static readonly BreakingChangeMap M = BreakingChangeMap.Parse(Encoding.UTF8.GetBytes(Map));

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // A re-run of a recorded result: the field differences of two small result documents.
    private static ReplayOutcome Changed(string verb, string recorded, string replay, string recordedVersion = "2.1.079")
    {
        FileComparison c = ResultCompare.Compare("r.json", Encoding.UTF8.GetBytes(recorded), Encoding.UTF8.GetBytes(replay), verb,
            s => s, s => s, VolatileFields.Default);
        var o = new ReplayOutcome { Seq = 1, Verb = verb, RecordedVersion = recordedVersion, Status = ReplayStatus.Changed };
        o.Fields.AddRange(c.Fields);
        M.Judge(o, M.Explain(recordedVersion, "2.1.104", verb, [verb], []));
        return o;
    }

    [Fact]
    public void A_change_inside_its_bound_is_explained_and_past_it_exceeds_the_claim()
    {
        // Recorded on 2.1.079: B1 (2.1.080) and B2 (2.1.081) are both in the window; only B1 claims p_mw.
        ReplayOutcome inside = Changed("dynamics-study", """{"rotor_angle_deg":[10.0,20.0],"p_mw":[100.0]}""",
            $$"""{"rotor_angle_deg":[{{N(10.0 + 5e-5)}},20.0],"p_mw":[100.5]}""", "2.1.079");
        Assert.Equal(ClaimVerdict.Explained, inside.Verdict);
        Assert.Equal(new[] { "B1", "B2" }, inside.ExplainedBy);
        Assert.False(inside.Flagged);

        // The same field pushed past its bound: B1 + B2 allow 1e-4 + 2e-4 = 3e-4 in angle; 4e-4 is beyond.
        ReplayOutcome beyond = Changed("dynamics-study", """{"rotor_angle_deg":[10.0,20.0],"p_mw":[100.0]}""",
            $$"""{"rotor_angle_deg":[{{N(10.0 + 4e-4)}},20.0],"p_mw":[100.5]}""", "2.1.079");
        Assert.Equal(ClaimVerdict.ExceedsClaim, beyond.Verdict);
        Assert.True(beyond.Flagged);
        FieldDifference angle = beyond.Fields.Single(f => f.Field == "$.rotor_angle_deg[*]");
        Assert.Equal(ClaimVerdict.ExceedsClaim, angle.Verdict);
        Assert.Equal("B1;B2", angle.ExplainedBy);
        Assert.StartsWith("abs<=" + N(1e-4 + 2e-4) + " (sum of B1 abs<=0.0001; B2 abs<=0.0002)", angle.Claimed, StringComparison.Ordinal);
        Assert.StartsWith("abs " + N(Math.Abs(10.0 + 4e-4 - 10.0)), angle.Observed, StringComparison.Ordinal);

        // Recorded on 2.1.080: only B2 (2e-4) is in the window, so 1.5e-4 is inside and 2.5e-4 exceeds.
        Assert.Equal(ClaimVerdict.Explained, Changed("dynamics-study", """{"rotor_angle_deg":[10.0]}""", $$"""{"rotor_angle_deg":[{{N(10.0 + 1.5e-4)}}]}""", "2.1.080").Verdict);
        Assert.Equal(ClaimVerdict.ExceedsClaim, Changed("dynamics-study", """{"rotor_angle_deg":[10.0]}""", $$"""{"rotor_angle_deg":[{{N(10.0 + 2.5e-4)}}]}""", "2.1.080").Verdict);
        // A relative bound: 1 % of 100 MW.
        Assert.Equal(ClaimVerdict.ExceedsClaim, Changed("dynamics-study", """{"p_mw":[100.0]}""", """{"p_mw":[102.0]}""").Verdict);
    }

    [Fact]
    public void A_predicted_transform_is_checked_value_by_value()
    {
        // An exact sign flip matches factor -1 (a pf run: B3 is the one change in scope).
        ReplayOutcome flip = Changed("pf", """{"vsc_mw":[100.0,-25.5]}""", """{"vsc_mw":[-100.0,25.5]}""", "2.1.081");
        Assert.Equal(ClaimVerdict.Explained, flip.Verdict);
        Assert.Equal(new[] { "B3" }, flip.ExplainedBy);
        // One value that is not the flip: the transform does not hold.
        ReplayOutcome notFlip = Changed("pf", """{"vsc_mw":[100.0,-25.5]}""", """{"vsc_mw":[-100.0,25.0]}""", "2.1.081");
        Assert.Equal(ClaimVerdict.ExceedsClaim, notFlip.Verdict);
        Assert.Contains("new/old [", notFlip.Fields[0].Observed, StringComparison.Ordinal);
        // A factor range (w2^2 for w2 in 0.9 to 1.1): 1.1025 holds, 1.3 does not, and a value from 0 has no ratio.
        Assert.Equal(ClaimVerdict.Explained, Changed("pf", """{"x_pu":[0.1]}""", $$"""{"x_pu":[{{N(0.1 * 1.1025)}}]}""", "2.1.081").Verdict);
        Assert.Equal(ClaimVerdict.ExceedsClaim, Changed("pf", """{"x_pu":[0.1]}""", """{"x_pu":[0.13]}""", "2.1.081").Verdict);
        Assert.Equal(ClaimVerdict.ExceedsClaim, Changed("pf", """{"x_pu":[0.0]}""", """{"x_pu":[0.1]}""", "2.1.081").Verdict);
    }

    [Fact]
    public void A_qualitative_claim_checks_the_transition_itself()
    {
        ReplayOutcome ok = Changed("acscopf", """{"status":"Optimal"}""", """{"status":"SecuredOutageFailsScreen"}""", "2.1.082");
        Assert.Equal(ClaimVerdict.Explained, ok.Verdict);
        Assert.Equal("Optimal->SecuredOutageFailsScreen", ok.Fields[0].Transition);
        ReplayOutcome wrong = Changed("acscopf", """{"status":"Optimal"}""", """{"status":"NotConverged"}""", "2.1.082");
        Assert.Equal(ClaimVerdict.ExceedsClaim, wrong.Verdict);
        Assert.Equal("Optimal->NotConverged", wrong.Fields[0].Observed);
        Assert.Equal("B4 Optimal->SecuredOutageFailsScreen", wrong.Fields[0].Claimed);
        // The exit code is checked the same way.
        var exit = new ReplayOutcome { Seq = 1, Verb = "acscopf", RecordedVersion = "2.1.082", Status = ReplayStatus.Changed };
        exit.Fields.Add(new FieldDifference { File = "(run)", Field = "(exit code)", Kind = "exit", MinAgreeingBits = 0, Count = 1, Transition = "0->3" });
        M.Judge(exit, M.Explain("2.1.082", "2.1.104", "acscopf", ["acscopf"], []));
        Assert.Equal(ClaimVerdict.Explained, exit.Verdict);
        var exit2 = new ReplayOutcome { Seq = 1, Verb = "acscopf", RecordedVersion = "2.1.082", Status = ReplayStatus.Changed };
        exit2.Fields.Add(new FieldDifference { File = "(run)", Field = "(exit code)", Kind = "exit", MinAgreeingBits = 0, Count = 1, Transition = "0->1" });
        M.Judge(exit2, M.Explain("2.1.082", "2.1.104", "acscopf", ["acscopf"], []));
        Assert.Equal(ClaimVerdict.ExceedsClaim, exit2.Verdict);
    }

    [Fact]
    public void Claims_that_cannot_compose_are_ambiguous_and_an_uncovered_field_is_unexplained()
    {
        // vsc_mw on a dcpf run recorded on 2.1.081: B3 claims a sign flip and B4 a bound. A transform does not add to a
        // bound, so the field is AMBIGUOUS, never EXPLAINED.
        ReplayOutcome both = Changed("dcpf", """{"vsc_mw":[100.0]}""", """{"vsc_mw":[-100.0]}""", "2.1.081");
        Assert.Equal(ClaimVerdict.Ambiguous, both.Verdict);
        Assert.Equal("B3;B4", both.Fields[0].ExplainedBy);
        Assert.True(both.Flagged);
        // A field none of the in-window changes claims.
        ReplayOutcome other = Changed("dynamics-study", """{"rotor_angle_deg":[10.0],"q_mvar":[5.0]}""", $$"""{"rotor_angle_deg":[{{N(10.0 + 1e-5)}}],"q_mvar":[6.0]}""");
        Assert.Equal(ClaimVerdict.Unexplained, other.Verdict);
        Assert.True(other.Unexplained);
        Assert.Equal(ClaimVerdict.Explained, other.Fields.Single(f => f.Field == "$.rotor_angle_deg[*]").Verdict);
        // The console names the flagged field with what was observed and what was claimed.
        ReplayOutcome beyond = Changed("dynamics-study", """{"rotor_angle_deg":[10.0]}""", $$"""{"rotor_angle_deg":[{{N(10.5)}}]}""");
        string text = ReplayReport.ConsoleText([beyond], "2.1.104");
        Assert.Contains("unexplained=0 exceeds-claim=1 ambiguous=0", text, StringComparison.Ordinal);
        Assert.Contains("EXCEEDS-CLAIM of B1; B2: r.json $.rotor_angle_deg[*] observed abs 0.5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_map_carries_complete_claims()
    {
        foreach (BreakingChange b in BreakingChangeMap.Default.Entries)
            foreach (BreakingChangeClaim c in b.Claims)
                Assert.Contains(c.Kind, new[] { "bound", "transform", "qualitative" });
        // The G4 angle bound is the measured worst movement in the CHANGELOG (1.9e-4 degrees, case118).
        BreakingChangeClaim g4 = BreakingChangeMap.Default.Entries.Single(b => b.Id == "B26").Claims.First();
        Assert.Equal(1.9e-4, g4.MaxAbs);
        // An incomplete claim is refused when the map is read.
        string bad = Map.Replace("\"kind\":\"bound\",\"maxAbs\":2e-4", "\"kind\":\"bound\"", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => BreakingChangeMap.Parse(Encoding.UTF8.GetBytes(bad)));
    }
}
