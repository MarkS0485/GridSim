using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// One end-to-end CLI run per WECC model family: <c>stability case14.raw --rms --fault-bus 4 --clear 40 --dyn-data plant.dyr</c>
/// with the unit at bus 2 given each chain by DYR records. Each run must succeed, report the unit's chain, keep every
/// converter iteration converged (the CLI warns otherwise) and stay in synchronism. With the fixture's bus-1 SEXS and TGOV1
/// left out the fields are held, and a 100 ms fault at bus 4 is beyond this case's clearing time for every family; 40 ms is
/// inside it. The output is kept in the temporary folder for the records.
/// </summary>
public class WeccCliFamilyRunTests
{
    [Theory]
    [InlineData("gfl-plant", "REGC_A + REEC_A + REPC_A")]
    [InlineData("wind-type3", "REGC_A + REEC_A + WTGT_A + WTGAR_A + WTGPT_A + WTGTRQ_A")]
    [InlineData("der-a", "DER_A")]
    [InlineData("regc-b", "REGC_B + REEC_D")]
    [InlineData("regfm-a1", "REGFM_A1")]
    public void Stability_rms_runs_each_wecc_model_family_from_a_dyr_file(string family, string chain)
    {
        string[] records = family switch
        {
            "gfl-plant" => new[] { WeccRenewableNetworkTests.RegcRecord, WeccRenewableNetworkTests.ReecRecord, WeccPlantControllerTests.RepcRecord },
            "wind-type3" => new[] { WeccRenewableNetworkTests.RegcRecord, WeccWindTests.ReecType3, WeccWindTests.Wtdt, WeccWindTests.Wtar, WeccWindTests.Wtpt, WeccWindTests.Wttq },
            "der-a" => new[] { WeccDerATests.DerRecord },
            "regc-b" => new[] { WeccRegcBReecDTests.BRecords },
            "regfm-a1" => new[] { WeccRegfmTests.GfmRecord },
            _ => throw new ArgumentException(family),
        };
        // The fixture's bus-1 TGOV1 and SEXS cannot hold that unit's dispatch and refuse their own start (see
        // WeccPlantControllerTests); they are left out, the rest of the fixture kept.
        string text = WeccRenewableNetworkTests.Dyr(records);
        text = string.Join("\n", text.Split('\n').Where(l => !l.Contains("'TGOV1'", StringComparison.Ordinal) && !l.Contains("'SEXS'", StringComparison.Ordinal)));
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-wecc-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dyr = Path.Combine(dir, "plant.dyr");
            File.WriteAllText(dyr, text);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"gridsim-wecc-cli-{family}.dyr"), text);
            CliProcess.Result r = CliProcess.RunBuilt("stability", WeccRenewableNetworkTests.Fixture("case14.raw"), "--rms", "--fault-bus", "4", "--clear", "40", "--dyn-data", dyr);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"gridsim-wecc-cli-{family}.txt"), $"exit {r.ExitCode}\n--- stdout\n{r.StdOut}\n--- stderr\n{r.StdErr}");
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("1 WECC renewable unit(s) from the dynamics file:", r.StdOut, StringComparison.Ordinal);
            Assert.Contains($"@ bus 2: {chain}  (PSS/E DYR plant.dyr", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("the converter current iteration did not settle", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("fault bus 4  *  cleared after 40 ms  *  STABLE (fleet stays in synchronism)", r.StdOut, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
