using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-842: the order of an event's steps at equal times is a rule, pinned here: steps are ordered by time; at an equal
/// time every loss comes before every recorded action, and each keeps its order in the catalogue. A replay's nadir
/// depends on it (a loss and an LFDD shed at the same second), so a change of sort must not move it.
/// </summary>
public sealed class GridEventOrderingTests
{
    private static GridEvent Event(IReadOnlyList<PowerStep> losses, IReadOnlyList<RecordedAction> actions) => new(
        "syn-order", "ordering", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 150_000, 30_000,
        losses, actions, new RecordedOutcome(0.1, 49.5, 49.8, 0, null, ProvenanceTag.Synthetic), ProvenanceTag.Synthetic);

    [Fact]
    public void At_equal_times_losses_come_first_then_actions_each_in_catalogue_order()
    {
        GridEvent ev = Event(
            new List<PowerStep>
            {
                new(1.0, -100.0, "loss A", ProvenanceTag.Synthetic),
                new(0.5, -10.0, "early", ProvenanceTag.Synthetic),
                new(1.0, -200.0, "loss B", ProvenanceTag.Synthetic),
            },
            new List<RecordedAction>
            {
                new(1.0, +50.0, "action A", ProvenanceTag.Synthetic),
                new(1.0, +60.0, "action B", ProvenanceTag.Synthetic),
            });

        IReadOnlyList<FrequencyEvent> steps = ev.ToFrequencyEvents();
        Assert.Equal(new[] { 0.5, 1.0, 1.0, 1.0, 1.0 }, steps.Select(s => s.AtSeconds));
        Assert.Equal(new[] { -10.0, -100.0, -200.0, 50.0, 60.0 }, steps.Select(s => s.DeltaPMw));
    }
}
