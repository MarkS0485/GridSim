using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.Doc15Frames;

namespace GridSim.Tests;

/// <summary>
/// 1.95.245: the linear PMU state estimate gets a production caller. <see cref="PmuMeasurementSetAdapter"/> pairs a
/// set's polar phasor rows into rectangular phasors with their first-order covariance; <see cref="LinearPmuStateEstimator"/>
/// takes an optional imaginary sigma (bit for bit unchanged without it) and the "#k" circuit form; the CLI verb
/// <c>pmu-estimate</c> runs it over a gridsim-measurements/1 set.
/// </summary>
public sealed class PmuEstimateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-pmu-estimate-" + Guid.NewGuid().ToString("N"));

    public PmuEstimateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static (GridModel Model, PowerFlowResult Solve) Case9()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        return (m, r);
    }

    /// <summary>VmPu + VaDeg at every bus of the solved case9, plus one PInjMw row the linear estimate cannot use.</summary>
    private static MeasurementSet FullSet(PowerFlowResult r)
    {
        var rows = new List<Measurement>();
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            rows.Add(new Measurement(MeasurementKind.VmPu, r.Vm[i], 0.001, Bus: r.BusIds[i], Source: "pmu"));
            rows.Add(new Measurement(MeasurementKind.VaDeg, r.VaDeg[i], 0.05, Bus: r.BusIds[i], Source: "pmu"));
        }
        rows.Add(new Measurement(MeasurementKind.PInjMw, 10.0, 1.0, Bus: 5));
        return new MeasurementSet { Case = "case9", BaseMva = 100.0, TakenUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Measurements = rows };
    }

    [Fact]
    public void The_adapter_pairs_every_bus_and_lists_the_row_it_cannot_use()
    {
        (GridModel m, PowerFlowResult r) = Case9();
        PmuPhasorSet p = PmuMeasurementSetAdapter.FromSet(FullSet(r), m);
        Assert.Equal(9, p.Voltages.Count);
        Assert.Empty(p.Currents);
        string line = Assert.Single(p.NotUsed);
        Assert.Contains("PInjMw @ bus 5", line, StringComparison.Ordinal);
        Assert.Equal(r.BusIds, p.Voltages.Select(v => v.BusId));
    }

    [Fact]
    public void The_covariance_is_the_first_order_polar_transform_exactly()
    {
        var set = new MeasurementSet
        {
            Case = "case9", BaseMva = 100.0,
            Measurements = new[]
            {
                new Measurement(MeasurementKind.VmPu, 1.02, 0.002, Bus: 4),
                new Measurement(MeasurementKind.VaDeg, -12.5, 0.1, Bus: 4),
            },
        };
        PmuVoltageMeasurement v = Assert.Single(PmuMeasurementSetAdapter.FromSet(set, IeeeCases.Case9()).Voltages);
        const double D = Math.PI / 180.0;
        (double s, double c) = DetMath.SinCos(-12.5 * D);
        double sm2 = 0.002 * 0.002, st = 0.1 * D, mst2 = 1.02 * 1.02 * (st * st);
        Assert.Equal(new Complex(1.02 * c, 1.02 * s), v.Value);
        Assert.Equal(Math.Sqrt(c * c * sm2 + s * s * mst2), v.Sigma);
        Assert.Equal(Math.Sqrt(s * s * sm2 + c * c * mst2), v.SigmaIm);
        Assert.Equal(c * s * (sm2 - mst2), v.CovOffDiag);
    }

    [Fact]
    public void Ambiguous_and_to_end_rows_are_refused_and_an_unpaired_row_is_listed()
    {
        GridModel m = IeeeCases.Case9();
        var twice = new MeasurementSet
        {
            Case = "case9", BaseMva = 100.0,
            Measurements = new[]
            {
                new Measurement(MeasurementKind.VmPu, 1.0, 0.001, Bus: 1),
                new Measurement(MeasurementKind.VmPu, 1.01, 0.001, Bus: 1),
                new Measurement(MeasurementKind.IMagPu, 0.5, 0.01, From: 1, To: 4, End: BranchEnd.To),
            },
        };
        var ex = Assert.Throws<InvalidDataException>(() => PmuMeasurementSetAdapter.FromSet(twice, m));
        Assert.Contains("a second VmPu row at bus 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("metered at the To end", ex.Message, StringComparison.Ordinal);

        var lone = new MeasurementSet { Case = "case9", BaseMva = 100.0, Measurements = new[] { new Measurement(MeasurementKind.VaDeg, 1.0, 0.1, Bus: 2) } };
        PmuPhasorSet p = PmuMeasurementSetAdapter.FromSet(lone, m);
        Assert.Empty(p.Voltages);
        Assert.Contains("bus 2: VaDeg with no VmPu row", Assert.Single(p.NotUsed), StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_phasors_recover_the_solved_state()
    {
        (GridModel m, PowerFlowResult r) = Case9();
        PmuPhasorSet p = PmuMeasurementSetAdapter.FromSet(FullSet(r), m);
        LinearPmuResult e = LinearPmuStateEstimator.Estimate(m, p.Voltages, p.Currents);
        Assert.True(e.Converged);
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            Assert.Equal(r.Vm[i], e.Vm[i], 12);
            Assert.Equal(r.VaDeg[i], e.VaDeg[i], 9);
        }
    }

    [Fact]
    public void An_explicit_imaginary_sigma_equal_to_sigma_is_bit_identical_to_none()
    {
        (GridModel m, PowerFlowResult r) = Case9();
        var plain = new List<PmuVoltageMeasurement>();
        var same = new List<PmuVoltageMeasurement>();
        for (int i = 0; i < r.BusIds.Count; i++)
        {
            Complex v = DetMath.FromPolar(r.Vm[i], r.VaDeg[i] * Math.PI / 180.0) + new Complex(1e-4 * i, -2e-4 * i);
            plain.Add(new PmuVoltageMeasurement(r.BusIds[i], v, 0.003, 1e-6));
            same.Add(new PmuVoltageMeasurement(r.BusIds[i], v, 0.003, 1e-6, SigmaIm: 0.003));
        }
        LinearPmuResult a = LinearPmuStateEstimator.Estimate(m, plain, Array.Empty<PmuCurrentMeasurement>());
        LinearPmuResult b = LinearPmuStateEstimator.Estimate(m, same, Array.Empty<PmuCurrentMeasurement>());
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.ObjectiveJ), BitConverter.DoubleToInt64Bits(b.ObjectiveJ));
    }

    [Fact]
    public void A_bad_imaginary_sigma_is_refused_by_name()
    {
        GridModel m = IeeeCases.Case9();
        var bad = new[] { new PmuVoltageMeasurement(1, Complex.One, 0.01, 0.0, SigmaIm: 0.0) };
        var ex = Assert.Throws<InvalidDataException>(() => LinearPmuStateEstimator.Estimate(m, bad, Array.Empty<PmuCurrentMeasurement>()));
        Assert.Contains("the imaginary sigma is 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinal_circuit_names_the_branch_by_index()
    {
        (GridModel m, PowerFlowResult r) = Case9();
        int k = 0;
        Branch br = m.Branches[k];
        // From-end current of branch k at the solved state, from the same pi model the estimator stamps.
        int f = r.BusIds.ToList().IndexOf(br.From), t = r.BusIds.ToList().IndexOf(br.To);
        Complex vf = DetMath.FromPolar(r.Vm[f], r.VaDeg[f] * Math.PI / 180.0), vt = DetMath.FromPolar(r.Vm[t], r.VaDeg[t] * Math.PI / 180.0);
        Complex ys = Complex.One / new Complex(br.R, br.X);
        double tapA = br.TapRatio <= 0 ? 1.0 : br.TapRatio;
        Complex tap = DetMath.FromPolar(tapA, br.PhaseShiftDeg * Math.PI / 180.0);
        Complex i = (ys + new Complex(0, br.B / 2.0)) / (tapA * tapA) * vf + (-ys / Complex.Conjugate(tap)) * vt;
        var voltages = r.BusIds.Select((id, n) => new PmuVoltageMeasurement(id, DetMath.FromPolar(r.Vm[n], r.VaDeg[n] * Math.PI / 180.0), 0.001)).ToList();
        LinearPmuResult e = LinearPmuStateEstimator.Estimate(m, voltages, new[] { new PmuCurrentMeasurement(br.From, br.To, "#" + k, i, 0.001) });
        Assert.True(e.Converged);
        Assert.True(e.ObjectiveJ < 1e-12, $"J = {e.ObjectiveJ}");
    }

    [Fact]
    public void The_verb_estimates_lists_what_it_did_not_use_and_writes_its_result()
    {
        (_, PowerFlowResult r) = Case9();
        string set = Path.Combine(_dir, "pmu9.json");
        FullSet(r).Save(set);
        string json = Path.Combine(_dir, "est.json");
        CliProcess.Result run = CliProcess.RunBuilt("pmu-estimate", "case9", "--measurements", set, "--json", json);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("phasors used: 9 voltage, 0 current (18 rectangular rows for 18 states); not used: 1 row(s)", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("not used: measurement #18 (PInjMw @ bus 5)", run.StdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(json));
        Assert.Contains("\"vm_pu\"", File.ReadAllText(json), StringComparison.Ordinal);
    }

    [Fact]
    public void The_verb_refuses_usage_errors_and_an_unobservable_set()
    {
        (_, PowerFlowResult r) = Case9();
        MeasurementSet full = FullSet(r);
        string part = Path.Combine(_dir, "part.json");
        (full with { Measurements = full.Measurements.Take(6).ToList() }).Save(part);
        CliProcess.Result unobs = CliProcess.RunBuilt("pmu-estimate", "case9", "--measurements", part);
        Assert.True(unobs.ExitCode == 3, unobs.ToString());
        Assert.Contains("NOT OBSERVABLE", unobs.StdErr, StringComparison.Ordinal);

        CliProcess.Result noSet = CliProcess.RunBuilt("pmu-estimate", "case9");
        Assert.True(noSet.ExitCode == 2, noSet.ToString());
        CliProcess.Result foreign = CliProcess.RunBuilt("pmu-estimate", "case9", "--measurements", part, "--method", "wls");
        Assert.True(foreign.ExitCode == 2, foreign.ToString());
        CliProcess.Result missing = CliProcess.RunBuilt("pmu-estimate", "case9", "--measurements", Path.Combine(_dir, "nope.json"));
        Assert.True(missing.ExitCode == 1, missing.ToString());
    }

    [Fact]
    public void A_set_written_by_pmu_ingest_is_read_by_pmu_estimate()
    {
        // The chain the verb exists for: pmu-ingest maps a capture to sets, pmu-estimate reads one. One voltage and one
        // current phasor cannot observe all of case9, so the estimate is refused as not observable (exit 3); what this
        // proves is that the set's rows pair into phasors (1 voltage, 1 current, nothing unused).
        const uint oldSoc = 1_700_000_000;
        var pmu = new Pmu(101, "STN_A", new[] { Voltage, Current });
        string cap = Path.Combine(_dir, "cap.bin");
        File.WriteAllBytes(cap, Config(1_000_000, oldSoc, pmu).Concat(Data(oldSoc, 0, new[] { (9_500.0f, 0.0f), (1_000.0f, -100.0f) })).ToArray());
        string map = Path.Combine(_dir, "channels.json");
        File.WriteAllText(map, """
            { "schema": "gridsim-pmu-channels/1", "case": "case9", "baseMva": 100,
              "reference": { "pmu": 101, "phasor": 0, "anchorDeg": 0 },
              "pmus": [ { "pmu": 101,
                          "voltages": [ { "phasor": 0, "bus": 1, "baseVolts": 9500, "vmSigmaPu": 0.002, "vaSigmaDeg": 0.1 } ],
                          "currents": [ { "phasor": 1, "from": 1, "to": 4, "baseAmps": 1000, "iMagSigmaPu": 0.005, "iAngSigmaDeg": 0.2 } ] } ] }
            """);
        string sets = Path.Combine(_dir, "sets");
        CliProcess.Result ingest = CliProcess.RunBuilt("pmu-ingest", cap, "--channels", map, "--measurements-dir", sets);
        Assert.True(ingest.ExitCode == 0, ingest.ToString());
        string one = Assert.Single(Directory.GetFiles(sets, "*.json"));
        CliProcess.Result est = CliProcess.RunBuilt("pmu-estimate", "case9", "--measurements", one);
        Assert.True(est.ExitCode == 3, est.ToString());
        Assert.Contains("phasors used: 1 voltage, 1 current", est.StdOut, StringComparison.Ordinal);
        Assert.Contains("not used: 0 row(s)", est.StdOut, StringComparison.Ordinal);
    }
}
