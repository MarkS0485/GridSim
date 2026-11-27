namespace GridSim.Core.IO.Ucte;

/// <summary>
/// The fixed columns (1-based, inclusive) of the UCTE-DEF records GridSim writes (RC5 WP34). Source: "UCTE data exchange
/// format for load flow and three phase short circuit studies", version 02 (2007-05-01), the public UCTE (now ENTSO-E)
/// format description; the same columns <see cref="UcteParser"/> reads (its remarks list them). The reader keeps its own
/// literals; this table is the writer's copy, and the round-trip tests hold the two together.
/// </summary>
internal static class UcteColumns
{
    /// <summary>A field's first and last column (1-based, inclusive).</summary>
    internal readonly record struct Field(int From, int To)
    {
        public int Width => To - From + 1;
    }

    // ---- ##N node ----------------------------------------------------------------------------------------------
    internal static readonly Field NodeCode = new(1, 8);
    internal static readonly Field NodeName = new(10, 21);
    internal static readonly Field NodeStatus = new(23, 23);
    internal static readonly Field NodeType = new(25, 25);
    internal static readonly Field NodeVoltage = new(27, 32);
    internal static readonly Field NodeLoadP = new(34, 40);
    internal static readonly Field NodeLoadQ = new(42, 48);
    internal static readonly Field NodeGenP = new(50, 56);
    internal static readonly Field NodeGenQ = new(58, 64);
    internal static readonly Field NodeGenPMin = new(66, 72);
    internal static readonly Field NodeGenPMax = new(74, 80);
    internal static readonly Field NodeGenQMin = new(82, 88);
    internal static readonly Field NodeGenQMax = new(90, 96);

    // ---- ##L line ----------------------------------------------------------------------------------------------
    internal static readonly Field Node1 = new(1, 8);
    internal static readonly Field Node2 = new(10, 17);
    internal static readonly Field Order = new(19, 19);
    internal static readonly Field Status = new(21, 21);
    internal static readonly Field LineR = new(23, 28);
    internal static readonly Field LineX = new(30, 35);
    internal static readonly Field LineB = new(37, 44);
    internal static readonly Field LineCurrent = new(46, 51);
    internal const int LineNameFrom = 53;

    // ---- ##T transformer ---------------------------------------------------------------------------------------
    internal static readonly Field TransformerU1 = new(23, 27);
    internal static readonly Field TransformerU2 = new(29, 33);
    internal static readonly Field TransformerS = new(35, 39);
    internal static readonly Field TransformerR = new(41, 46);
    internal static readonly Field TransformerX = new(48, 53);
    internal static readonly Field TransformerB = new(55, 62);
    internal static readonly Field TransformerG = new(64, 69);
    internal static readonly Field TransformerCurrent = new(71, 76);
    internal const int TransformerNameFrom = 78;

    // ---- ##R regulation ----------------------------------------------------------------------------------------
    internal static readonly Field PhaseDu = new(21, 25);
    internal static readonly Field PhaseN = new(27, 28);
    internal static readonly Field PhaseTap = new(30, 32);
    internal static readonly Field AngleDu = new(40, 44);
    internal static readonly Field AngleTheta = new(46, 50);
    internal static readonly Field AngleN = new(52, 53);
    internal static readonly Field AngleTap = new(55, 57);
    internal static readonly Field AngleType = new(65, 68);
}
