using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse;

/// <summary>How <see cref="PsseRawParser"/> reads a file (Tranche A, October 2026).</summary>
public sealed record PsseReadOptions
{
    /// <summary>The PSS/E revision to read the file as, when its first line does not say (no REV field and no
    /// "PSS/E-NN" in the comment). Null: such a file is refused. When the file does say, a different value here is
    /// refused rather than guessed between.</summary>
    public int? Revision { get; init; }

    /// <summary>The system's nominal frequency (Hz) when the file's BASFRQ field is 0 or missing. A file that states
    /// BASFRQ and a different value here is refused. Null with no BASFRQ: <see cref="PsseRawParser.AssumedFrequencyHz"/>,
    /// noted.</summary>
    public double? FrequencyHz { get; init; }

    /// <summary>
    /// False (the default): a file holding an IN-SERVICE device that GridSim's solvers do not model (two-terminal,
    /// capacitor-commutated two-terminal DC, VSC regulating a remote bus, multi-terminal converters on two DC buses, FACTS
    /// devices in modes 4 to 8 or regulating a remote bus, or induction machines) is refused, because its power flow would differ from the file's. True: such devices are
    /// read past, each kind counted in a note, so the rest of the network can be studied. GNE devices (user-written
    /// models) are refused either way.
    /// </summary>
    public bool AllowUnmodelled { get; init; }

    /// <summary>
    /// False (the default): automatic transformer control (COD) is not applied; every tap stays at the file's ratio and
    /// the count is noted, exactly as before this option existed. True: every in-service winding with voltage control
    /// (COD 1) becomes an on-load tap changer (<see cref="Model.Oltc"/>) that the steady-state solve's tap loop steps:
    /// controlled bus CONT, band VMI..VMA (target the middle, deadband half the width), ratio limits RMI..RMA and step
    /// (RMA - RMI) / (NTP - 1), all on the branch's tap ratio. The taps start from the file's ratios. Reactive power
    /// (COD 2), active power (COD 3), DC line (COD 4) and asymmetric (COD 5) control are still not applied, and noted.
    /// </summary>
    public bool ApplyTapControl { get; init; }

    /// <summary>
    /// False (the default): every switched shunt is fixed at BINIT, exactly as before this option existed. True: each
    /// in-service switched shunt with discrete local voltage control (MODSW 1, SWREM 0 or its own bus) whose blocks are
    /// all one size, and whose BINIT is a whole number of them, is also returned as a
    /// <see cref="PowerFlow.SwitchedShunt"/> bank (<see cref="PsseParseResult.SwitchedShunts"/>) that
    /// <see cref="PowerFlow.SwitchedShuntPowerFlow"/> switches in whole blocks to hold its bus in VSWLO..VSWHI. The model
    /// still carries BINIT in the bus shunt; the bank's steps count blocks switched from BINIT (its initial step is 0), so
    /// the model solved without the bank is the fixed-at-BINIT model. Every other switched shunt stays fixed, noted.
    /// </summary>
    public bool ApplyShuntControl { get; init; }
}

/// <summary>The bus-level metadata a RAW file carries that <see cref="GridModel"/> has no place for.</summary>
/// <param name="Area">The bus's area number.</param>
/// <param name="Zone">The bus's zone number.</param>
/// <param name="Owner">The bus's owner number.</param>
public sealed record PsseBusMeta(int Area, int Zone, int Owner);

/// <summary>Which RAW record a model generator came from: its bus and its machine ID (the key DYR records use).</summary>
public sealed record PsseMachineKey(int Bus, string Id);

/// <summary>Which RAW record a model branch came from.</summary>
/// <param name="Kind">"branch", "switching device", "transformer" or "transformer3" (one winding of a three-winding unit).</param>
/// <param name="FromBus">Bus I of the record.</param>
/// <param name="ToBus">Bus J of the record.</param>
/// <param name="ThirdBus">Bus K of a three-winding record, else 0.</param>
/// <param name="Circuit">The circuit identifier (CKT).</param>
/// <param name="Winding">For a three-winding record, which winding (1, 2 or 3) this branch is; else 0.</param>
public sealed record PsseBranchKey(string Kind, int FromBus, int ToBus, int ThirdBus, string Circuit, int Winding);

