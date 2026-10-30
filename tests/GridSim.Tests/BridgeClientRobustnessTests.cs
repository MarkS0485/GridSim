using System;
using System.Collections.Generic;
using System.IO;
using GridSim.Core.Diagnostics;
using GridSim.Core.Net;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The bridge client's robustness and time-handling contract:
/// <list type="bullet">
/// <item>R275: one unreadable message is reported and skipped; it no longer ends the receive loop.</item>
/// <item>R276: the horizon is the exact 169 h, with no 15-minute clock-skew allowance.</item>
/// <item>R277: an Unspecified DateTime is UTC by contract on the wire and in the audit-log hash.</item>
/// </list>
/// </summary>
public class BridgeClientRobustnessTests
{
    private const string TopologyJson = """
    {
      "type": "topology",
      "model": {
        "name": "toy2", "baseMva": 100,
        "buses": [ { "id": 1, "type": "Slack", "baseKv": 400 }, { "id": 2, "type": "Pq", "baseKv": 400, "pd": 10 } ],
        "generators": [ { "bus": 1, "pg": 10 } ],
        "branches": [ { "from": 1, "to": 2, "r": 0.01, "x": 0.1, "name": "L1-2" } ]
      }
    }
    """;

    private static string StateJson(string t) => $$"""
    {
      "type": "state", "t": "{{t}}",
      "result": { "buses": [ { "id": 1, "vm": 1.0, "vaDeg": 0.0 }, { "id": 2, "vm": 0.99, "vaDeg": -1.0 } ], "lineFlows": [] }
    }
    """;

    private static string AgoIso(TimeSpan age) => (DateTime.UtcNow - age).ToString("o");
    private static string HistoricalIso() => AgoIso(TimeSpan.FromHours(ForwardInferenceGuard.HorizonHours + 24));

    // ---- R275: per-message rejection ----------------------------------------------------------------

    [Fact]
    public void A_malformed_message_is_rejected_and_counted_and_the_stream_carries_on()
    {
        var client = new GridBridgeClient();
        var rejected = new List<BridgeMessageException>();
        var frames = new List<SimFrame>();
        client.MessageRejected += rejected.Add;
        client.FrameReceived += frames.Add;

        client.DispatchGuarded(TopologyJson);
        client.DispatchGuarded("{ \"type\": \"state\", \"t\": ");   // truncated JSON
        client.DispatchGuarded(StateJson(HistoricalIso()));          // the next good frame still lands

        Assert.Single(rejected);
        Assert.Equal(1, client.RejectedMessages);
        // Test changed 2026-10-19: since GR-632 PeekType scans the WHOLE top level (to refuse a duplicate "type" key),
        // so the truncation now fails the peek itself rather than the typed read after it. The message is still
        // rejected, counted and skipped; the inner exception is Dispatch's InvalidDataException for an unreadable message.
        Assert.IsType<InvalidDataException>(rejected[0].InnerException);
        Assert.Contains("not valid JSON", rejected[0].InnerException!.Message, StringComparison.Ordinal);
        Assert.Single(frames);
    }

    [Fact]
    public void A_state_with_an_unreadable_timestamp_is_rejected_not_loop_fatal()
    {
        // Before R275 the FormatException from 't' escaped the receive loop and killed the session.
        var client = new GridBridgeClient();
        var rejected = new List<BridgeMessageException>();
        client.MessageRejected += rejected.Add;

        client.DispatchGuarded(TopologyJson);
        client.DispatchGuarded(StateJson("not-a-date"));

        BridgeMessageException ex = Assert.Single(rejected);
        Assert.IsType<FormatException>(ex.InnerException);
        Assert.Contains("not-a-date", ex.Message);
    }

    [Fact]
    public void A_state_before_any_topology_is_rejected_through_the_guard()
    {
        var client = new GridBridgeClient();
        var rejected = new List<BridgeMessageException>();
        client.MessageRejected += rejected.Add;

        client.DispatchGuarded(StateJson(HistoricalIso()));

        Assert.IsType<InvalidOperationException>(Assert.Single(rejected).InnerException);
    }

