using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Events;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The event catalog: it round-trips through JSON, enforces the historical horizon on load (a future event is
/// rejected), and turns losses + recorded actions into ordered swing events.
/// </summary>
public class EventCatalogTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Builtins_round_trip_through_json()
    {
        IReadOnlyList<GridEvent> original = EventCatalog.Builtin(Now);
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-cat-{Guid.NewGuid():N}.json");
        try
        {
            EventCatalog.Save(path, original);
            IReadOnlyList<GridEvent> loaded = EventCatalog.Load(path, Now);

            Assert.Equal(original.Count, loaded.Count);
            for (int i = 0; i < original.Count; i++)
            {
                Assert.Equal(original[i].Id, loaded[i].Id);
                Assert.Equal(original[i].WhenUtc, loaded[i].WhenUtc);
                Assert.Equal(original[i].PreEventInertiaMwS, loaded[i].PreEventInertiaMwS);
                Assert.Equal(original[i].Losses.Count, loaded[i].Losses.Count);
                Assert.Equal(original[i].Outcome.NadirHz, loaded[i].Outcome.NadirHz);
                Assert.Equal(original[i].Provenance, loaded[i].Provenance); // enum survives as string
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_future_dated_event_is_rejected_on_load()
    {
        var future = new List<GridEvent>
        {
            new("future", "should not load", Now.AddDays(1), 100_000, 30_000,
                new List<PowerStep> { new(1.0, -1000, "x", ProvenanceTag.Synthetic) },
                new List<RecordedAction>(),
                new RecordedOutcome(-0.25, 49.3, 49.8, 0, null, ProvenanceTag.Synthetic),
                ProvenanceTag.Synthetic),
        };
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-fut-{Guid.NewGuid():N}.json");
        try
        {
            EventCatalog.Save(path, future);
            Assert.Throws<ForwardInferenceException>(() => EventCatalog.Load(path, Now));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ToFrequencyEvents_merges_losses_and_actions_in_time_order()
    {
        GridEvent ev = EventCatalog.Get("gb-2019-08-09", utcNow: Now);
        var fe = ev.ToFrequencyEvents();

        Assert.Equal(ev.Losses.Count + ev.RecordedActions.Count, fe.Count);
        for (int i = 1; i < fe.Count; i++)
            Assert.True(fe[i].AtSeconds >= fe[i - 1].AtSeconds, "events must be time-ordered");
        // The recorded LFDD action is a POSITIVE step (load removed), among negative losses.
        Assert.Contains(fe, e => e.DeltaPMw > 0);
        Assert.Contains(fe, e => e.DeltaPMw < 0);
    }

    [Fact]
    public void Get_is_case_insensitive_and_unknown_ids_throw()
    {
        Assert.Equal("gb-2019-08-09", EventCatalog.Get("GB-2019-08-09", utcNow: Now).Id);
        Assert.Throws<KeyNotFoundException>(() => EventCatalog.Get("no-such-event", utcNow: Now));
    }
}
