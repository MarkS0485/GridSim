using System.Globalization;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Several FACTS shunt elements and static var compensators holding one bus (PSS/E RMPCT sharing). The PSS/E RAW data
/// format defines RMPCT for a FACTS device as the "percent of the total Mvar required to hold the voltage at the bus
/// controlled by the shunt element of this FACTS device that are to be contributed by the shunt element", and the same
/// for a switched shunt; so the bus is held at the devices' common set-point and the reactive power its sources supply
/// is split RMPCT_k / sum(RMPCT). A device whose share passes its own limit is fixed at that limit and the others carry
/// the rest.
///
/// <para>Oracle: the test network of <see cref="PsseFactsTests.Case"/> puts bus 2 on the end of a lossless line of
/// X = 0.02 pu from the slack (1.0 pu, angle 0) with nothing else at bus 2, so bus 2 carries no active power and sits at
/// angle 0; held at V, the reactive power it sends into the line is, by hand, Q = (V^2 - V) / X on 100 MVA: 50.5 Mvar at
/// V = 1.01 pu. Every share below is checked against that number and the RMPCT ratios.</para>
/// </summary>
public class PsseSharedVoltageControlTests
{
    private static string I(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A STATCOM at bus 2 (MODE 1, no terminal bus): NAME, I, J, MODE, PDES, QDES, VSET, SHMX, TRMX, VTMN, VTMX,
    /// VSMX, IMX, LINX, RMPCT, OWNER, SET1, SET2, VSREF, REMOT, MNAME.</summary>
    private static string Statcom(string name, double vset, double shmx, double rmpct)
        => $"'{name}', 2, 0, 1, 0, 0, {I(vset)}, {I(shmx)}, 9999, 0.9, 1.1, 1.0, 0, 0.05, {I(rmpct)}, 1, 0, 0, 0, 0, ''";

    /// <summary>A revision 33 MODSW 2 switched shunt at bus 2: I, MODSW, ADJM, STAT, VSWHI, VSWLO, SWREM, RMPCT, RMIDNT,
    /// BINIT, N1, B1 (four 25 Mvar capacitor blocks, BINIT 0).</summary>
    private static string Svc(double vhi, double vlo, double rmpct) => $"2, 2, 0, 1, {I(vhi)}, {I(vlo)}, 0, {I(rmpct)}, '', 0, 4, 25";

    private static string Raw(string facts, string? svc = null)
    {
        string raw = PsseFactsTests.Case(facts);
        if (svc is not null)
            raw = raw.Replace("0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA",
                "0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA\n" + svc, StringComparison.Ordinal);
        return raw;
    }

    private static GridModel Read(string raw) =>
        PsseRawParser.ParseWithDiagnostics(raw, "shared", new PsseReadOptions { FrequencyHz = 50, ApplyShuntControl = true }).Model;

    private static int Ix(PowerFlowResult pf, int bus) => pf.BusIds.ToList().IndexOf(bus);

    private static void Close(double expected, double actual, string what, double rel = 1e-12)
        => Assert.True(Math.Abs(expected - actual) <= rel * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>The hand value: the reactive power bus 2 sends into its line when held at <paramref name="v"/> (Mvar).</summary>
    private static double HandQ(double v) => (v * v - v) / 0.02 * 100;

    [Fact]
    public void Two_statcoms_on_one_bus_hold_it_and_share_the_reactive_power_by_rmpct()
    {
        GridModel m = Read(Raw(Statcom("S75", 1.01, 500, 75) + "\n" + Statcom("S25", 1.01, 500, 25)));
        Assert.Equal(new[] { 75.0, 25.0 }, m.FactsDevices.Select(d => d.RmpctPercent));
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.FactsNote);
        Assert.Equal(1.01, pf.Vm[Ix(pf, 2)]);
        double total = pf.QInjMvar[Ix(pf, 2)];
        Close(HandQ(1.01), total, "bus 2 reactive power against the hand value", 1e-9);
        Close(0.75 * total, pf.FactsDevices![0].ShuntMvar, "S75's share");
        Close(0.25 * total, pf.FactsDevices[1].ShuntMvar, "S25's share");
        Assert.False(pf.FactsDevices[0].ShuntLimited);
        Assert.False(pf.FactsDevices[1].ShuntLimited);

        // The network solve is the one a single device holding the bus gives, to the bit: sharing only splits the result.
        PowerFlowResult one = new NewtonRaphsonPowerFlow().Solve(Read(Raw(Statcom("S", 1.01, 1000, 100))));
        Assert.True(one.Converged, one.FactsNote);
        Assert.Equal(one.Vm.Select(BitConverter.DoubleToInt64Bits), pf.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(one.VaDeg.Select(BitConverter.DoubleToInt64Bits), pf.VaDeg.Select(BitConverter.DoubleToInt64Bits));
        Close(one.FactsDevices![0].ShuntMvar, pf.FactsDevices[0].ShuntMvar + pf.FactsDevices[1].ShuntMvar, "shares add up to the single device");
    }

    [Fact]
    public void A_statcom_whose_share_passes_its_rating_is_fixed_at_the_rating_and_the_other_carries_the_rest()
    {
        // Equal RMPCT would give each 25.25 Mvar; S10 is rated 10 MVA at unity voltage, so 10.1 Mvar at 1.01 pu.
        GridModel m = Read(Raw(Statcom("S10", 1.01, 10, 50) + "\n" + Statcom("S500", 1.01, 500, 50)));
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.FactsNote);
        Assert.Equal(1.01, pf.Vm[Ix(pf, 2)]);
        FactsResult small = pf.FactsDevices![0], big = pf.FactsDevices[1];
        Assert.True(small.ShuntLimited);
        Assert.False(big.ShuntLimited);
        Close(10 * 1.01, small.ShuntMvar, "S10 at its rating", 1e-9);
        Close(10.0, small.ShuntCurrentMva, "S10's current", 1e-9);
        Close(HandQ(1.01) - 10 * 1.01, big.ShuntMvar, "S500 carries the rest", 1e-9);
    }

    [Fact]
    public void A_statcom_and_an_svc_with_one_set_point_share_by_rmpct()
    {
        GridModel m = Read(Raw(Statcom("S60", 1.01, 500, 60), Svc(1.01, 1.01, 40)));
        StaticVarCompensator c = Assert.Single(m.StaticVarCompensators);
        Assert.Equal(40.0, c.RmpctPercent);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.FactsNote);
        Assert.Equal(1.01, pf.Vm[Ix(pf, 2)]);
        double total = HandQ(1.01);
        Close(0.6 * total, pf.FactsDevices![0].ShuntMvar, "STATCOM share", 1e-9);
        SvcResult s = Assert.Single(pf.StaticVarCompensators!);
        Assert.Equal(SvcState.Regulating, s.State);
        Close(0.4 * total, s.Mvar, "SVC share", 1e-9);
        Close(0.4 * total / (1.01 * 1.01), s.SusceptanceMvar, "SVC susceptance = Q / V^2", 1e-9);
    }

