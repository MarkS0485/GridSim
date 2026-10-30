namespace GridSim.Core.IO.Matpower;

/// <summary>
/// The outcome of parsing a MATPOWER <c>.m</c> case: the built <see cref="Model.GridModel"/>
/// plus any diagnostics gathered on the way (isolated buses dropped, out-of-service elements
/// skipped, multiple slack/reference buses, etc.). The notes are informational  -  a case that
/// produced notes is still a valid, solvable model.
/// </summary>
public sealed record MatpowerParseResult(GridSim.Core.Model.GridModel Model, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// The case's DC lines (<c>mpc.dcline</c>), in file order (F24, October 2026). They are CARRIED here, so a
    /// converter or the MATPOWER writer can keep them, but NOT modelled: no GridSim solver applies them, whereas
    /// MATPOWER's power flow adds each line's flow as an injection at both ends. A case with DC lines therefore solves
    /// differently from MATPOWER, and the parse notes say so. Empty when the file has none.
    /// </summary>
    public IReadOnlyList<MatpowerDcLine> DcLines { get; init; } = Array.Empty<MatpowerDcLine>();

    /// <summary>
    /// N165 (October 2026): true when the file was recognised as a raw ohm / kW distribution feeder and
    /// <see cref="Model"/> is the per-unit / MW conversion (<see cref="MatpowerUnitNormalizer.Normalize"/>); false
    /// when the file was already per-unit / MW, or when the caller asked for <see cref="MatpowerUnitHandling.AsWritten"/>.
    /// </summary>
    public bool RawUnitsConverted { get; init; }
}

/// <summary>
/// N165 (October 2026): how <see cref="MatpowerParser"/> treats a file whose numbers look like raw engineering units
/// (branch impedance in ohms, demand in kW / kVAr; see <see cref="MatpowerUnitNormalizer.LooksRawUnits"/>).
/// </summary>
public enum MatpowerUnitHandling
{
    /// <summary>The default and the safe path: a raw feeder is converted to per-unit / MW, with a parse note. A file
    /// that does not look raw is unaffected.</summary>
    NormaliseRawFeeders = 0,

    /// <summary>Every number exactly as the file writes it (the parser's behaviour before N165). A raw-looking file
    /// gets a note that it was NOT converted. For inspecting or testing the raw data only: no solver should be given
    /// such a model without <see cref="MatpowerUnitNormalizer.Normalize"/>.</summary>
    AsWritten = 1,
}

/// <summary>
/// One MATPOWER DC line (a row of <c>mpc.dcline</c>), with the 17 standard columns under their MATPOWER meanings
/// (F24). Units as MATPOWER gives them: MW, MVAr and pu.
/// </summary>
/// <param name="FromBus">F_BUS: the "from" bus id.</param>
/// <param name="ToBus">T_BUS: the "to" bus id.</param>
/// <param name="InService">BR_STATUS above 0.</param>
/// <param name="PfMw">PF: flow at the from end (MW).</param>
/// <param name="PtMw">PT: flow at the to end (MW).</param>
/// <param name="QfMvar">QF: reactive injection at the from end (MVAr).</param>
/// <param name="QtMvar">QT: reactive injection at the to end (MVAr).</param>
/// <param name="VfPu">VF: voltage setpoint at the from end (pu).</param>
/// <param name="VtPu">VT: voltage setpoint at the to end (pu).</param>
/// <param name="PminMw">PMIN (MW).</param>
/// <param name="PmaxMw">PMAX (MW).</param>
/// <param name="QminFMvar">QMINF (MVAr).</param>
/// <param name="QmaxFMvar">QMAXF (MVAr).</param>
/// <param name="QminTMvar">QMINT (MVAr).</param>
/// <param name="QmaxTMvar">QMAXT (MVAr).</param>
/// <param name="Loss0Mw">LOSS0: constant loss term (MW).</param>
/// <param name="Loss1">LOSS1: linear loss coefficient (MW per MW of PF).</param>
public sealed record MatpowerDcLine(
    int FromBus, int ToBus, bool InService,
    double PfMw, double PtMw, double QfMvar, double QtMvar, double VfPu, double VtPu,
    double PminMw, double PmaxMw, double QminFMvar, double QmaxFMvar, double QminTMvar, double QmaxTMvar,
    double Loss0Mw, double Loss1)
{
    /// <summary>The number of standard columns of a MATPOWER dcline row (F_BUS ... LOSS1).</summary>
    public const int ColumnCount = 17;
}
