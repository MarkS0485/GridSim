using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using GridSim.Core.IO;
using GridSim.Core.IO.Geo;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.PyPsa;
using GridSim.Core.IO.Telemetry;
using GridSim.Core.Model;
using GridSim.Core.Net;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29: further IO and transport findings (FB47 to FB58). See
/// BugHunt1029DynamicsTests.cs for the conventions.</summary>
public sealed partial class BugHunt1029Tests
{
    // BH47: a name with outer blanks does not round-trip through RAW (the reader trims); the writer now says so.
    [Fact]
    public void BH47_raw_writer_notes_names_with_outer_blanks()
    {
        GridModel m = IeeeCases.Case9();
        GridModel named = m.CopyWith(buses: m.Buses.Select((b, i) => i == 0 ? b with { Name = "North " } : b).ToList());
        Assert.Contains(PsseRawWriter.Write(named).Notes, n => n.StartsWith("1 name(s) begin or end with a blank", StringComparison.Ordinal));
        Assert.DoesNotContain(PsseRawWriter.Write(m).Notes, n => n.Contains("begin or end with a blank", StringComparison.Ordinal));
    }

    // BH48: PMIN = -Inf, which the parser reads, is written as -Inf instead of being refused.
    [Fact]
    public void BH48_matpower_pmin_minus_infinity_round_trips()
    {
        GridModel m = IeeeCases.Case9();
        GridModel g = m.CopyWith(generators: m.Generators.Select((x, i) => i == 1 ? x with { Pmin = double.NegativeInfinity } : x).ToList());
        MatpowerWriteResult w = MatpowerWriter.Write(g);
        GridModel back = MatpowerParser.Parse(w.Text, "bh48");
        Assert.Equal(double.NegativeInfinity, back.Generators[1].Pmin);
        Assert.Equal(g.Generators[0].Pmin, back.Generators[0].Pmin);
    }

    // BH55: an APPID or PDU length that does not fit the 16-bit header fields is refused, not wrapped.
    [Fact]
    public void BH55_goose_encoder_refuses_values_that_do_not_fit_sixteen_bits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec61850GooseFrame.ToBytes(new GooseHeader(0x1_0001, new byte[4])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec61850GooseFrame.ToBytes(new GooseHeader(-1, new byte[4])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec61850GooseFrame.ToBytes(new GooseHeader(1, new byte[ushort.MaxValue - 7])));
        // The largest PDU that fits still round-trips, and so does the largest APPID.
        var ok = new GooseHeader(ushort.MaxValue, new byte[ushort.MaxValue - 8]);
        GooseHeader back = Iec61850GooseFrame.Parse(Iec61850GooseFrame.ToBytes(ok));
        Assert.Equal(ok.AppId, back.AppId);
        Assert.Equal(ok.Pdu.Length, back.Pdu.Length);
    }

    // BH56: a point outside the GB layout box is flagged, not silently drawn on the edge (Shetland in the zonal model).
    [Fact]
    public void BH56_points_outside_the_gb_layout_box_are_flagged()
    {
        Assert.True(OsgbGrid.IsInsideGbLayout(54.0, -2.0));
        Assert.True(OsgbGrid.IsInsideGbLayout(OsgbGrid.LayoutSouthLatDeg, OsgbGrid.LayoutWestLonDeg));   // edges included
        Assert.False(OsgbGrid.IsInsideGbLayout(60.377, -1.24));
        PypsaGbZonalNetwork z = PypsaGbZonal.Load(Path.Combine(BHRepoRoot(), "data", "matpower", "gb", "pypsa-gb", "zonal"));
        Assert.Equal(new[] { "Z1_1" }, z.Zones.Where(x => x.LayoutClamped).Select(x => x.Name));
        Assert.Equal(1.0, z.Zones.Single(x => x.Name == "Z1_1").Y);   // still drawn on the edge, now flagged
    }

    private static string BHRepoRoot()
    {
        string? d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "GridSim.slnx"))) d = Path.GetDirectoryName(d);
        return d ?? throw new InvalidOperationException("repository root (GridSim.slnx) not found above the test binaries");
    }

    // BH57: cancelling the token given to ConnectAsync fails the pending queries at once, instead of leaving each to wait
    // out its full QueryTimeout.
    [Fact]
    public async Task BH57_cancelling_the_connect_token_fails_pending_queries_promptly()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        // A server that accepts the WebSocket and then says nothing.
        Task<WebSocketHolder> server = Task.Run(async () =>
        {
            HttpListenerContext ctx = await listener.GetContextAsync();
            HttpListenerWebSocketContext ws = await ctx.AcceptWebSocketAsync(null);
            return new WebSocketHolder(ws);
        });

        await using var client = new GridBridgeClient { AllowUnencryptedEndpoint = true, QueryTimeout = TimeSpan.FromSeconds(60) };
        using var cts = new CancellationTokenSource();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), cts.Token);
        using WebSocketHolder held = await server;
        Task<QueryResult> pending = client.Query("bus_voltages", DateTime.UtcNow.AddDays(-30), new { });
        await Task.Delay(200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"the pending query waited {sw.Elapsed} after the cancel");
    }

    private sealed class WebSocketHolder(HttpListenerWebSocketContext ctx) : IDisposable
    {
        public void Dispose() => ctx.WebSocket.Dispose();
    }
}
