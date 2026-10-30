using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// An <see cref="IPowerFlowSolver"/> that runs the on-load tap changer (OLTC) outer loop of
/// <see cref="SteadyStateSolver"/> around a Newton engine, so a caller that takes an <see cref="IPowerFlowSolver"/>
/// (the contingency screens, the N-1 sweep, stress and chain) can solve to the same regulated steady state as the
/// default case solve.
/// <para>
/// <b>Why it exists (October 2026, D04).</b> The contingency family used to run the raw Newton engine, with no OLTC
/// loop, while the default case solve (<c>gridsim &lt;case&gt;</c>) runs the loop. The post-fault steady state that
/// GB security assessment judges is the state AFTER the Grid Supply Point tap changers have acted (NETS SQSS v2.10,
/// 8 April 2025, section 6.2: "following a secured event, the target voltages at Grid Supply Points should be
/// achieved ... after the operation of Grid Supply Transformer tap-changers"), so the screens now include the loop.
/// </para>
/// <para>
/// With no OLTC records in the model the loop is skipped and the answer is exactly the wrapped engine's, bit for bit
/// (<see cref="SteadyStateSolver.Solve"/> returns <c>engine.Solve(model, options)</c> directly). The returned result
/// is the result of the last loop pass, solved on the tap-adjusted model; the model the caller passed in is never
/// changed.
/// </para>
/// <para>
/// <b>Not thread-safe</b>, exactly like <see cref="SteadyStateSolver"/>: one instance per thread. A concurrent call
/// on a shared instance throws <see cref="InvalidOperationException"/> rather than racing.
/// </para>
/// </summary>
public sealed class OltcLoopPowerFlow : IPowerFlowSolver
{
    /// <summary>The OLTC loop, driving the caller's engine.</summary>
    private readonly SteadyStateSolver _loop;

    /// <summary>The pass cap of the loop (default <see cref="SteadyStateSolver.DefaultMaxOltcPasses"/>).</summary>
    private readonly int _maxOltcPasses;

    /// <summary>Wrap <paramref name="engine"/> (a dense or sparse Newton engine, on any linear backend) in the OLTC
    /// loop. Throws on a null engine: a caller that wants the shared engine choice passes
    /// <see cref="SolverSelector.CreateNewton"/>'s answer explicitly, so the engine is always visible at the call site.</summary>
    public OltcLoopPowerFlow(IPowerFlowSolver engine, int maxOltcPasses = SteadyStateSolver.DefaultMaxOltcPasses)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (maxOltcPasses < 0)
            throw new ArgumentOutOfRangeException(nameof(maxOltcPasses), maxOltcPasses, "the OLTC pass cap must be zero or more");
        _loop = new SteadyStateSolver(engine);
        _maxOltcPasses = maxOltcPasses;
        Engine = engine;
    }

    /// <summary>N232 (October 2026): the engine this loop drives, so the contingency screens can tell that every solve
    /// of this wrapper is a built-in Newton solve (see <c>ContingencyAnalysis.IsBuiltInNewton</c>). Read only; the loop
    /// itself is unchanged.</summary>
    internal IPowerFlowSolver Engine { get; }

    /// <summary>Number of OLTC re-solve passes taken by the last <see cref="Solve"/> (0 for a model with no OLTCs).</summary>
    public int OltcPasses => _loop.OltcPasses;

    /// <summary>Whether the last <see cref="Solve"/> left every OLTC settled (see <see cref="SteadyStateSolver.TapsSettled"/>).</summary>
    public bool TapsSettled => _loop.TapsSettled;

    /// <summary>Solve <paramref name="model"/> to its tap-regulated steady state with <paramref name="options"/>.</summary>
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
        => _loop.Solve(model, options, _maxOltcPasses);
}
