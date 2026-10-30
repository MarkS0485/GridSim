using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// WECC expansion, slice 1: WECC REGC_A + REEC_A units in the RMS engine and from PSS/E DYR records.
///   - WSCC-9 with its bus-3 unit (a synchronous unit in the case) given a WECC chain through its data: it leaves the
///     machine fleet, is retained as a converter node of its own, starts at rest, rides through a bolted fault at its
///     own bus on reactive current and recovers its pre-fault power, and the run is bit-identical when repeated;
///   - the IEEE 14-bus PSS/E fixture with the bus-2 machine records replaced by REGCA1 + REECA1 records: every value lands
///     where the record put it, the chain runs in an RMS study, and the renewable data problems are refused with a list.
/// </summary>
public class WeccRenewableNetworkTests
{
    private static readonly Dictionary<int, double> Xdp = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static RegcaParameters Regc() => new()
    {
        Lvplsw = 1, Tg = 0.02, Rrpwr = 10.0, Brkpt = 0.9, Zerox = 0.4, Lvpl1 = 1.22, Volim = 1.2, Lvpnt1 = 0.8, Lvpnt0 = 0.4,
        Iolim = -1.3, Tfltr = 0.02, Khv = 0.7, Iqrmax = 9999.0, Iqrmin = -9999.0, Accel = 0.7,
    };

    private static ReecParameters ReecA() => new()
    {
        Variant = ReecVariant.A, PfFlag = 0, VFlag = 1, QFlag = 1, PqFlag = 0, Vdip = 0.9, Vup = 1.1, Trv = 0.02,
        Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 2.0, Iqh1 = 1.05, Iql1 = -1.05, Vref0 = 0.0, Tp = 0.05, Qmax = 0.6, Qmin = -0.6,
        Vmax = 1.1, Vmin = 0.9, Kqp = 0.0, Kqi = 0.1, Kvp = 0.0, Kvi = 40.0, Tiq = 0.02, DPmax = 99.0, DPmin = -99.0,
        Pmax = 1.0, Pmin = 0.0, Imax = 1.2, Tpord = 0.02,
    };

    private static (GridModel M, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9Wecc()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var data = m.Generators.Select(g =>
        {
            double x = Xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus == 3 ? d with { Renewable = new WeccPlant(Regc(), ReecA(), "test") } : d;
        }).ToList();
        return (m, pf, data);
    }