    [Fact]
    public void A_message_with_no_type_is_an_error_not_a_silent_skip()
    {
        var client = new GridBridgeClient();
        Assert.Throws<InvalidDataException>(() => client.Dispatch("{ \"hello\": 1 }"));
        Assert.Throws<InvalidDataException>(() => client.Dispatch("not json at all"));
    }

    [Fact]
    public void A_well_formed_message_of_an_unknown_type_is_still_a_forward_compatible_no_op()
    {
        var client = new GridBridgeClient();
        int rejected = 0;
        client.MessageRejected += _ => rejected++;
        client.DispatchGuarded("{ \"type\": \"heartbeat\" }");
        Assert.Equal(0, rejected);
        Assert.Equal(0, client.RejectedMessages);
    }

    [Fact]
    public void A_forward_inference_violation_is_still_loop_fatal()
    {
        // The horizon is an invariant, not a data glitch: the guard re-throws it so the loop faults.
        var client = new GridBridgeClient();
        int rejected = 0;
        client.MessageRejected += _ => rejected++;
        client.DispatchGuarded(TopologyJson);

        Assert.Throws<ForwardInferenceException>(() => client.DispatchGuarded(StateJson(DateTime.UtcNow.ToString("o"))));
        Assert.Equal(0, rejected);
    }

    // ---- R276: no clock-skew allowance --------------------------------------------------------------

    [Fact]
    public void A_frame_inside_the_horizon_by_ten_minutes_is_refused()
    {
        // 168 h 50 min old. The old 15-minute allowance accepted this; the exact 169 h horizon refuses it.
        var client = new GridBridgeClient();
        client.HandleTopology(TopologyJson);
        string stamp = AgoIso(TimeSpan.FromHours(ForwardInferenceGuard.HorizonHours) - TimeSpan.FromMinutes(10));
        Assert.Throws<ForwardInferenceException>(() => client.HandleState(StateJson(stamp)));
    }

    [Fact]
    public void A_frame_just_outside_the_horizon_is_accepted()
    {
        var client = new GridBridgeClient();
        client.HandleTopology(TopologyJson);
        string stamp = AgoIso(TimeSpan.FromHours(ForwardInferenceGuard.HorizonHours) + TimeSpan.FromMinutes(1));
        SimFrame f = client.HandleState(StateJson(stamp));
        Assert.Equal(1, f.Tick);
    }

    // ---- R277: Unspecified is UTC -------------------------------------------------------------------

    [Fact]
    public void Normalise_relabels_unspecified_as_utc_without_shifting_it()
    {
        var naive = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);
        DateTime n = UtcTime.Normalise(naive);
        Assert.Equal(DateTimeKind.Utc, n.Kind);
        Assert.Equal(naive.Ticks, n.Ticks);   // no offset applied, whatever this machine's time zone

        var utc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, UtcTime.Normalise(utc));
        Assert.Equal(utc, UtcTime.Normalise(utc.ToLocalTime()));   // a Local value is still converted
    }

    [Fact]
    public void Iso_sends_an_unspecified_instant_as_that_same_utc_instant()
    {
        // Before R277 a BST machine sent 2024-07-01T11:00:00Z for this value.
        var naive = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal("2024-07-01T12:00:00.0000000Z", GridBridgeClient.Iso(naive));

        var utc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2024-07-01T12:00:00.0000000Z", GridBridgeClient.Iso(utc));
        Assert.Equal("2024-07-01T12:00:00.0000000Z", GridBridgeClient.Iso(utc.ToLocalTime()));
    }

    [Fact]
    public void The_audit_hash_of_an_unspecified_stamp_equals_the_hash_of_the_utc_stamp()
    {
        // The hash must not depend on the machine's time zone: an Unspecified stamp is the UTC stamp.
        var naive = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var utc = DateTime.SpecifyKind(naive, DateTimeKind.Utc);

        var a = new AuditLog();
        var b = new AuditLog();
        AuditLogEntry ea = a.Append(naive, naive, ProvenanceTag.DerivedFromRecorded, "same");
        AuditLogEntry eb = b.Append(utc, utc, ProvenanceTag.DerivedFromRecorded, "same");

        Assert.Equal(eb.Hash, ea.Hash);
        Assert.True(a.Verify());
        Assert.True(b.Verify());
    }
}
