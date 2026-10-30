using GridSim.Core.Estimation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R062 (October 2026 review, part): SchweppeGm and Qr were reachable from the API only. Every EstimationMethod member
/// must be selectable from `gridsim estimate --method`; source-level (the test project does not reference the CLI).
/// </summary>
public class EstimateMethodSelectionTests
{
    [Fact]
    public void Every_estimation_method_has_a_cli_name()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs"));
        foreach (EstimationMethod m in Enum.GetValues<EstimationMethod>())
            Assert.Contains($"=> EstimationMethod.{m},", src);
        Assert.Contains("\"schweppe-gm\" or \"gm\" => EstimationMethod.SchweppeGm,", src);
        Assert.Contains("\"qr\" => EstimationMethod.Qr,", src);
    }
}
