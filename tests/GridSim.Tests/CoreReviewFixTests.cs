using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Dgs;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit.Ena;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression tests for the 2.1.098 core fixes. Each expected value is derived in the test from the physics or the
/// standard, not read back from the code under test:
/// <list type="bullet">
/// <item>PSS/E two-winding impedance referred through WINDV2 (PSS/E PAG eq. 4.21b; OpenIPSL TwoWindingTransformer).</item>
/// <item>DGS transformer impedance referred to the LV terminal base.</item>
/// <item>Battery entropic heat -I T dU/dT (Bernardi, Pawlikowski and Newman 1985).</item>
/// <item>CGMES node-breaker transformer rated voltages against the node base voltages.</item>
/// <item>G74 iec60909-q motor decay mu q Ik'' (IEC 60909-0).</item>
/// <item>RMS sequence faults: dynamic loads in the negative sequence, and balanced faults beside an unbalanced one.</item>
/// </list>
/// </summary>
public sealed class CoreReviewFixTests
{
    private static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void Rel(double want, double got, double rel, string what)
        => Assert.True(Math.Abs(want - got) <= rel * Math.Max(1.0, Math.Abs(want)), $"{what}: want {want:R}, got {got:R}");

    private static void Close(Complex want, Complex got, double rel, string what)
        => Assert.True((want - got).Magnitude <= rel * Math.Max(1.0, want.Magnitude), $"{what}: want {want}, got {got}");

    // ---- PSS/E two-winding transformer: Z x w2^2 ----------------------------------------------------------------------

    /// <summary>The two-bus RAW of <see cref="PsseTapControlTests"/> (bus 1 at 132 kV, bus 2 at 33 kV, one transformer
    /// with tap control off), with R = 0.01 and X = 0.10 on the system base (CZ 1).</summary>
    private static string TwoBus() => PsseTapControlTests.TwoBus(10, 5, cod: 0).Replace("\r\n", "\n")
        .Replace("\n0, 0.1, 100\n", "\n0.01, 0.1, 100\n");

    private static Branch Transformer(string text) =>
        PsseRawParser.ParseWithDiagnostics(text, "w2", new PsseReadOptions { FrequencyHz = 50 }).Model.Branches.Single();