/// <summary>
/// The outcome of reading a PSS/E RAW file: the built <see cref="GridModel"/>, the notes gathered on the way, and what
/// the file carries that the model has no place for (Tranche A, October 2026). Notes are informational: a case that
/// produced notes is still a valid, solvable model; anything that would make it wrong is refused instead.
/// </summary>
public sealed record PsseParseResult(GridModel Model, IReadOnlyList<string> Notes)
{
    /// <summary>The PSS/E revision the file was read as (30, 33, 34 or 35).</summary>
    public int Revision { get; init; }

    /// <summary>The two title lines, trimmed.</summary>
    public IReadOnlyList<string> Titles { get; init; } = Array.Empty<string>();

    /// <summary>Area, zone and owner of every bus of the file, dropped isolated buses included, and of every star point
    /// (which takes winding 1's), keyed by bus id.</summary>
    public IReadOnlyDictionary<int, PsseBusMeta> BusMeta { get; init; } = new Dictionary<int, PsseBusMeta>();

    /// <summary>Area names from the area interchange data, keyed by area number.</summary>
    public IReadOnlyDictionary<int, string> AreaNames { get; init; } = new Dictionary<int, string>();

    /// <summary>Zone names from the zone data, keyed by zone number.</summary>
    public IReadOnlyDictionary<int, string> ZoneNames { get; init; } = new Dictionary<int, string>();

    /// <summary>Owner names from the owner data, keyed by owner number.</summary>
    public IReadOnlyDictionary<int, string> OwnerNames { get; init; } = new Dictionary<int, string>();

    /// <summary>The RAW record of each generator, position k for <see cref="GridModel.AllGenerators"/>[k].</summary>
    public IReadOnlyList<PsseMachineKey> MachineKeys { get; init; } = Array.Empty<PsseMachineKey>();

    /// <summary>The RAW record of each branch, position k for <see cref="GridModel.Branches"/>[k].</summary>
    public IReadOnlyList<PsseBranchKey> BranchKeys { get; init; } = Array.Empty<PsseBranchKey>();

    /// <summary>The branch and transformer records the file holds that are NOT in the model (out of service, or connected
    /// to a dropped isolated bus), in file order; a whole skipped three-winding unit has winding 0. A sequence (SEQ) file
    /// may name them, and is not refused for doing so.</summary>
    public IReadOnlyList<PsseBranchKey> SkippedBranchKeys { get; init; } = Array.Empty<PsseBranchKey>();

    /// <summary>The bus ids added as the star points of three-winding transformers, in file order.</summary>
    public IReadOnlyList<int> StarPointBuses { get; init; } = Array.Empty<int>();

    /// <summary>The switched shunts that regulate (<see cref="PsseReadOptions.ApplyShuntControl"/>), in file order; empty
    /// without the option. Steps count blocks switched from BINIT, which the model's bus shunt already holds.</summary>
    public IReadOnlyList<PowerFlow.SwitchedShunt> SwitchedShunts { get; init; } = Array.Empty<PowerFlow.SwitchedShunt>();
}

/// <summary>The text of a written RAW file, the notes on what the format could not carry, and the file's branch order.</summary>
/// <param name="Text">The RAW text.</param>
/// <param name="Notes">What was written differently from the model, or not written.</param>
/// <param name="BranchOrder">The model branch index of each branch as the file holds it (lines first, then
/// transformers), so a caller can compare the read-back model with the model in the same order.</param>
public sealed record PsseWriteResult(string Text, IReadOnlyList<string> Notes, IReadOnlyList<int> BranchOrder);
