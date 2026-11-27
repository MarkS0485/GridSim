using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static GridSim.Tests.MotorStartTestSupport;

namespace GridSim.Tests;

/// <summary>WP29: <c>gridsim motor-start</c> with a starter object and with a group, through the real entry point. The
/// case identity is computed independently with the documented rule (as MotorStartCliProcessTests does).</summary>
public sealed class Rc5Wp29MotorStartCliTests
{
    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Identity(string dir, string manifest, string network)
    {
        var sb = new StringBuilder();
        sb.Append(manifest).Append('\t').Append(Sha(Path.Combine(dir, manifest))).Append('\n');
        sb.Append("network/").Append(network).Append('\t').Append(Sha(Path.Combine(dir, network))).Append('\n');
        foreach (string f in Directory.GetFiles(Schemas, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append("schemas/").Append(Path.GetRelativePath(Schemas, f).Replace('\\', '/')).Append('\t').Append(Sha(f)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Setup(string fixtureCase, string fixtureNetwork)
    {
        string dir = TestTemp.Under("gridsim-wp29-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(Fixture(fixtureCase), Path.Combine(dir, fixtureCase));
        File.Copy(Fixture(fixtureNetwork), Path.Combine(dir, fixtureNetwork));
        return dir;
    }

    private static readonly object Integration = new { stepSeconds = 0.001 };
    private static readonly object Termination = new { runningSpeedPu = 0.98, horizonSeconds = 10.0, stallDwellSeconds = 0.5, stallAcceleratingTorquePu = 0.0 };
    private static readonly object PreStartTolerance = new { pgToleranceMw = 1e-3, vmTolerancePu = 1e-6, maxSlackMovementMw = 2.0 };

    [Fact]
    public void A_star_delta_study_runs_and_reports_its_transition()
    {
        string dir = Setup("stiff.case.json", "stiff.network.json");
        try
        {
            string sha = Identity(dir, "stiff.case.json", "stiff.network.json");
            string study = Path.Combine(dir, "study.json");
            File.WriteAllText(study, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @case = new { path = "stiff.case.json", sha256 = sha },
                motor = "M1",
                initial = new { speedPu = 0.0, connected = false },
                start = new { atSeconds = 0.1, method = "starDelta", starter = new { switchSpeedPu = 0.8 } },
                integration = Integration,
                // In star the torque margin over the 0.7 w^2 load falls to about 0.028 pu near 0.7 pu speed, so the
                // run is slow: an independent forward-Euler integration of this motor's equivalent circuit (stiff
                // 1.0 pu bus, H 0.6 s, a = 1/sqrt(3), 1 ms) reaches 0.8 pu at 16.24 s and 0.98 pu in delta at
                // 16.39 s. The shared 10 s horizon ends in star; 20 s leaves 3.6 s over the derived running time.
                termination = new { runningSpeedPu = 0.98, horizonSeconds = 20.0, stallDwellSeconds = 0.5, stallAcceleratingTorquePu = 0.0 },
                preStart = PreStartTolerance,
                report = new { sampleEverySteps = 100 },
            }));
            var run = CliProcess.RunBuilt("motor-start", study);
            Assert.True(run.ExitCode == 0, run.ToString());
            using JsonDocument doc = JsonDocument.Parse(run.StdOut);
            JsonElement result = doc.RootElement.GetProperty("result");
            Assert.Equal("RUNNING", result.GetProperty("status").GetString());
            Assert.Equal("starDelta", result.GetProperty("request").GetProperty("method").GetString());
            JsonElement tr = Assert.Single(result.GetProperty("starterTransitions").EnumerateArray());
            Assert.Equal("star", tr.GetProperty("fromStage").GetString());
            Assert.Equal("delta", tr.GetProperty("toStage").GetString());
            Assert.Equal(result.GetProperty("samples").GetArrayLength(), result.GetProperty("starterSamples").GetArrayLength());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_reactor_in_ohms_is_converted_on_the_motor_base()
    {
        string dir = Setup("stiff.case.json", "stiff.network.json");
        try
        {
            string sha = Identity(dir, "stiff.case.json", "stiff.network.json");
            string study = Path.Combine(dir, "study.json");
            // M1 is 5 MVA, 11 kV: Z_base = 121 / 5 = 24.2 ohm, so 2.42 ohm is 0.1 pu (to one rounding of each operand).
            File.WriteAllText(study, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @case = new { path = "stiff.case.json", sha256 = sha },
                motor = "M1",
                initial = new { speedPu = 0.0, connected = false },
                start = new { atSeconds = 0.1, method = "reactor", starter = new { reactorROhm = 0.0, reactorXOhm = 2.42 } },
                integration = Integration,
                termination = Termination,
                preStart = PreStartTolerance,
                report = new { sampleEverySteps = 1000 },
            }));
            var run = CliProcess.RunBuilt("motor-start", study);
            Assert.True(run.ExitCode == 0, run.ToString());
            using JsonDocument doc = JsonDocument.Parse(run.StdOut);
            JsonElement starter = doc.RootElement.GetProperty("result").GetProperty("request").GetProperty("starter");
            Assert.True(Math.Abs(starter.GetProperty("reactorXPu").GetDouble() - 0.1) <= 1e-15);
            Assert.Equal(0.0, starter.GetProperty("reactorRPu").GetDouble());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_group_study_reports_every_motor_and_maps_the_exit_to_the_group_outcome()
    {
        string dir = Setup("weak.case.json", "weak.network.json");
        try
        {
            string sha = Identity(dir, "weak.case.json", "weak.network.json");
            string study = Path.Combine(dir, "study.json");
            File.WriteAllText(study, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @case = new { path = "weak.case.json", sha256 = sha },
                group = new object[]
                {
                    new { motor = "M1", initial = new { speedPu = 0.0, connected = false }, start = new { atSeconds = 0.1, method = "directOnLine" } },
                    new { motor = "M2", initial = new { speedPu = 0.0, connected = false }, start = new { atSeconds = 0.2, method = "softStarter", starter = new { initialVoltageFraction = 0.5, rampSeconds = 2.0 } } },
                },
                integration = Integration,
                termination = Termination,
                preStart = PreStartTolerance,
                report = new { sampleEverySteps = 1000 },
            }));
            var run = CliProcess.RunBuilt("motor-start", study);
            // M2 cannot start: its locked-rotor torque at 1.0 pu is 0.3543 pu (computed independently in Python from the
            // fixture's circuit), below its constant 0.4 pu load, and the soft starter only lowers its voltage (the bus is
            // below 1.0 pu on the inductive feeder). So it is held at standstill and STALLED after the 0.5 s dwell, and
            // the group is STALLED, exit 3.
            Assert.True(run.ExitCode == 3, run.ToString());
            using JsonDocument doc = JsonDocument.Parse(run.StdOut);
            JsonElement result = doc.RootElement.GetProperty("result");
            Assert.Equal("STALLED", result.GetProperty("status").GetString());
            JsonElement motors = result.GetProperty("motors");
            Assert.Equal(2, motors.GetArrayLength());
            Assert.Equal("M1", motors[0].GetProperty("motor").GetProperty("name").GetString());
            Assert.Equal("M2", motors[1].GetProperty("motor").GetProperty("name").GetString());
            Assert.Equal("STALLED", motors[1].GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Object, result.GetProperty("voltageDip").ValueKind);

            // The two shapes do not mix.
            string mixed = File.ReadAllText(study).Replace("\"group\":", "\"motor\":\"M1\",\"group\":", StringComparison.Ordinal);
            File.WriteAllText(study, mixed);
            var refused = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("unknown field 'motor'", refused.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unknown_method_or_starter_field_is_refused_as_input()
    {
        string dir = Setup("stiff.case.json", "stiff.network.json");
        try
        {
            string sha = Identity(dir, "stiff.case.json", "stiff.network.json");
            string study = Path.Combine(dir, "study.json");
            string Body(object start) => JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @case = new { path = "stiff.case.json", sha256 = sha },
                motor = "M1",
                initial = new { speedPu = 0.0, connected = false },
                start,
                integration = Integration,
                termination = Termination,
                preStart = PreStartTolerance,
            });
            File.WriteAllText(study, Body(new { atSeconds = 0.1, method = "wauchope" }));
            var unknownMethod = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(1, unknownMethod.ExitCode);
            Assert.Contains("start.method must be one of", unknownMethod.StdErr, StringComparison.Ordinal);
            File.WriteAllText(study, Body(new { atSeconds = 0.1, method = "starDelta", starter = new { closedTransition = true } }));
            var unknownField = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(1, unknownField.ExitCode);
            Assert.Contains("unknown field 'closedTransition'", unknownField.StdErr, StringComparison.Ordinal);
            // An ohms field on a method other than reactor is refused under its own name, not as a converted pu field.
            File.WriteAllText(study, Body(new { atSeconds = 0.1, method = "starDelta", starter = new { reactorXOhm = 2.42 } }));
            var ohmsOnStarDelta = CliProcess.RunBuilt("motor-start", study);
            Assert.Equal(1, ohmsOnStarDelta.ExitCode);
            Assert.Contains("start.starter.reactorXOhm: not a field of a starDelta starter", ohmsOnStarDelta.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("reactorXPu", ohmsOnStarDelta.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
