using System.Collections.Generic;
using System.IO;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-706: the interpose endpoint is checked before anything listens. A port outside 0..65535 and an unbracketed IPv6
/// address are usage errors (exit 2); they used to reach the TCP listener and exit 1 as a transport failure, or could
/// not be given at all.
/// </summary>
public sealed class InterposeEndpointProcessTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-endpoint-{System.Guid.NewGuid():N}");

    public InterposeEndpointProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (System.UnauthorizedAccessException) { }
    }

    private string Crosswalk()
    {
        string path = Path.Combine(_dir, "modbus-crosswalk.json");
        new RtuPointMap
        {
            Case = "case14",
            Bindings = new List<RtuPointBinding>
            {
                new() { Address = "MB:U1:3:@100", Kind = MeasurementKind.PInjMw, Bus = 4, Scale = 0.1 },
            },
        }.Save(path);
        return path;
    }

    [Theory]
    [InlineData("--port", "70000")]
    [InlineData("--port", "-1")]
    [InlineData("--listen", "127.0.0.1:70000")]
    [InlineData("--listen", "::1:2404")]
    [InlineData("--listen", "[::1]:99999")]
    [InlineData("--listen", "[127.0.0.1]:2404")]
    public void An_endpoint_that_cannot_be_bound_as_given_exits_2(string flag, string value)
    {
        CliProcess.Result r = CliProcess.RunBuilt("interpose", "--protocol", "modbus", flag, value,
            "--crosswalk", Crosswalk(), "--live-seconds", "1");
        Assert.True(r.ExitCode == 2, $"expected exit 2\n{r}");
        Assert.Contains("error:", r.StdErr, System.StringComparison.Ordinal);
    }
}
