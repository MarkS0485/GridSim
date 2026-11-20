using GridSim.Core.Model.Substations;

namespace GridSim.Tests.Substations;

/// <summary>Hand-built node-breaker fixtures for RC4 R4-11 part A. Every node name is chosen so that its ordinal
/// (string.CompareOrdinal) order is the order a reader expects, which keeps the hand-worked groupings short.</summary>
internal static class SubstationFixtures
{
    public static NbSwitch Sw(string id, string from, string to, bool open = false, SwitchKind kind = SwitchKind.Breaker) =>
        new(id, kind, from, to, open);

    public static Substation Sub(string id, double kv, string[] nodes, params NbSwitch[] switches) =>
        new(id, null, new[] { new VoltageLevel("V", kv) }, Array.Empty<SubstationBay>(),
            nodes.Select(n => new ConnectivityNode(n, "V")).ToArray(), switches);

    /// <summary>
    /// Double busbar with a coupler, three feeders, each fed through two busbar disconnectors and a breaker.
    /// Nodes A1 (between A's disconnectors and breaker), A2 (feeder side), B1, B2, D1, D2, X1 and X2 (the busbars).
    /// Normal state: A on X1, B and D on X2, coupler closed, all breakers closed.
    /// Equipment: generator GA (slack) and load LA (20 MW) at A2, generator GB at B2, load LD (80 MW, 10 MVAr) at D2.
    /// </summary>
    public static SubstationModel DoubleBusbar() => new()
    {
        Name = "double-busbar",
        Substations = new[]
        {
            Sub("S", 132, new[] { "A1", "A2", "B1", "B2", "D1", "D2", "X1", "X2" },
                Sw("A_d1", "X1", "A1"), Sw("A_d2", "X2", "A1", open: true), Sw("A_cb", "A1", "A2"),
                Sw("B_d1", "X1", "B1", open: true), Sw("B_d2", "X2", "B1"), Sw("B_cb", "B1", "B2"),
                Sw("D_d1", "X1", "D1", open: true), Sw("D_d2", "X2", "D1"), Sw("D_cb", "D1", "D2"),
                Sw("CPL", "X1", "X2")),
        },
        Generators = new[] { new NbGenerator("GA", "A2", 60, Slack: true), new NbGenerator("GB", "B2", 20) },
        Loads = new[] { new NbLoad("LA", "A2", 20, 0), new NbLoad("LD", "D2", 80, 10) },
    };

    /// <summary>
    /// Breaker-and-a-half: two diameters between busbars X1 and X2. Diameter 1 is X1 -CB1a- P1 -CB1b- Q1 -CB1c- X2 and
    /// diameter 2 is X1 -CB2a- P2 -CB2b- Q2 -CB2c- X2; a feeder sits on each of P1, Q1, P2 and Q2.
    /// </summary>
    public static SubstationModel BreakerAndAHalf() => new()
    {
        Name = "breaker-and-a-half",
        Substations = new[]
        {
            Sub("S", 400, new[] { "P1", "P2", "Q1", "Q2", "X1", "X2" },
                Sw("CB1a", "X1", "P1"), Sw("CB1b", "P1", "Q1"), Sw("CB1c", "Q1", "X2"),
                Sw("CB2a", "X1", "P2"), Sw("CB2b", "P2", "Q2"), Sw("CB2c", "Q2", "X2")),
        },
        Generators = new[] { new NbGenerator("GA", "P1", 100, Slack: true) },
        Loads = new[] { new NbLoad("LB", "Q1", 30), new NbLoad("LC", "P2", 40), new NbLoad("LD", "Q2", 30) },
    };

    /// <summary>A four-node ring busbar R1-R2-R3-R4 closed by breakers CBa (R1-R2), CBb (R2-R3), CBc (R3-R4), CBd (R4-R1).</summary>
    public static SubstationModel Ring() => new()
    {
        Name = "ring",
        Substations = new[]
        {
            Sub("S", 132, new[] { "R1", "R2", "R3", "R4" },
                Sw("CBa", "R1", "R2"), Sw("CBb", "R2", "R3"), Sw("CBc", "R3", "R4"), Sw("CBd", "R4", "R1")),
        },
        Generators = new[] { new NbGenerator("G1", "R1", 50, Slack: true) },
        Loads = new[] { new NbLoad("L2", "R2", 10), new NbLoad("L3", "R3", 20), new NbLoad("L4", "R4", 20) },
    };

    public const double LineX = 0.1;

    /// <summary>
    /// Two substations joined by two identical lossless lines. SA (132 kV): busbars X1, X2 with coupler CPL, generator GA
    /// on X1 (slack, 1.0 pu), generator GB on X2 (30 MW, 1.0 pu), line LN1 from X1 (node L1) and LN2 from X2 (node L2).
    /// SB: nodes R1, R2 (line ends) and Y with the load LOAD (80 MW, 0 MVAr), joined by CBR1 and CBR2. Lines are
    /// x = 0.1 pu, r = 0, b = 0 on 100 MVA.
    /// </summary>
    public static SubstationModel TwoFeed() => new()
    {
        Name = "two-feed",
        Substations = new[]
        {
            Sub("SA", 132, new[] { "GA", "GB", "L1", "L2", "X1", "X2" },
                Sw("CBGA", "X1", "GA"), Sw("CBGB", "X2", "GB"), Sw("CPL", "X1", "X2"), Sw("CBL1", "X1", "L1"), Sw("CBL2", "X2", "L2")),
            Sub("SB", 132, new[] { "R1", "R2", "Y" }, Sw("CBR1", "Y", "R1"), Sw("CBR2", "Y", "R2")),
        },
        Lines = new[] { new NbLine("LN1", "L1", "R1", 0, LineX), new NbLine("LN2", "L2", "R2", 0, LineX) },
        Generators = new[] { new NbGenerator("GA", "GA", 0, Slack: true), new NbGenerator("GB", "GB", 30) },
        Loads = new[] { new NbLoad("LOAD", "Y", 80, 0) },
    };
}
