namespace GridSim.Core.PowerFlow;

/// <summary>Terminal outcome of a power-flow solve. <see cref="PowerFlowResult.Converged"/> is true
/// <b>only</b> for <see cref="Converged"/>; every other value means the state is not trustworthy.</summary>
public enum PowerFlowStatus
{
    /// <summary>Met the mismatch tolerance and every solved value is finite.</summary>
    Converged,
    /// <summary>Ran to the iteration cap without meeting tolerance, or the Q-limit outer loop reached
    /// <see cref="PowerFlowOptions.MaxQLimitPasses"/> with buses still switching type (state is finite but
    /// unconverged).</summary>
    MaxIterations,
    /// <summary>A NaN/Inf appeared in the solved state - the linear solve diverged. The "solution" is invalid
    /// and must never be treated as converged, even if a stale mismatch happened to read below tolerance.</summary>
    NonFinite,
}

/// <summary>Real/reactive power flowing into each end of a branch, the loss across it, and how loaded it is vs its rating.</summary>
public sealed record LineFlow(
    int From, int To,
    double PFromMw, double QFromMvar,
    double PToMw, double QToMvar,
    double PLossMw, double QLossMvar,
    string Name = "", double RatingMva = 0, double LoadingPercent = 0);

/// <summary>The solved state of the grid after a power flow.</summary>
public sealed class PowerFlowResult
{
    public required bool Converged { get; init; }
    public required int Iterations { get; init; }
    public required double MaxMismatch { get; init; }

    /// <summary>Richer outcome than the <see cref="Converged"/> bool: distinguishes a clean convergence from
    /// hitting the iteration cap versus a non-finite (NaN/Inf) blow-up. When a producer does not set it (a presolved
    /// state loaded from disk or the bridge carries only the boolean) it follows <see cref="Converged"/>:
    /// <see cref="PowerFlowStatus.Converged"/> when converged, else <see cref="PowerFlowStatus.MaxIterations"/>. It used to
    /// default to Converged whatever the boolean said, so a state with <c>converged: false</c> read "Converged" to any
    /// consumer that branched on the status.</summary>
    public PowerFlowStatus Status
    {
        get => _status ?? (Converged ? PowerFlowStatus.Converged : PowerFlowStatus.MaxIterations);
        init => _status = value;
    }

    private readonly PowerFlowStatus? _status;

    public required IReadOnlyList<int> BusIds { get; init; }
    public required double[] Vm { get; init; }      // p.u.
    public required double[] VaDeg { get; init; }    // degrees
    public required double[] PInjMw { get; init; }   // net injection (generation - demand), MW
    public required double[] QInjMvar { get; init; } // net injection, MVAr

    public required double TotalLossMw { get; init; }
    public required double TotalLossMvar { get; init; }
    public required IReadOnlyList<LineFlow> LineFlows { get; init; }

    /// <summary>
    /// The bus types the solve ENDED with, aligned with <see cref="BusIds"/> (N071, October 2026): a bus typed PV in the
    /// model reads <see cref="BusType.Pq"/> here when the reactive-limit loop pinned its generators at a limit (their AVR
    /// no longer holds its voltage), or when it carries no generator (solved as PQ since R283). Set by the two Newton
    /// engines; <c>null</c> from any solver that does not report it (the backward/forward sweep, a presolved state read
    /// from disk), and a caller must then fall back to the model's own types. Informational: it changes no solved number.
    /// </summary>
    public IReadOnlyList<GridSim.Core.Model.BusType>? SolvedBusTypes { get; init; }

    /// <summary>
    /// D-DOC-150 (October 2026): the largest bus-voltage update of the backward/forward sweep's last iteration (pu), the
    /// quantity the sweep's convergence test compares with <see cref="PowerFlowOptions.Tolerance"/> (with its PV and loop
    /// checks). <c>null</c> from both Newton engines, whose test is the power mismatch itself (<see cref="MaxMismatch"/>).
    /// The sweep does NOT test the power mismatch, so a converged sweep can report a <see cref="MaxMismatch"/> above the
    /// tolerance (a two-bus v2 case printed "converged ... mismatch 1.30E-008 pu"); <see cref="ConvergedLabel"/> says which
    /// test decided. Informational: it changes no solved number and no verdict.
    /// </summary>
    public double? SweepVoltageStepPu { get; init; }

    /// <summary>The solved state of each two-terminal LCC link of the model (<see cref="LccDcLinks"/>), in model order;
    /// null when the model has none.</summary>
    public IReadOnlyList<LccLinkResult>? LccLinks { get; init; }

    /// <summary>Why the AC/DC alternation of a model with LCC links did not settle (null when it did, or with no links).</summary>
    public string? LccNote { get; init; }

    /// <summary>The solved state of each VSC link of the model (<see cref="VscDcLinks"/>), in model order; null when the
    /// model has none.</summary>
    public IReadOnlyList<VscLinkResult>? VscLinks { get; init; }

    /// <summary>Why the AC/DC solve of a model with VSC links failed (null when it settled, or with no links).</summary>
    public string? VscNote { get; init; }

    /// <summary>The solved state of each multi-terminal DC link of the model (<see cref="MtdcLinks"/>), in model order;
    /// null when the model has none.</summary>
    public IReadOnlyList<MtdcLinkResult>? MtdcLinks { get; init; }

    /// <summary>Why the AC/DC solve of a model with multi-terminal DC links failed (null when it settled, or with none).</summary>
    public string? MtdcNote { get; init; }

    /// <summary>The solved state of each FACTS device of the model (<see cref="FactsDevices"/>), in model order; null when
    /// the model has none.</summary>
    public IReadOnlyList<FactsResult>? FactsDevices { get; init; }

    /// <summary>The solved state of each static var compensator of the model, in model order; null when the model has
    /// none.</summary>
    public IReadOnlyList<SvcResult>? StaticVarCompensators { get; init; }

    /// <summary>Why the solve of a model with FACTS devices or static var compensators failed (null when it settled, or
    /// with none): a shunt element whose active power alone exceeds its rating, a UPFC outside its terminal voltage band
    /// or its series or bridge ratings, an AC solve that did not converge, or no settling.</summary>
    public string? FactsNote { get; init; }

    /// <summary>The solved state of each induction machine of the model (<see cref="InductionMachines"/>), in model order;
    /// null when the model has none.</summary>
    public IReadOnlyList<InductionMachineResult>? InductionMachines { get; init; }

    /// <summary>Why the solve of a model with induction machines failed (null when it settled, or with none): a schedule
    /// no slip on the stable branch meets, an AC solve that did not converge, or no settling.</summary>
    public string? InductionNote { get; init; }

    /// <summary>
    /// D-DOC-150: the word a report prints for a converged solve, naming the test that decided it: "converged" when the
    /// power mismatch was tested (the Newton engines, unchanged), "converged on the voltage step (|dV| x pu; ...)" when the
    /// sweep's voltage-update test was, so a mismatch printed next to it above the tolerance is not read as a
    /// contradiction. Only for a converged result; a caller prints its own non-convergence text.
    /// </summary>
    public string ConvergedLabel()
        => SweepVoltageStepPu is double dv
            ? "converged on the voltage step (|dV| " + dv.ToString("E2", System.Globalization.CultureInfo.InvariantCulture)
              + " pu; the sweep does not test the power mismatch)"
            : "converged";
}
