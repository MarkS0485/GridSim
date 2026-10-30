using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The sequence-of-events cross-reference (<see cref="SequenceOfEvents"/>): a predicted protection cascade
/// reconciled against a real timestamped alarm log. Agreement where the physics reproduced a trip;
/// divergences - a predicted trip that never fired, a real trip the model missed, or a matched pair out of
/// the predicted order - are where the model is missing something.
/// </summary>
public class SequenceOfEventsTests
{
    private static CascadeResult Predicted() => new(
        new[]
        {
            new CascadeStep(1, "51", "Time overcurrent", "L12", From: 1, To: 2, TripTimeS: 2.4, Reason: "118% of RateA", ResolveConverged: true, OverloadedBranchesAfter: 1),
            new CascadeStep(2, "49", "Thermal overload", "L34", From: 3, To: 4, TripTimeS: 240, Reason: "105% of RateA", ResolveConverged: true, OverloadedBranchesAfter: 0),
        },
        FinalConverged: true, StopReason: "settled");

    [Fact]
    public void An_alarm_log_matching_the_prediction_is_fully_consistent()
    {
        var observed = new List<AlarmEvent>
        {
            new(AtSeconds: 2.4, Device: "51", From: 1, To: 2),
            new(AtSeconds: 240, Device: "49", From: 3, To: 4),
        };
        SoeCrossReference x = SequenceOfEvents.CrossReference(Predicted(), observed);
        Assert.True(x.FullyConsistent);
        Assert.Equal(2, x.Agreements);
        Assert.Empty(x.Divergences);
    }

    [Fact]
    public void A_real_trip_the_model_did_not_predict_is_flagged_as_the_blind_spot()
    {
        var observed = new List<AlarmEvent>
        {
            new(AtSeconds: 2.4, Device: "51", From: 1, To: 2),
            new(AtSeconds: 50, Device: "50", From: 5, To: 6, Description: "unexpected instantaneous trip"),
            new(AtSeconds: 240, Device: "49", From: 3, To: 4),
        };
        SoeCrossReference x = SequenceOfEvents.CrossReference(Predicted(), observed);
        Assert.False(x.FullyConsistent);
        Assert.Equal(2, x.Agreements);
        Assert.Equal(1, x.ObservedNotPredicted);
        SoeEntry blind = Assert.Single(x.Divergences);
        Assert.Equal(SoeMatchKind.ObservedNotPredicted, blind.Kind);
        Assert.Equal((5, 6), (blind.From, blind.To));
    }

    [Fact]
    public void A_predicted_trip_absent_from_the_log_is_flagged()
    {
        var observed = new List<AlarmEvent> { new(AtSeconds: 2.4, Device: "51", From: 1, To: 2) };
        SoeCrossReference x = SequenceOfEvents.CrossReference(Predicted(), observed);
        Assert.Equal(1, x.Agreements);
        Assert.Equal(1, x.PredictedNotObserved);
        Assert.Contains(x.Divergences, e => e.Kind == SoeMatchKind.PredictedNotObserved && e.From == 3 && e.To == 4);
    }

    [Fact]
    public void Matched_trips_in_the_wrong_order_are_flagged()
    {
        // The thermal (L34) alarm arrives BEFORE the overcurrent (L12) alarm - the reverse of the predicted
        // order (overcurrent first). Both branches match, but the sequence diverged.
        var observed = new List<AlarmEvent>
        {
            new(AtSeconds: 1.0, Device: "49", From: 3, To: 4),
            new(AtSeconds: 5.0, Device: "51", From: 1, To: 2),
        };
        SoeCrossReference x = SequenceOfEvents.CrossReference(Predicted(), observed);
        Assert.Equal(1, x.OrderMismatches);
        Assert.Contains(x.Divergences, e => e.Kind == SoeMatchKind.OrderMismatch);
        Assert.False(x.FullyConsistent);
    }

    [Fact]
    public void Non_branch_alarms_are_ignored_by_the_branch_cross_reference()
    {
        var observed = new List<AlarmEvent>
        {
            new(AtSeconds: 0.5, Device: "81U", From: 0, To: 0, Description: "underfrequency (a bus alarm)"),
            new(AtSeconds: 2.4, Device: "51", From: 1, To: 2),
            new(AtSeconds: 240, Device: "49", From: 3, To: 4),
        };
        SoeCrossReference x = SequenceOfEvents.CrossReference(Predicted(), observed);
        Assert.True(x.FullyConsistent); // the frequency alarm doesn't create a spurious ObservedNotPredicted
        Assert.Equal(2, x.Agreements);
    }
}
