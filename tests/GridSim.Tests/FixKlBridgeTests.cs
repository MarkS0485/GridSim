using GridSim.Core.Diagnostics;
using GridSim.Core.Net;
using Xunit;

namespace GridSim.Tests;

/// <summary>Post-RC3 batch K: APP-04, a bridge query result is held to the forward-inference guard.</summary>
public sealed class FixKlBridgeTests
{
    private static string Result(string id, string? at) =>
        "{\"type\":\"result\",\"id\":\"" + id + "\",\"op\":\"scenario\"" + (at is null ? "" : ",\"at\":\"" + at + "\"")
        + ",\"payload\":{\"verdict\":\"feasible\"}}";

    [Fact]
    public async Task App04_a_result_dated_inside_the_horizon_fails_its_query_with_the_guard_exception()
    {
        var client = new GridBridgeClient();
        Task<GridSim.Core.IO.QueryResult> pending = client.AddPendingForTest("r1", "scenario");
        string recent = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        client.HandleResult(Result("r1", recent));
        await Assert.ThrowsAsync<ForwardInferenceException>(() => pending);
    }

    [Fact]
    public async Task App04_a_historical_result_and_an_undated_result_complete_as_before()
    {
        var client = new GridBridgeClient();
        Task<GridSim.Core.IO.QueryResult> old = client.AddPendingForTest("r2", "scenario");
        string historical = DateTime.UtcNow.AddHours(-200).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        client.HandleResult(Result("r2", historical));
        Assert.Equal("feasible", (await old).Verdict);

        // No 'at' means there is no timestamp to check: accepted (recorded in the fix report as a ruled limit).
        Task<GridSim.Core.IO.QueryResult> undated = client.AddPendingForTest("r3", "scenario");
        client.HandleResult(Result("r3", null));
        Assert.Equal("feasible", (await undated).Verdict);
    }
}