    [Fact]
    public void A_unit_with_a_wecc_chain_is_converter_plant_of_its_own()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9Wecc();
        Assert.False(m.Generators.Single(g => g.Bus == 3).IsConverterInterfaced);   // a synchronous unit in the case
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(m, pf, data);
        Assert.Equal(2, p.Machines.Count);
        RmsConverterUnit u = Assert.Single(p.ConverterUnits);
        Assert.Equal(3, u.Bus);
        Assert.NotNull(u.Wecc);
        Assert.Equal(0.85, u.P0, 9);
        Assert.Equal(System.Numerics.Complex.Zero, u.NortonAdmittance);
        Assert.IsType<WeccConverterState>(u.CreateModel(RmsConverterUnit.NodeBuses(p.ConverterUnits)));
    }

    [Fact]
    public void Undisturbed_run_stays_at_its_power_flow_point()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9Wecc();
        // A fault scheduled after the end: the run is undisturbed.
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, faultBusId: 3, onsetSeconds: 50.0,
            clearingSeconds: 0.1, stepSeconds: 0.005, durationSeconds: 3.0);
        RmsConverterTrace c = Assert.Single(r.Converters!);
        Assert.Equal(0, r.ConverterIterationFailures);
        double v0 = pf.Vm[pf.BusIds.ToList().IndexOf(3)];
        double worstV = c.TerminalVoltagePu.Max(v => Math.Abs(v - v0));
        double worstP = c.ActivePowerPu.Max(p => Math.Abs(p - 0.85));
        double worstW = r.Machines.Max(mt => mt.SpeedPu.Max(Math.Abs));
        Assert.True(worstV <= 1e-9, $"V moved {worstV}");
        Assert.True(worstP <= 1e-9, $"P moved {worstP}");
        Assert.True(worstW <= 1e-9, $"speed moved {worstW}");
        Assert.All(c.InDipMode, d => Assert.False(d));
    }

    [Fact]
    public void Fault_at_the_wecc_unit_bus_rides_through_on_reactive_current_and_recovers()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9Wecc();
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, faultBusId: 3,
            onsetSeconds: 0.1, clearingSeconds: 0.1, stepSeconds: 0.002, durationSeconds: 4.0);
        Assert.True(r.StayedInSynchronism);
        Assert.Equal(0, r.ConverterIterationFailures);
        RmsConverterTrace c = Assert.Single(r.Converters!);
        List<double> t = r.Machines[0].TimeSeconds.ToList();
        int mid = t.FindIndex(x => x >= 0.15);
        Assert.True(c.InDipMode[mid]);
        Assert.True(c.TerminalVoltagePu[mid] < 0.05, $"V {c.TerminalVoltagePu[mid]}");
        Assert.Equal(0.0, c.ActiveCurrentPu[mid]);                      // LVACM: no active current at a collapsed terminal
        Assert.True(c.ReactiveCurrentPu[mid] > 0.5, $"Iq {c.ReactiveCurrentPu[mid]}");   // Kqv injection, exporting
        Assert.False(c.InDipMode[^1]);
        // Pre-fault power restored: the power order is back at 0.85; the output P = |V| Pord / Vt_filt still moves with the
        // machines' post-fault swing (about 1e-3 at 4 s), so two decimals.
        Assert.Equal(0.85, c.ActivePowerPu[^1], 2);
    }

    [Fact]
    public void Repeated_fault_runs_are_bit_identical()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9Wecc();
        MultiMachineSubtransient.Result a = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 1.0);
        MultiMachineSubtransient.Result b = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 1.0);
        Assert.Equal(a.Converters![0].ActivePowerPu, b.Converters![0].ActivePowerPu);
        Assert.Equal(a.Converters![0].ReactivePowerPu, b.Converters![0].ReactivePowerPu);
        Assert.Equal(a.Machines[0].DeltaRad, b.Machines[0].DeltaRad);
    }

    // ---------------------------------------------------------------- PSS/E DYR

    internal static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    internal const string RegcRecord = "     2 'REGCA1' 1  1  0.02  10.0  0.9  0.4  1.22  1.2  0.8  0.4  -1.3  0.02  0.7  9999.0  -9999.0  0.7 /";

    internal const string ReecRecord = "     2 'REECA1' 1  0  0  1  1  0  0\n"
        + "   0.9  1.1  0.02  -0.05  0.05  2.0  1.05  -1.05  0.0  0.0  0.0  0.0  0.05  0.6  -0.6  1.1  0.9  0.0  0.1  0.0  40.0  0.0\n"
        + "   0.02  99.0  -99.0  1.0  0.0  1.2  0.02  0.0 1.2 0.2 1.2 0.5 1.2 1.0 1.2  0.0 1.1 0.2 1.1 0.5 1.1 1.0 1.1 /";

    // case14.dyr with every bus-2 machine record (GENSAL over two lines, IEEET1, PSS1A, HYGOV) replaced by the records given.
    internal static string Dyr(params string[] bus2)
    {
        string[] lines = File.ReadAllText(Fixture("case14.dyr")).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = lines.Where(l => l.Length > 0 && !l.StartsWith("     2 ", StringComparison.Ordinal) && !l.StartsWith("                    1.25", StringComparison.Ordinal));
        return string.Join("\n", kept.Concat(bus2)) + "\n";
    }

    [Fact]
    public void Regca1_and_reeca1_records_map_value_for_value()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture("case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(Dyr(RegcRecord, ReecRecord), raw, "case14", new PsseDyrOptions { SourceName = "case14.dyr" });
        Assert.Equal(4, r.Machines);
        (int k, WeccPlant plant) = Assert.Single(r.Renewables.Select(kv => (kv.Key, kv.Value)));
        Assert.Equal(2, raw.Model.Generators[k].Bus);
        Assert.Equal("REGC_A + REEC_A", plant.Chain);
        RegcaParameters g = plant.Regc!;
        Assert.Equal((1, 0.02, 10.0, 0.9, 0.4, 1.22, 1.2), (g.Lvplsw, g.Tg, g.Rrpwr, g.Brkpt, g.Zerox, g.Lvpl1, g.Volim));
        Assert.Equal((0.8, 0.4, -1.3, 0.02, 0.7, 9999.0, -9999.0, 0.7), (g.Lvpnt1, g.Lvpnt0, g.Iolim, g.Tfltr, g.Khv, g.Iqrmax, g.Iqrmin, g.Accel));
        ReecParameters e = plant.Reec!;
        Assert.Equal((ReecVariant.A, 0, 0, 1, 1, 0, 0, 0), (e.Variant, e.RemoteBus, e.PfFlag, e.VFlag, e.QFlag, e.PFlag, e.PqFlag, (int)e.Vref0));
        Assert.Equal((0.9, 1.1, 0.02, -0.05, 0.05, 2.0, 1.05, -1.05), (e.Vdip, e.Vup, e.Trv, e.Dbd1, e.Dbd2, e.Kqv, e.Iqh1, e.Iql1));
        Assert.Equal((0.05, 0.6, -0.6, 1.1, 0.9, 0.1, 40.0, 0.02), (e.Tp, e.Qmax, e.Qmin, e.Vmax, e.Vmin, e.Kqi, e.Kvi, e.Tiq));
        Assert.Equal((99.0, -99.0, 1.0, 0.0, 1.2, 0.02), (e.DPmax, e.DPmin, e.Pmax, e.Pmin, e.Imax, e.Tpord));
        Assert.Equal(new[] { 0.0, 0.2, 0.5, 1.0 }, e.Vq);
        Assert.Equal(new[] { 1.2, 1.2, 1.2, 1.2 }, e.Iq);
        Assert.Equal(new[] { 1.1, 1.1, 1.1, 1.1 }, e.Ip);
        Assert.Contains(r.Notes, n => n.Contains("REGC_A Accel = 0.7 is a PSS/E network-solution acceleration factor", StringComparison.Ordinal));
        Assert.Equal("PSS/E DYR case14.dyr, line(s) 11, 12", plant.Source);
    }

    [Fact]
    public void A_dyr_wecc_unit_runs_in_an_rms_study_at_rest_and_through_a_fault()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture("case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(Dyr(RegcRecord, ReecRecord), raw, "case14");
        GridModel m = raw.Model;
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-wecc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        MachineDynamicsSet set;
        try
        {
            string path = Path.Combine(dir, "case14.dyr.dynamics.json");
            File.WriteAllText(path, r.Json);
            set = MachineDynamicsLoader.Load(path, Path.Combine(CliProcess.Root(), "schemas", "v2"), m).WithRenewables(r.Renewables);
        }
        finally { Directory.Delete(dir, true); }
        var data = new List<SubtransientInit.MachineData>();
        for (int k = 0; k < m.Generators.Count; k++)
        {
            SubtransientInit.MachineData d = set.For(k)?.Data
                ?? new SubtransientInit.MachineData(3.0, 0.0, 1.6, 0.25, 0.18, 1.55, 0.4, 0.18, 7.0, 0.03, 0.5, 0.05);
            // The fixture's governors are left out: its TGOV1 valve limit (1.1 pu) cannot hold the slack unit's 2.32 pu
            // dispatch, which the governor initialisation refuses (R252); this test is about the renewable unit.
            d = d with { Governor = null };
            data.Add(set.Renewables.TryGetValue(k, out WeccPlant? plant) ? d with { Renewable = plant } : d);
        }
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 4, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        RmsConverterTrace c = Assert.Single(rest.Converters!);
        Assert.Equal(2, c.Bus);
        double p0 = c.ActivePowerPu[0];
        Assert.Equal(0.4, p0, 9);
        Assert.True(c.ActivePowerPu.Max(p => Math.Abs(p - p0)) <= 1e-9);
        Assert.True(c.TerminalVoltagePu.Max(v => Math.Abs(v - c.TerminalVoltagePu[0])) <= 1e-9);

        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(m, pf, data, 4, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 3.0);
        Assert.True(fault.StayedInSynchronism);
        Assert.Equal(0, fault.ConverterIterationFailures);
        RmsConverterTrace cf = fault.Converters![0];
        Assert.Contains(true, cf.InDipMode);
        Assert.Equal(0.4, cf.ActivePowerPu[^1], 3);
    }

    [Theory]
    [InlineData("1.2  0.8  0.4  -1.3", "1.2  0.8  0.4  1.3", "REGC_A Iolim = 1.3: must be 0 or negative")]
    [InlineData("     2 'REECA1' 1  0  0  1  1  0  0", "     2 'REECA1' 1  0  0  1  1  0  4", "REEC_A PqFlag = 4: must be one of 0, 1")]
    [InlineData("     2 'REECA1' 1  0  0  1  1  0  0", "     2 'REECA1' 1  7  0  1  1  0  0", "REEC_A remote voltage bus 7: only the unit's own terminal (0) is modelled")]
    public void Renewable_values_out_of_range_are_refused_with_the_unit_and_line(string find, string replace, string expected)
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture("case14.raw"));
        string text = Dyr(RegcRecord, ReecRecord);
        Assert.Contains(find, text, StringComparison.Ordinal);
        text = text.Replace(find, replace, StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(text, raw, "case14"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("for machine '1' at bus 2", ex.Message, StringComparison.Ordinal);
        PsseDyrResult allowed = PsseDyrReader.Read(text, raw, "case14", new PsseDyrOptions { AllowUnmodelled = true });
        Assert.Empty(allowed.Renewables);   // left out, listed
        Assert.Contains(allowed.Notes, n => n.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Chain_rules_are_enforced()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture("case14.raw"));
        // An electrical control without its REGC_A.
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(Dyr(ReecRecord), raw, "case14"));
        Assert.Contains("REEC_A for machine '1' at bus 2 (line 11) has no REGC_A record for its unit", ex.Message, StringComparison.Ordinal);
        // An exciter on a renewable unit.
        ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(Dyr(RegcRecord, ReecRecord, "     2 'SEXS'   1   0.1  10.0  100.0  0.05  0.0  4.0 /"), raw, "case14"));
        Assert.Contains("SEXS for machine '1' at bus 2 (line 15): a renewable converter unit (REGC_A) has no field or shaft for a exciter", ex.Message, StringComparison.Ordinal);
        // A wrong count.
        ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(Dyr(RegcRecord.Replace(" 0.7 /", " /", StringComparison.Ordinal)), raw, "case14"));
        Assert.Contains("REGC_A needs exactly 15 values after the machine ID (1 ICONs, 14 CONs); the record has 14", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wecc_and_v33_user_model_names_and_usrmdl_records_are_the_same_model()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Fixture("case14.raw"));
        WeccPlant Plant(params string[] recs) => PsseDyrReader.Read(Dyr(recs), raw, "case14").Renewables.Single().Value;
        WeccPlant a = Plant(RegcRecord, ReecRecord);
        WeccPlant b = Plant(RegcRecord.Replace("'REGCA1'", "'REGC_A'", StringComparison.Ordinal), ReecRecord.Replace("'REECA1'", "'REECAU1'", StringComparison.Ordinal));
        Assert.Equal(a.Regc, b.Regc);
        Assert.Equal(a.Reec!.Kvi, b.Reec!.Kvi);
        // USRMDL: 'REGCAU1' IC IT NI NC NS NV, then the values; NI / NC must match.
        string usr = RegcRecord.Replace("'REGCA1' 1  1", "'USRMDL' 1 'REGCAU1' 101 1 1 14 3 0  1", StringComparison.Ordinal);
        Assert.Equal(a.Regc, Plant(usr, ReecRecord).Regc);
        var ex = Assert.Throws<InvalidDataException>(() => Plant(usr.Replace("101 1 1 14", "101 1 2 14", StringComparison.Ordinal), ReecRecord));
        Assert.Contains("USRMDL REGC_A declares NI = 2 and NC = 14; GridSim's REGC_A record has 1 ICONs and 14 CONs", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// WECC expansion: the bits of a WECC fault ride-through pinned, so the same digest on every SIMD tier
/// (DOTNET_EnableHWIntrinsic=0, DOTNET_EnableAVX2=0, default) and every run proves the RMS engine with a WECC unit is
/// bit-identical across them. The digest covers every converter and machine trace sample of the WSCC-9 bus-3 fault run.
/// </summary>
public class WeccRenewableDigestTests
{
    internal static string Digest(MultiMachineSubtransient.Result r)
    {
        var bytes = new List<byte>();
        void Add(IEnumerable<double> xs) { foreach (double x in xs) bytes.AddRange(BitConverter.GetBytes(x)); }
        foreach (RmsConverterTrace c in r.Converters!)
        {
            Add(c.TerminalVoltagePu); Add(c.ActivePowerPu); Add(c.ReactivePowerPu); Add(c.ActiveCurrentPu); Add(c.ReactiveCurrentPu);
            foreach (bool d in c.InDipMode) bytes.Add(d ? (byte)1 : (byte)0);
        }
        foreach (MultiMachineSubtransient.MachineTrace m in r.Machines) { Add(m.DeltaRad); Add(m.SpeedPu); Add(m.EqppPu); Add(m.EddpPu); }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
    }

    [Fact]
    public void Wscc9_wecc_fault_run_has_the_pinned_digest()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var xdp = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        var reg = new RegcaParameters();
        var reec = new ReecParameters { Qmax = 0.6, Qmin = -0.6, Imax = 1.2, Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.2, 1.2, 1.2, 1.2 },
            Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.2, 1.2, 1.2, 1.2 }, QFlag = 1, VFlag = 1, Thld = 0.05, Iqfrz = 0.1, Thld2 = 0.05 };
        var data = m.Generators.Select(g =>
        {
            double x = xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus == 3 ? d with { Renewable = new WeccPlant(reg, reec) } : d;
        }).ToList();
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        string d = Digest(r);
        Assert.True(d == "469F56FB408DB49A46FD097FBB8B2C25CE650DD91863B95471156926AABF1B6F", "digest " + d);
    }
}