    [Fact]
    public void An_svc_whose_share_passes_its_range_is_fixed_at_its_maximum_and_the_statcom_carries_the_rest()
    {
        // One 5 Mvar block: the SVC's 50 % share (25.25 Mvar) is out of reach, so it sits at 5 Mvar at 1 pu (5.1005 Mvar at
        // 1.01 pu) and the STATCOM supplies the rest.
        string svc = "2, 2, 0, 1, 1.01, 1.01, 0, 50, '', 0, 1, 5";
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Raw(Statcom("S", 1.01, 500, 50), svc)));
        Assert.True(pf.Converged, pf.FactsNote);
        SvcResult s = Assert.Single(pf.StaticVarCompensators!);
        Assert.Equal(SvcState.AtMaximum, s.State);
        Assert.Equal(5.0, s.SusceptanceMvar);
        Close(HandQ(1.01) - 5 * 1.01 * 1.01, pf.FactsDevices![0].ShuntMvar, "the STATCOM carries the rest", 1e-9);
    }

    [Fact]
    public void Two_vsets_on_one_bus_are_refused_naming_both_devices()
    {
        GridModel m = Read(Raw(Statcom("A", 1.01, 500, 50) + "\n" + Statcom("B", 1.02, 500, 50)));
        var ex = Assert.Throws<InvalidOperationException>(() => new NewtonRaphsonPowerFlow().Solve(m));
        Assert.Contains("FACTS device A's shunt element at 1.01 pu and by FACTS device B's shunt element at 1.02 pu", ex.Message, StringComparison.Ordinal);
        Assert.Contains("conflicting set-points", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_bus_needs_a_positive_rmpct_on_every_device()
    {
        GridModel m = Read(Raw(Statcom("A", 1.01, 500, 0) + "\n" + Statcom("B", 1.01, 500, 50)));
        var ex = Assert.Throws<InvalidOperationException>(() => new NewtonRaphsonPowerFlow().Solve(m));
        Assert.Contains("FACTS device A's shunt element shares the voltage control of bus 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RMPCT must be a positive finite percentage; it is 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_alone_on_its_bus_ignores_rmpct()
    {
        // RMPCT 0 alone: PSS/E needs RMPCT only when devices share; the single device supplies everything, to the bit.
        PowerFlowResult zero = new NewtonRaphsonPowerFlow().Solve(Read(Raw(Statcom("S", 1.01, 500, 0))));
        PowerFlowResult hundred = new NewtonRaphsonPowerFlow().Solve(Read(Raw(Statcom("S", 1.01, 500, 100))));
        Assert.True(zero.Converged, zero.FactsNote);
        Assert.Equal(BitConverter.DoubleToInt64Bits(hundred.FactsDevices![0].ShuntMvar), BitConverter.DoubleToInt64Bits(zero.FactsDevices![0].ShuntMvar));
        Assert.Equal(hundred.Vm.Select(BitConverter.DoubleToInt64Bits), zero.Vm.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void An_svc_that_starts_holding_a_band_edge_another_device_does_not_hold_fails_loud()
    {
        // The STATCOM holds 1.01 pu, below the SVC's band 1.02..1.04: the SVC would hold 1.02 pu. One bus, two voltages.
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Raw(Statcom("S", 1.01, 500, 50), Svc(1.04, 1.02, 50))));
        Assert.False(pf.Converged);
        Assert.Contains("static var compensator SVC 2 has started holding 1.02 pu", pf.FactsNote, StringComparison.Ordinal);
        Assert.Contains("conflicting set-points", pf.FactsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void An_svc_inside_its_band_stays_at_binit_while_a_statcom_holds_the_bus()
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Raw(Statcom("S", 1.01, 500, 50), Svc(1.03, 0.97, 50))));
        Assert.True(pf.Converged, pf.FactsNote);
        SvcResult s = Assert.Single(pf.StaticVarCompensators!);
        Assert.Equal(SvcState.Initial, s.State);
        Assert.Equal(0.0, s.SusceptanceMvar);
        Close(HandQ(1.01), pf.FactsDevices![0].ShuntMvar, "the STATCOM holds the bus alone", 1e-9);
    }

    [Fact]
    public void Rmpct_survives_the_raw_writer_and_reader()
    {
        GridModel m = Read(Raw(Statcom("S75", 1.01, 500, 75) + "\n" + Statcom("S25", 1.01, 500, 25), Svc(1.01, 1.01, 12.5)));
        GridModel back = PsseRawParser.ParseWithDiagnostics(PsseRawWriter.Write(m).Text, "back",
            new PsseReadOptions { FrequencyHz = 50, ApplyShuntControl = true }).Model;
        Assert.Equal(new[] { 75.0, 25.0 }, back.FactsDevices.Select(d => d.RmpctPercent));
        Assert.Equal(12.5, Assert.Single(back.StaticVarCompensators).RmpctPercent);
    }
}
