using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Scripting;

/// <summary>
/// The live sim engine (<see cref="LiveSim"/>) driven one fixed step at a time. The integration step is fixed when the
/// engine is selected (default 20 ms); a script step must be a whole multiple of it. Every call that moves the engine is
/// journalled, so a failed statement is rolled back by building a new engine and replaying the journal prefix: the live
/// sim is deterministic (seeded noise, fixed order), so the replay is bit-identical.
/// </summary>
public sealed class LiveEngine
{
    public const long DefaultStepUs = 20_000;

    public enum OpKind { Tick, Infeed, Network }

    /// <summary>One journalled call: a step, a change of net infeed, or a replacement of the network the loop re-solves.</summary>
    public readonly record struct Op(OpKind Kind, double Mw, GridModel? Model = null);

    /// <summary>The settings of SELECT ENGINE LIVE: the integration step, the power-flow solver and whether the authored asset
    /// fleet (inverter protection, LFDD, reclosers) runs when the case carries one.</summary>
    public sealed record Options(long StepUs, SolverKind Solver = SolverKind.Auto, bool Assets = true)
    {
        public string Canonical() => $"step {StepUs} us, solver {Solver}, assets {(Assets ? "on" : "off")}";
    }

    private readonly List<Op> _journal;
    private readonly LiveSim _sim;
    private int _tick;

    public LiveEngine(GridModel model, Options options, IReadOnlyList<Op>? replay = null)
    {
        BaseModel = model;
        Settings = options;
        StepUs = options.StepUs;
        _journal = [];
        try
        {
            _sim = new LiveSim(model, holdSteady: true, solver: options.Solver,
                dynamicsConfig: options.Assets ? LiveSim.AuthoredBundleConfig(model) : null);
        }
        catch (ArgumentException ex)
        {
            throw new ScriptException(ErrorPhase.Execution, default, "the live engine refused this model: " + ex.Message);
        }
        if (replay is not null)
            foreach (Op op in replay) Do(op);
    }

    public GridModel BaseModel { get; }
    public Options Settings { get; }
    public long StepUs { get; }

    /// <summary>True when an authored asset fleet runs: tripping or closing a generator then cannot change the network (see
    /// <see cref="LiveSim.ReplaceNetwork"/>).</summary>
    public bool HasAssetFleet => _sim.AssetFleet is not null;
    public int JournalCount => _journal.Count;
    public SimFrame? LastFrame { get; private set; }
    public double NominalHz => _sim.NominalHz;
    public string ActiveSolver => _sim.ActiveSolver;

    /// <summary>A new engine in the state this one had after its first <paramref name="count"/> operations.</summary>
    public LiveEngine RebuildTo(int count) => new(BaseModel, Settings, _journal.Take(count).ToList());

    /// <summary>Advance one integration step.</summary>
    public SimFrame Tick() => Do(new Op(OpKind.Tick, 0))!;

    /// <summary>Change the net infeed by <paramref name="netInfeedDeltaMw"/> (negative = a loss of infeed), felt from the next step.</summary>
    public void Infeed(double netInfeedDeltaMw)
    {
        if (netInfeedDeltaMw != 0) Do(new Op(OpKind.Infeed, netInfeedDeltaMw));
    }

    /// <summary>Re-solve from <paramref name="network"/> (the case with the script's trips and set-points applied) from the next step.</summary>
    public void SetNetwork(GridModel network)
    {
        ArgumentNullException.ThrowIfNull(network);
        Do(new Op(OpKind.Network, 0, network));
    }

    private SimFrame? Do(Op op)
    {
        _journal.Add(op);
        if (op.Kind == OpKind.Network)
        {
            try { _sim.ReplaceNetwork(op.Model!); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new ScriptException(ErrorPhase.Execution, default, "the live engine cannot take this change: " + ex.Message);
            }
            return null;
        }
        if (op.Kind == OpKind.Infeed)
        {
            _sim.TripInfeed(-op.Mw);
            return null;
        }
        double stepMinutes = StepUs / 60_000_000.0;
        SimFrame f = _sim.Step(_tick++, stepMinutes);
        LastFrame = f;
        if (!f.Result.Converged)
            throw new ScriptException(ErrorPhase.Execution, default,
                string.Create(CultureInfo.InvariantCulture, $"the live engine's power flow did not converge at tick {f.Tick}"));
        return f;
    }
}
