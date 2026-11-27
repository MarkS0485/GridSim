using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP20 bit-identity: a composite load without the WP20 extensions (<c>Extensions</c> null, the WP3b CMLD subset) gives
/// exactly the bits it gave before WP20. Method: this file was written and built against the 2.1.118 binaries (commit
/// 87c795d9, before any WP20 edit; GridSim.Core.dll SHA-256
/// 70d09a382476442bdf5e79c5de10bb3c5e15fdc4ff949f96e502533c44ef5df2), run once to read each SHA-256 below, and the values were pinned; the same tests then ran
/// unchanged against the WP20 build. The hashed values are the raw IEEE 754 bits of:
/// <list type="bullet">
/// <item>The WP3b CMLD subset driven alone by a scripted bus voltage (1.01 pu, a dip to 0.6 pu for 0.15 s, recovery to
/// 1.0 pu), 1200 steps of 5 ms: the injection, the motor speed and the far-end voltage at every step.</item>
/// <item>case14 (GENROU units, default DYR path) with that CMLD subset at bus 4, a bus-4 fault at 0.1 s cleared after
/// 0.05 s: every machine trace and the load's converter trace.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp20BitIdentityTests
{
    private const double Hz = 50.0;

    private static void Pin(string expected, string actual) => Assert.True(expected == actual, "pinned " + expected + ", got " + actual);

    private static InductionMotorParameters Motor() =>
        InductionMotorParameters.FromCircuit(0.01, 0.06, 4.0, 0.03, 0.04, 1.0, InductionMotorTorque.PowerLaw(2.0), Hz);

    /// <summary>The WP3b test's CMLD subset (Rc5Wp3bDynamicLibraryTests.CmldLoad).</summary>
    internal static CompositeLoadData Wp3bCmld(int bus, double p, double q) => new(bus, p, q, 100.0, 0.08, 1.0, 0.02, 0.02, 0.04, 0.04, 0.5,
        0.4, 0.8, Motor(), 0.95, new StaticLoadCharacteristic(0.5, 2.0, 0.3, 1.0), new StaticLoadCharacteristic(0.5, 2.0, 0.3, 1.0));

    private static Complex ScriptedVoltage(int k)
    {
        if (k < 20) return GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2);
        if (k < 50) return GridSim.Core.Numerics.DetMath.FromPolar(0.6, -0.25);
        return GridSim.Core.Numerics.DetMath.FromPolar(1.0, -0.21);
    }

    [Fact]
    public void The_wp3b_cmld_subset_alone_gives_the_pinned_bits()
    {
        CompositeLoadData d = Wp3bCmld(4, 47.8, -3.9);
        DynamicLoadPlan plan = d.Plan(GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2), 100.0, Hz);
        var m = (CompositeLoadData.State)plan.CreateModel();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            Complex y = plan.NortonAdmittance;
            w.Write(BitConverter.DoubleToInt64Bits(y.Real));
            w.Write(BitConverter.DoubleToInt64Bits(y.Imaginary));
            for (int k = 0; k < 1200; k++)
            {
                Complex v = ScriptedVoltage(k);
                Complex inj = m.Commit(v);
                m.Step(k * 0.005, v, new[] { v }, 0.005);
                foreach (double x in new[] { inj.Real, inj.Imaginary, m.Motor.Speed, m.FarEndVoltage.Real, m.FarEndVoltage.Imaginary })
                    w.Write(BitConverter.DoubleToInt64Bits(x));
            }
        }
        Pin("3F19519195634BFE10787CAE7ADA736666AA93A40838A0A0CD73695222CEFCD2", Convert.ToHexString(SHA256.HashData(ms.ToArray())));
    }

    private const string Genrou = " 7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0";

    private const string Dyr =
          "     1 'GENROU' 1 " + Genrou + " /\n"
        + "     2 'GENROU' 1 " + Genrou + " /\n"
        + "     3 'GENROU' 1 " + Genrou + " /\n"
        + "     6 'GENROU' 1 " + Genrou + " /\n"
        + "     8 'GENROU' 1 " + Genrou + " /\n";

    /// <summary>case14 with GENROU units through the default DYR reader, the power flow solved to 1e-13.</summary>
    internal static (GridModel Model, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case14()
    {
        string raw = File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(raw, "case14");
        PsseDyrResult dyr = PsseDyrReader.Read(Dyr, r, "case14", new PsseDyrOptions { SourceName = "wp20.dyr" });
        string dir = TestTemp.Under("gridsim-wp20-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        MachineDynamicsSet set;
        try
        {
            string path = Path.Combine(dir, "wp20.dyr.dynamics.json");
            File.WriteAllText(path, dyr.Json);
            set = MachineDynamicsLoader.Load(path, Path.Combine(CliProcess.Root(), "schemas", "v2"), r.Model);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        GridModel m = r.Model;
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        return (m, pf, m.Generators.Select((_, k) => set.For(k)!.Data with { Governor = null }).ToList());
    }

    internal static string Digest(MultiMachineSubtransient.Result r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
                foreach (IReadOnlyList<double> s in new[] { m.DeltaRad, m.SpeedPu, m.EqpPu, m.EdpPu, m.EqppPu, m.EddpPu })
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
            foreach (RmsConverterTrace c in r.Converters ?? Array.Empty<RmsConverterTrace>())
                foreach (IReadOnlyList<double> s in new[] { c.TerminalVoltagePu, c.ActivePowerPu, c.ReactivePowerPu })
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    [Fact]
    public void Case14_with_the_wp3b_cmld_subset_gives_the_pinned_bits()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case14();
        var loaded = new RmsLoadedMachineData(data, new DynamicLoadData[] { Wp3bCmld(4, 47.8, -3.9) });
        MultiMachineSubtransient.Result run = RmsTransientStability.Study(m, pf, loaded, 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 1.0);
        Assert.True(run.ConverterIterationsConverged);
        Pin("17CA4EB6987EEEC06CAB5599C1B913BAF073CB0601B7B93AEB0F259541A2BDE1", Digest(run));
    }
}