    /// <summary>
    /// The unit is t1:1, then Z, then 1:t2. Referred to bus I with GridSim's single from-side tap a = t1/t2:
    /// Vm1 = Vi/t1, Vm2 = Vj/t2, Im = t2 Ij, so Vi/(t1/t2) - Vj = Z t2^2 Ij. With t1 = 1, t2 = 1.05: tap = 1/1.05 and
    /// Z' = (0.01 + j0.10) x 1.1025 = 0.011025 + j0.11025. The same unit written with CW 2 (kV) and CW 3 (pu of NOMV)
    /// gives the same w2 and so the same R and X.
    /// </summary>
    [Fact]
    public void Psse_two_winding_impedance_is_referred_through_windv2_for_every_cw()
    {
        string cw1 = TwoBus().Replace("\n1, 0\n", "\n1.05, 0\n");
        // CW 2: WINDV1 = 132 kV on the 132 kV bus, WINDV2 = 34.65 kV on the 33 kV bus (1.05 pu).
        string cw2 = TwoBus().Replace("\n1, 2, 0, '1', 1, 1, 1,", "\n1, 2, 0, '1', 2, 1, 1,")
            .Replace("\n1, 0, 0, 200, 200, 200, ", "\n132, 0, 0, 200, 200, 200, ").Replace("\n1, 0\n", "\n34.65, 0\n");
        // CW 3: WINDV2 = 1.1 pu of NOMV2 = 31.5 kV, which is 34.65 kV, 1.05 pu of the 33 kV bus.
        string cw3 = TwoBus().Replace("\n1, 2, 0, '1', 1, 1, 1,", "\n1, 2, 0, '1', 3, 1, 1,").Replace("\n1, 0\n", "\n1.1, 31.5\n");
        foreach ((string name, string text) in new[] { ("CW 1", cw1), ("CW 2", cw2), ("CW 3", cw3) })
        {
            Assert.NotEqual(TwoBus(), text);
            Branch t = Transformer(text);
            Rel(0.01 * 1.05 * 1.05, t.R, 1e-14, name + " R");
            Rel(0.10 * 1.05 * 1.05, t.X, 1e-14, name + " X");
            Rel(1.0 / 1.05, t.TapRatio, 1e-14, name + " tap");
        }
        // w2 = 1: the factor is exactly 1.0, so R and X are the file's own values to the bit.
        Branch plain = Transformer(TwoBus());
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.01), BitConverter.DoubleToInt64Bits(plain.R));
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.1), BitConverter.DoubleToInt64Bits(plain.X));
    }

    /// <summary>An impedance correction table scales the referred impedance: table F = 1.5 at the winding-1 ratio 1.0, so
    /// Z = (0.01 + j0.10) x 1.1025 x 1.5.</summary>
    [Fact]
    public void Psse_impedance_table_applies_to_the_referred_impedance()
    {
        string text = TwoBus().Replace("\n1, 0\n", "\n1.05, 0\n")
            .Replace(", 1.03, 0.97, 21, 0, 0, 0, 0\n", ", 1.03, 0.97, 21, 1, 0, 0, 0\n")
            .Replace("0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA\n", "0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA\n1, 0.9, 1.2, 1.0, 1.5, 1.1, 1.2\n");
        Assert.Contains("\n1, 0.9, 1.2, 1.0, 1.5", text, StringComparison.Ordinal);
        Branch t = Transformer(text);
        Rel(0.01 * 1.1025 * 1.5, t.R, 1e-14, "R");
        Rel(0.10 * 1.1025 * 1.5, t.X, 1e-14, "X");
    }

    // ---- DGS transformer: Z x (utrn_l / uknom_LV)^2 -------------------------------------------------------------------

    /// <summary>
    /// strn 100 MVA, 33 / 11.5 kV on 33 / 11 kV terminals, uktr 10 %, no copper loss. The short-circuit reactance in ohms
    /// on the LV side is 0.10 x 11.5^2 / 100 = 0.13225 ohm; the LV terminal base is 11^2 / 100 = 1.21 ohm, so
    /// X = 0.13225 / 1.21 = 0.10929752... pu. The ratio is (33/33) / (11.5/11) = 11 / 11.5.
    /// </summary>
    [Fact]
    public void Dgs_transformer_impedance_is_referred_to_the_lv_terminal_base()
    {
        string text = GridSim.Tests.Substations.DgsFixtures.Header + string.Join("\n",
            "$$ElmTerm;ID(a:40);uknom(r:20)", "H;33", "L;11",
            "$$TypTr2;ID(a:40);strn(r:20);utrn_h(r:20);utrn_l(r:20);uktr(r:20);pcutr(r:20)", "TT;100;33;11.5;10;0",
            "$$ElmTr2;ID(a:40);typ_id(p:40);nntap(i:4)", "TR1;TT;0",
            "$$ElmXnet;ID(a:40);bustp(a:2)", "X;SL",
            "$$StaCubic;ID(a:40);fold_id(p:40);obj_id(p:40);obj_bus(i:2)", "C1;H;X;0", "C2;H;TR1;0", "C3;L;TR1;1") + "\n";
        DgsImportResult r = DgsImporter.Import(DgsTableReader.Read(text), new DgsImportOptions(true));
        NbTransformer t = r.Model.Transformers.Single();
        Rel(0.13225 / 1.21, t.X, 1e-14, "X");
        Assert.Equal(0.0, t.R);
        Rel(11.0 / 11.5, t.TapRatio, 1e-15, "tap");
    }

    // ---- Battery entropic heat: -I T dU/dT ----------------------------------------------------------------------------

    /// <summary>
    /// The heat of a cell is q = I (U - V) - I T dU/dT with discharge-positive current (Bernardi, Pawlikowski and Newman
    /// 1985). From rest (V1 = V2 = 0) the irreversible part of one step is even in I, so Step(+I) - Step(-I) is the
    /// reversible part alone: -2 I T dU/dT dt. LFP has dU/dT = -0.05 mV/K; at 298.15 K, I = 100 A and dt = 1 s that is
    /// +2.9815 J: the cell heats on discharge.
    /// </summary>
    [Fact]
    public void Battery_entropic_heat_has_the_bernardi_sign()
    {
        var cell = new CellModel(BatteryChemistryLibrary.Lfp, 0.5, 25.0);
        CellModel discharge = cell.Clone(), charge = cell.Clone();
        double qd = discharge.Step(100.0, 1.0), qc = charge.Step(-100.0, 1.0);
        double dUdT = BatteryChemistryLibrary.Lfp.EntropicCoefficient[0.5];
        Assert.Equal(-0.05e-3, dUdT);
        Rel(-2.0 * 100.0 * cell.TemperatureK * dUdT, qd - qc, 1e-12, "reversible heat difference");
        Rel(2.9815, qd - qc, 1e-9, "2.9815 J");
        Assert.True(qd > qc);
    }

    // ---- CGMES node-breaker: ratedU against the node base --------------------------------------------------------------

    /// <summary>
    /// The exporter writes the transformer of <see cref="GridSim.Tests.Substations.CgmesProfilesTests.Rich"/> with ratedU
    /// equal to its node voltages (110 / 20 kV). Rewritten as a 120 / 21 kV unit on the same nodes: Z in ohms on the 120 kV
    /// side is x / (120^2 / 100) pu on the rating, the same pu on 21 kV, and on the 20 kV node base x (21/20)^2. The nominal
    /// ratio on the node bases is (120/110) / (21/20) = 1.038961..., times the tap changer's own ratio.
    /// </summary>
    [Fact]
    public void Cgmes_node_breaker_transformer_rated_off_its_node_voltages_gets_the_ratio_and_referred_impedance()
    {
        CgmesExportResult e = CgmesSubstationExporter.Export(GridSim.Tests.Substations.CgmesProfilesTests.Rich());
        NbTransformer Read(Func<string, string> edit, out IReadOnlyList<string> notes)
        {
            CgmesReadResult r = CgmesSubstationReader.Read(e.Files.Select(f => (f.Kind, f.FileName, edit(f.Text))));
            notes = r.Notes;
            return r.Model.Transformers.Single();
        }
        NbTransformer before = Read(s => s, out IReadOnlyList<string> plainNotes);
        Assert.DoesNotContain(plainNotes, n => n.Contains("differ from its nodes' base voltages", StringComparison.Ordinal));
        string eq = e.Files.Single(f => f.Text.Contains("PowerTransformerEnd.ratedU>110<", StringComparison.Ordinal)).Text;
        double xOhm = double.Parse(Regex.Match(eq, @"PowerTransformerEnd\.x>([^<]+)<").Groups[1].Value, CultureInfo.InvariantCulture);
        double rOhm = double.Parse(Regex.Match(eq, @"PowerTransformerEnd\.r>([^<]+)<").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(xOhm > 0 && rOhm > 0);
        string Rated(string s) => s.Replace("PowerTransformerEnd.ratedU>110<", "PowerTransformerEnd.ratedU>120<", StringComparison.Ordinal)
            .Replace("PowerTransformerEnd.ratedU>20<", "PowerTransformerEnd.ratedU>21<", StringComparison.Ordinal);
        Assert.NotEqual(eq, Rated(eq));
        NbTransformer after = Read(Rated, out IReadOnlyList<string> notes);
        double n2 = 21.0 / 20.0, zb = 120.0 * 120.0 / 100.0;
        Rel(xOhm / zb * n2 * n2, after.X, 1e-14, "X");
        Rel(rOhm / zb * n2 * n2, after.R, 1e-14, "R");
        Rel(before.TapRatio * ((120.0 / 110.0) / (21.0 / 20.0)), after.TapRatio, 1e-15, "tap");
        Rel(1.038961038961039, (120.0 / 110.0) / (21.0 / 20.0), 1e-15, "nominal ratio");
        Assert.Equal(before.PhaseShiftDeg, after.PhaseShiftDeg);
        Assert.Contains(notes, n => n.Contains("its rated voltages (120 / 21 kV) differ from its nodes' base voltages (110 / 20 kV)", StringComparison.Ordinal));
    }

    // ---- G74 iec60909-q motor: mu q Ik'' ------------------------------------------------------------------------------

    private const double G74BaseMva = 100.0, G74Kv = 132.0, C = 1.10;
    private static double IBaseKa => G74BaseMva / (Math.Sqrt(3.0) * G74Kv);

    private static GridModel G74Case() => new()
    {
        BaseMva = G74BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: G74Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: 0.2, Name: "G1") },
        Branches = Array.Empty<Branch>(),
    };

    private static string G74Json(double ratedMw) =>
        "{\"schema\":\"gridsim-g74-data/1\",\"generators\":[{\"bus\":1,\"xdTransPu\":0.3,\"xdSyncPu\":1.2,\"tdSubS\":0.05,\"tdTransS\":1.0}],"
        + "\"motors\":[{\"name\":\"M1\",\"bus\":1,\"ratedMva\":10,\"lockedRotorCurrentRatio\":6,\"decay\":\"iec60909-q\",\"ratedMw\":" + Inv(ratedMw) + ",\"polePairs\":2}]}";

    /// <summary>
    /// IEC 60909-0: an asynchronous motor's breaking current is mu q Ik''M. Motor Ik'' = c x 6 x 10/100 = 0.66 pu and
    /// IrM = 0.1 pu, so Ik''M/IrM = 6.6; at tmin = 0.1 s, mu = 0.62 + 0.72 e^(-0.32 x 6.6) = 0.707117 and, with
    /// m = 8 MW / 2 pole pairs = 4 MW, q = 0.57 + 0.12 ln 4 = 0.736355. The generator part is the two-exponential envelope.
    /// At t = 0 the motor gives Ik''.
    /// </summary>
    [Fact]
    public void G74_iec_q_motor_decay_applies_mu_times_q_and_starts_at_the_initial_current()
    {
        G74BusResult r = G74Method.Compute(G74Case(), 1, G74Data.Parse(G74Json(8.0)), new G74Options(new[] { 0.0, 0.1 }, BreakTimeSeconds: 0.1));
        Assert.Equal(G74Status.Ok, r.Status);
        double mot = C / ((1.0 / 6.0) * 100.0 / 10.0);   // 0.66 pu
        double mu = 0.62 + 0.72 * Math.Exp(-0.32 * 6.6), q = 0.57 + 0.12 * Math.Log(4.0);
        Rel(0.707117, mu, 1e-6, "mu");
        Rel(0.736355, q, 1e-6, "q");
        double t = 0.1;
        double genT = (C / 0.2 - C / 0.3) * Math.Exp(-t / 0.05) + (C / 0.3 - C / 1.2) * Math.Exp(-t / 1.0) + C / 1.2;
        Rel((genT + mot * mu * q) * IBaseKa, r.BreakKa[1], 1e-9, "break current at 0.1 s");
        Rel((genT + mot * mu * q) * IBaseKa, r.BreakAtTbKa, 1e-9, "break current at tb");
        Rel((C / 0.2 + mot) * IBaseKa, r.BreakKa[0], 1e-12, "initial current");
        G74Contribution m = r.Contributions.Single(c => c.Kind == "induction-motor");
        Rel(mot * mu * q * IBaseKa, m.AtTimesKa[1], 1e-9, "motor part at 0.1 s");
    }

    /// <summary>A small motor (m = 0.1 MW / 2 = 0.05 MW per pole pair) at t = 0 gives Ik'' exactly (before, q(0.02) =
    /// 1.03 + 0.12 ln 0.05 = 0.6705 of it). A time between 0 and 0.02 s, where IEC gives no factor, is refused.</summary>
    [Fact]
    public void G74_iec_q_motor_at_time_zero_is_the_initial_current_and_times_below_20_ms_are_refused()
    {
        G74BusResult r = G74Method.Compute(G74Case(), 1, G74Data.Parse(G74Json(0.1)), new G74Options(new[] { 0.0 }));
        Assert.Equal(G74Status.Ok, r.Status);
        G74Contribution m = r.Contributions.Single(c => c.Kind == "induction-motor");
        Assert.Equal(m.IkppKa, m.AtTimesKa[0]);
        Rel(0.66 * IBaseKa, m.AtTimesKa[0], 1e-12, "motor at t = 0");

        G74BusResult early = G74Method.Compute(G74Case(), 1, G74Data.Parse(G74Json(0.1)), new G74Options(new[] { 0.01 }));
        Assert.Equal(G74Status.Failed, early.Status);
        Assert.Contains(early.Reasons, s => s.Contains("between 0 and 0.02 s", StringComparison.Ordinal));
        G74BusResult earlyTb = G74Method.Compute(G74Case(), 1, G74Data.Parse(G74Json(0.1)), new G74Options(new[] { 0.1 }, BreakTimeSeconds: 0.015));
        Assert.Equal(G74Status.Failed, earlyTb.Status);
        // With decay off the motor factors are not used, so the time is not refused.
        Assert.Equal(G74Status.Ok, G74Method.Compute(G74Case(), 1, G74Data.Parse(G74Json(0.1)), new G74Options(new[] { 0.01 }, Decay: false)).Status);
    }

    // ---- RMS sequence faults ------------------------------------------------------------------------------------------

    private static IReadOnlyList<SubtransientInit.MachineData> Genrou(GridModel model)
        => model.Generators.Select(g => new SubtransientInit.MachineData(
            H: 5.0, Damping: 0.0, Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    /// <summary>A machine at bus 1 (X2 = 0.2 pu on 100 MVA) feeding a radial chain of j0.1 lines (no charging).</summary>
    private static (GridModel Model, PowerFlowResult Pf) Radial(int buses, double pd2 = 0.0, double qd2 = 0.0)
    {
        var bus = new List<Bus> { new(1, BusType.Slack, BaseKv: 132) };
        var br = new List<Branch>();
        for (int i = 2; i <= buses; i++)
        {
            bus.Add(new Bus(i, BusType.Pq, Pd: i == 2 ? pd2 : 0.0, Qd: i == 2 ? qd2 : 0.0, BaseKv: 132));
            br.Add(new Branch(i - 1, i, 0.0, 0.1));
        }
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = bus,
            Branches = br,
            Generators = new[] { new Generator(Bus: 1, Pg: pd2, MBase: 100, H: 5.0, Name: "G1", SubtransientXdPu: 0.22, NegativeSeqXPu: 0.2) },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        return (m, pf);
    }

    private static RmsFaultStudyResult Run(GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data, params RmsEvent[] events)
        => RmsTransientStability.StudyEventsWithFaults(m, pf, data, events, stepSeconds: 0.005, durationSeconds: 0.3);

    // Overbye's CIM5 example motor (Ra 0.01, Xa 0.06, Xm 4, R1 0.03, X1 0.04 on its own base).
    private const double Ra = 0.01, Xa = 0.06, Xm = 4.0, R1 = 0.03, X1 = 0.04;

    /// <summary>The equivalent circuit Ra + jXa in series with jXm in parallel with R1/s + jX1, written here.</summary>
    private static Complex Circuit(double s)
    {
        var rotor = new Complex(R1 / s, X1);
        return new Complex(Ra, Xa) + new Complex(0.0, Xm) * rotor / (new Complex(0.0, Xm) + rotor);
    }

    /// <summary>
    /// An induction motor's negative-sequence impedance is about Rs + R'r/(2 - s) + j(Xs + X'r), close to its locked-rotor
    /// value, so the network stamps it by its Norton admittance k / (Ra + jX') (X' = Xa + Xm X1 / (Xm + X1)) plus the
    /// constant shunt that makes up the bus Q, as in the positive sequence. Two buses: Z2 at bus 2 is
    /// 1 / (1 / j(0.2 + 0.1) + k / (Ra + jX') + yComp), with yComp = -j (Q/S - Qmotor) / |V|^2 and Qmotor found here from
    /// the circuit at the slip where it draws P. Before, the bus took its full P + jQ as a constant admittance.
    /// </summary>
    [Fact]
    public void Rms_negative_sequence_carries_a_dynamic_load_by_its_norton_admittance()
    {
        const double p = 40.0, q = 19.0, motorMva = 50.0;
        (GridModel m, PowerFlowResult pf) = Radial(2, p, q);
        var motor = InductionMotorParameters.FromCircuit(Ra, Xa, Xm, R1, X1, 1.0, InductionMotorTorque.PowerLaw(2.0), m.FrequencyHz);
        var data = new RmsLoadedMachineData(Genrou(m), new DynamicLoadData[] { new InductionMotorLoadData(2, p, q, motor, motorMva) });
        RmsFaultStudyResult st = Run(m, pf, data, new RmsSequenceFault(0.05, 2, FaultType.LineToLine), new RmsFaultClear(0.1, 2));
        RmsFaultSample s = st.Faults.Single().Samples[0];

        int i2 = pf.BusIds.ToList().IndexOf(2);
        double v = pf.Vm[i2], v2 = v * v;
        double power = p / motorMva;
        double lo = 1e-12, hi = 0.1;
        for (int k = 0; k < 400; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid == lo || mid == hi) break;
            if (v2 * (Complex.One / Circuit(mid)).Real < power) lo = mid; else hi = mid;
        }
        Complex z = Circuit(0.5 * (lo + hi));
        double kBase = motorMva / 100.0;
        double qMotor = v2 * z.Imaginary / (z.Real * z.Real + z.Imaginary * z.Imaginary) * kBase;
        Complex yComp = new Complex(0.0, -(q / 100.0 - qMotor)) / v2;
        double xp = Xa + Xm * X1 / (Xm + X1);
        Complex yMotor = kBase / new Complex(Ra, xp);
        Complex want = Complex.One / (Complex.One / new Complex(0.0, 0.3) + yMotor + yComp);
        Close(want, s.Z2Pu, 1e-9, "Z2");
        // The static admittance of the whole load would give a clearly different value.
        Complex yStatic = Complex.Conjugate(new Complex(p, q) / 100.0) / v2;
        Complex old = Complex.One / (Complex.One / new Complex(0.0, 0.3) + yStatic);
        Assert.True((old - want).Magnitude > 0.01, $"{old} vs {want}");
    }

    /// <summary>
    /// A balanced fault is the shunt Yf from each phase to earth; the symmetrical-component transform of the diagonal
    /// Yf I is Yf I, so the shunt is in every sequence network. Radial 1-2-3 (j0.1 lines, X2 = j0.2 at bus 1), a three-phase
    /// fault through j0.05 at bus 2 and a single line to ground fault at bus 3:
    /// Z2 = j0.1 + (j0.05 || j0.3) = j0.142857...; Z0 = 3 x j0.1 (line 2-3, zero sequence 3 Z1) + j0.05 = j0.35, the
    /// three-phase fault being the only earth path (no machine or winding earthing). Before, the SLG fault saw neither, so
    /// the point was unearthed and drew nothing.
    /// </summary>
    [Fact]
    public void Rms_unbalanced_fault_sees_a_balanced_fault_at_another_bus_in_its_negative_and_zero_sequence()
    {
        (GridModel m, PowerFlowResult pf) = Radial(3);
        RmsFaultStudyResult st = Run(m, pf, Genrou(m),
            new RmsSequenceFault(0.05, 2, FaultType.ThreePhase, 0.0, 0.05), new RmsSequenceFault(0.1, 3, FaultType.SingleLineToGround),
            new RmsFaultClear(0.2, 2), new RmsFaultClear(0.25, 3));
        RmsFaultTrace slg = st.Faults.Single(f => f.Type == FaultType.SingleLineToGround);
        RmsFaultSample s = slg.Samples[0];
        Close(new Complex(0.0, 0.1 + 0.05 * 0.3 / 0.35), s.Z2Pu, 1e-12, "Z2");
        Assert.True(s.Grounded);
        Close(new Complex(0.0, 0.35), s.Z0Pu, 1e-12, "Z0");
        // I1 = I2 = I0 = V1 / (Z2 + Z0) for a bolted SLG fault.
        Close(s.V1 / (s.Z2Pu + s.Z0Pu), s.I0, 1e-12, "I0");
        Assert.True(s.EarthCurrentPu > 0.1, $"{s.EarthCurrentPu}");   // zero before: the point had no earth path
        // Once the three-phase fault clears (0.2 s), the SLG point has no earth path again and Z2 is j0.1 + j0.3.
        RmsFaultSample late = slg.Samples.First(x => x.TimeSeconds > 0.2 - 1e-9);
        Assert.False(late.Grounded);
        Close(new Complex(0.0, 0.4), late.Z2Pu, 1e-12, "Z2 after the clearance");
    }

    /// <summary>A bolted bus fault (the 1e7 pu shunt) and a three-phase fault along a line are stamped the same way.
    /// Line to line at bus 3: with a bolted fault at bus 2, Z2 = j0.1 + 1 / (1e7 + 1/j0.3); with a three-phase fault
    /// through j0.05 at the middle of line 1-2, Z2 = j0.1 + j0.05 + (j0.05 || (j0.05 + j0.2)).</summary>
    [Fact]
    public void Rms_unbalanced_fault_sees_a_bolted_bus_fault_and_a_three_phase_line_fault()
    {
        (GridModel m, PowerFlowResult pf) = Radial(3);
        RmsFaultStudyResult bolted = Run(m, pf, Genrou(m),
            new RmsBusFault(0.05, 2), new RmsSequenceFault(0.1, 3, FaultType.LineToLine), new RmsFaultClear(0.2, 3), new RmsFaultClear(0.2, 2));
        Close(new Complex(0.0, 0.1) + Complex.One / (new Complex(1e7, 0.0) + Complex.One / new Complex(0.0, 0.3)),
            bolted.Faults.Single().Samples[0].Z2Pu, 1e-12, "Z2 with a bolted fault");

        RmsFaultStudyResult line = Run(m, pf, Genrou(m),
            new RmsBranchSequenceFault(0.05, 0, 0.5, FaultType.ThreePhase, 0.0, 0.05), new RmsSequenceFault(0.1, 3, FaultType.LineToLine),
            new RmsFaultClear(0.2, 3), new RmsBranchFaultClear(0.2, 0));
        RmsFaultSample s = line.Faults.Single(f => f.Type == FaultType.LineToLine).Samples[0];
        Close(new Complex(0.0, 0.1 + 0.05 + 0.05 * 0.25 / 0.30), s.Z2Pu, 1e-12, "Z2 with a line fault");
    }

    /// <summary>Two unbalanced faults in place at once couple the sequence networks at two points (a cross-country fault),
    /// which one equivalent shunt per fault cannot represent: refused. The same two faults one after the other run.</summary>
    [Fact]
    public void Rms_two_overlapping_unbalanced_faults_are_refused()
    {
        (GridModel m, PowerFlowResult pf) = Radial(3);
        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => Run(m, pf, Genrou(m),
            new RmsSequenceFault(0.05, 2, FaultType.SingleLineToGround), new RmsBranchSequenceFault(0.1, 1, 0.5, FaultType.LineToLine),
            new RmsBranchFaultClear(0.15, 1), new RmsFaultClear(0.2, 2)));
        Assert.Contains("cross-country", ex.Message, StringComparison.Ordinal);
        Assert.Contains("events[0]", ex.Message, StringComparison.Ordinal);
        RmsFaultStudyResult ok = Run(m, pf, Genrou(m),
            new RmsSequenceFault(0.05, 2, FaultType.SingleLineToGround), new RmsFaultClear(0.1, 2),
            new RmsBranchSequenceFault(0.1, 1, 0.5, FaultType.LineToLine), new RmsBranchFaultClear(0.15, 1));
        Assert.Equal(2, ok.Faults.Count);
    }
}
