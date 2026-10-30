using System.Diagnostics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Qsts;

/// <summary>
/// Quasi-static time series engine: runs a <see cref="QstsStudy"/> on a case as a sequence of power flows, one per step,
/// warm-started from the previous step, with the controllers acting and their state carried between steps.
/// <para>
/// <b>Reuse.</b> Every solve is a call to the <see cref="IPowerFlowSolver"/> given (the sweep, dense or sparse Newton);
/// QSTS only builds the model each solve sees. Step 0 is never warm-started, so a one-step run with nothing varying is
/// the single power flow of the case, bit for bit.
/// </para>
/// <para>
/// <b>Steady mode</b> solves each step with the existing steady-state loops (<see cref="SteadyStateSolver"/> around
/// <see cref="SwitchedShuntPowerFlow"/>), carrying taps and bank steps forward. <b>Timed mode</b> runs the controllers of
/// this file; the order within a step is (1) advance the timers and storage energy over the elapsed interval, (2) build
/// the step's model (profiles, devices, carried taps and banks, warm start), (3) solve, then repeat: continuous
/// controllers (inverters, storage) to a fixed point, else capacitor banks that are ready, else tap changers that are
/// ready, re-solving after each action, until nothing acts or a cap is reached, (4) record the step and judge it.
/// Re-solves within a step start from the step's starting voltages, as the existing tap loop's passes do.
/// </para>
/// <para>
/// <b>Determinism.</b> A run is strictly sequential, devices act in model order, ordering never depends on a hash, and
/// the only transcendental (the inverter response lag) goes through <see cref="DetMath"/>, as does the LDC phasor.
/// Not thread-safe: one engine per thread (the hosting-capacity study runs one per scenario).
/// </para>
/// </summary>
public sealed class QstsEngine
{
    private readonly IPowerFlowSolver _engine;
    private readonly PowerFlowOptions? _options;

    /// <summary>A QSTS engine solving with <paramref name="engine"/> and <paramref name="options"/> (null = defaults).</summary>
    public QstsEngine(IPowerFlowSolver engine, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options;
    }

    /// <summary>The power-flow engine every solve uses.</summary>
    public IPowerFlowSolver Engine => _engine;

    /// <summary>Run <paramref name="study"/> on <paramref name="model"/>. Throws <see cref="InvalidDataException"/> when the
    /// study is refused (<see cref="QstsStudy.Validate"/>). A step that does not converge stops the run: the result then
    /// has <see cref="QstsResult.Completed"/> false and <see cref="QstsResult.FailedStep"/> set; it is never success.</summary>
    public QstsResult Run(GridModel model, QstsStudy study, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(study);
        study.Validate(model);
        var run = new RunState(this, model, study, cancellation);
        return run.Execute();
    }

    // =================================================================================================================
    // One run: all mutable state lives here, so an engine can be reused for the next run.
    // =================================================================================================================
    private sealed class RunState
    {
        private readonly QstsEngine _owner;
        private readonly GridModel _model;
        private readonly QstsStudy _s;
        private readonly CancellationToken _ct;
        private readonly int _nBus;
        private readonly int[] _busIds;
        private readonly Dictionary<int, int> _busPos = new();
        private readonly double _dtHours;

        // Profiles bound per bus / generator (null = not bound).
        private readonly QstsProfile?[] _busProfile;
        private readonly bool[] _busScaleP, _busScaleQ;
        private readonly QstsProfile?[] _genProfile;
        private readonly bool[] _genAbsolute;
        private readonly bool[] _busTouched;

        // OLTCs.
        private readonly int[] _oltcBranch;
        private readonly OltcTiming[] _oltcTiming;
        private readonly bool[] _oltcHeld;
        private readonly double[] _oltcAcc;
        private readonly int[] _oltcDir;          // excursion direction: +1 voltage high, -1 voltage low, 0 in band
        private readonly int[] _oltcOpsInExc;
        private readonly double[] _oltcLastWeight;
        private readonly int[] _oltcOps;

        // Capacitors.
        private readonly int[] _capSteps;
        private readonly int[] _capBranch;
        private readonly double[] _capAcc;
        private readonly int[] _capDir;
        private readonly int[] _capLastDirThisStep;
        private readonly bool[] _capFrozen;
        private readonly double[] _capLastOut;
        private readonly int[] _capOps;

        // PV and storage.
        private readonly double[] _pvP, _pvQ, _pvPcap, _pvAlpha, _pvLastDelta;
        private readonly double[] _pvQPrevStep, _pvPcapPrevStep;
        private readonly double[] _stP, _stE, _stAlpha, _stLastDelta;
        private readonly int[] _stBranch;

        private Branch[] _branches;
        private bool _branchesChanged;
        private readonly List<QstsOperation> _ops = new();

        public RunState(QstsEngine owner, GridModel model, QstsStudy study, CancellationToken ct)
        {
            _owner = owner; _model = model; _s = study; _ct = ct;
            _nBus = model.Buses.Count;
            _busIds = new int[_nBus];
            for (int i = 0; i < _nBus; i++) { _busIds[i] = model.Buses[i].Id; _busPos.TryAdd(model.Buses[i].Id, i); }
            _dtHours = study.StepSeconds / 3600.0;

            _busProfile = new QstsProfile?[_nBus];
            _busScaleP = new bool[_nBus]; _busScaleQ = new bool[_nBus];
            _busTouched = new bool[_nBus];
            foreach (QstsLoadBinding lb in study.Loads)
            {
                QstsProfile pr = study.Profile(lb.Profile)!;
                if (lb.Buses is null)
                {
                    for (int i = 0; i < _nBus; i++)
                    {
                        Bus b = model.Buses[i];
                        if (b.Pd == 0 && b.Qd == 0) continue;   // "all" means every bus with demand
                        _busProfile[i] = pr; _busScaleP[i] = lb.ScaleP; _busScaleQ[i] = lb.ScaleQ; _busTouched[i] = true;
                    }
                }
                else
                    foreach (int id in lb.Buses)
                    {
                        int i = _busPos[id];
                        _busProfile[i] = pr; _busScaleP[i] = lb.ScaleP; _busScaleQ[i] = lb.ScaleQ; _busTouched[i] = true;
                    }
            }
            _genProfile = new QstsProfile?[model.Generators.Count];
            _genAbsolute = new bool[model.Generators.Count];
            foreach (QstsGeneratorBinding gb in study.Generators) { _genProfile[gb.Generator] = study.Profile(gb.Profile); _genAbsolute[gb.Generator] = gb.AbsoluteMw; }
            foreach (QstsLoadAddition a in study.LoadAdditions) _busTouched[_busPos[a.Bus]] = true;
            foreach (QstsPvUnit u in study.Pv) _busTouched[_busPos[u.Bus]] = true;
            foreach (QstsStorageUnit u in study.Storage) _busTouched[_busPos[u.Bus]] = true;
            foreach (QstsCapacitor c in study.Capacitors) _busTouched[_busPos[c.Bus]] = true;

            int no = model.Oltcs.Count;
            _oltcBranch = study.OltcEnabled ? SteadyStateSolver.MapOltcRecordsToBranches(model) : Enumerable.Repeat(-1, no).ToArray();
            _oltcTiming = new OltcTiming[no];
            for (int k = 0; k < no; k++)
            {
                _oltcTiming[k] = study.TimingFor(model.Oltcs[k].Transformer);
                if (_oltcTiming[k].Characteristic == OltcCharacteristic.Inverse && !(model.Oltcs[k].DeadbandPu > 0))
                    throw new InvalidDataException($"QSTS study refused: OLTC '{model.Oltcs[k].Transformer}' has inverse time but a zero deadband (the inverse characteristic divides by the half-band).");
                bool ldc = _oltcTiming[k].LdcRPu != 0 || _oltcTiming[k].LdcXPu != 0;
                if (ldc && _oltcBranch[k] >= 0)
                {
                    Branch tx = model.Branches[_oltcBranch[k]];
                    if (model.Oltcs[k].ControlsBus != tx.From && model.Oltcs[k].ControlsBus != tx.To)
                        throw new InvalidDataException($"QSTS study refused: OLTC '{model.Oltcs[k].Transformer}' has line drop compensation but regulates bus {model.Oltcs[k].ControlsBus}, which is not an end of its transformer (the compensation needs the transformer current at the regulated end).");
                }
            }
            _oltcHeld = new bool[no]; _oltcAcc = new double[no]; _oltcDir = new int[no]; _oltcOpsInExc = new int[no];
            _oltcLastWeight = new double[no]; _oltcOps = new int[no];

            int nc = study.Capacitors.Count;
            _capSteps = new int[nc]; _capBranch = new int[nc]; _capAcc = new double[nc]; _capDir = new int[nc];
            _capLastDirThisStep = new int[nc]; _capFrozen = new bool[nc]; _capLastOut = new double[nc]; _capOps = new int[nc];
            for (int c = 0; c < nc; c++)
            {
                _capSteps[c] = study.Capacitors[c].InitialSteps;
                _capBranch[c] = BranchIndex(study.Capacitors[c].MonitoredBranch);
                _capLastOut[c] = double.NegativeInfinity;
            }

            int np = study.Pv.Count;
            _pvP = new double[np]; _pvQ = new double[np]; _pvPcap = new double[np]; _pvAlpha = new double[np]; _pvLastDelta = new double[np];
            _pvQPrevStep = new double[np]; _pvPcapPrevStep = new double[np];
            int ns = study.Storage.Count;
            _stP = new double[ns]; _stE = new double[ns]; _stAlpha = new double[ns]; _stLastDelta = new double[ns]; _stBranch = new int[ns];
            for (int j = 0; j < ns; j++)
            {
                QstsStorageUnit u = study.Storage[j];
                _stE[j] = u.InitialSoc * u.CapacityMwh;
                _stBranch[j] = BranchIndex(u.MonitoredBranch);
            }
            _branches = model.Branches.ToArray();
            _monitored = study.MonitorBranches.Select(BranchIndex).ToArray();
        }

        private readonly int[] _monitored;

        private int BranchIndex(string? name)
        {
            if (name is null) return -1;
            for (int i = 0; i < _model.Branches.Count; i++) if (_model.Branches[i].Name == name) return i;
            return -1;
        }

        // -------------------------------------------------------------------------------------------------------------
        public QstsResult Execute()
        {
            var sw = Stopwatch.StartNew();
            QstsStudy s = _s;
            var records = new List<QstsStepRecord>(s.Steps);
            int nBr = _model.Branches.Count;
            var secAbove = new double[_nBus]; var secBelow = new double[_nBus];
            var puAbove = new double[_nBus]; var puBelow = new double[_nBus];
            var vmaxSeen = new double[_nBus]; var vminSeen = new double[_nBus];
            Array.Fill(vmaxSeen, double.NegativeInfinity); Array.Fill(vminSeen, double.PositiveInfinity);
            var brSec = new double[nBr]; var brPctSec = new double[nBr]; var brMax = new double[nBr];
            double loadMwh = 0, lossMwh = 0, sourceMwh = 0, pvAvMwh = 0, pvMwh = 0, chMwh = 0, disMwh = 0;
            double dt = s.StepSeconds;
            PowerFlowResult? prev = null;
            int unsettled = 0;
            int failedStep = -1;
            string? failure = null;

            for (int k = 0; k < s.Steps; k++)
            {
                _ct.ThrowIfCancellationRequested();
                long t = (long)k * s.StepSeconds;

                // (1) Advance over the interval [t(k-1), t(k)): timers and storage energy.
                if (k > 0)
                {
                    for (int o = 0; o < _oltcAcc.Length; o++) if (_oltcDir[o] != 0) _oltcAcc[o] += dt * _oltcLastWeight[o];
                    for (int c = 0; c < _capAcc.Length; c++) if (_capDir[c] != 0) _capAcc[c] += dt;
                    for (int j = 0; j < _stE.Length; j++) _stE[j] = AdvanceEnergy(s.Storage[j], _stE[j], _stP[j], _dtHours);
                    Array.Copy(_pvQ, _pvQPrevStep, _pvQ.Length);
                    Array.Copy(_pvPcap, _pvPcapPrevStep, _pvPcap.Length);
                }
                Array.Clear(_capLastDirThisStep);
                Array.Clear(_capFrozen);
                var socStart = new double[_stE.Length];
                for (int j = 0; j < _stE.Length; j++) socStart[j] = _stE[j] / s.Storage[j].CapacityMwh;

                // (2) The step's scaled buses and generators (before devices), and the warm start.
                StepBase sb = BuildStepBase(t, prev);

                StepOutcome outcome = s.Mode == QstsControlMode.Steady ? SolveSteady(k, t, sb) : SolveTimed(k, t, sb);
                if (!outcome.Result.Converged)
                {
                    failedStep = k;
                    failure = $"step {k} (t = {t} s): the power flow did not converge ({outcome.Result.Status}, mismatch {outcome.Result.MaxMismatch:E3} pu after {outcome.Result.Iterations} iterations)";
                    break;
                }
                PowerFlowResult r = outcome.Result;
                if (!outcome.Settled) unsettled++;

                // (4) Record and judge.
                GridModel solved = outcome.Model;
                double[] vm = VmByModelOrder(r);
                double vmin = double.PositiveInfinity, vmax = double.NegativeInfinity; int vminBus = -1, vmaxBus = -1;
                for (int i = 0; i < _nBus; i++)
                {
                    double v = vm[i];
                    if (v < vmin) { vmin = v; vminBus = _busIds[i]; }
                    if (v > vmax) { vmax = v; vmaxBus = _busIds[i]; }
                    (double lo, double hi) = Limits(_model.Buses[i]);
                    if (v > hi) { secAbove[i] += dt; puAbove[i] += (v - hi) * dt; }
                    if (v < lo) { secBelow[i] += dt; puBelow[i] += (lo - v) * dt; }
                    if (v > vmaxSeen[i]) vmaxSeen[i] = v;
                    if (v < vminSeen[i]) vminSeen[i] = v;
                }
                double[] loading = new double[nBr];
                double maxLoad = 0; int maxLoadBr = -1;
                for (int b = 0; b < nBr && b < r.LineFlows.Count; b++)
                {
                    LineFlow lf = r.LineFlows[b];
                    if (!(lf.RatingMva > 0)) continue;
                    double pct = lf.LoadingPercent;
                    loading[b] = pct;
                    if (pct > maxLoad || maxLoadBr < 0) { maxLoad = pct; maxLoadBr = b; }
                    if (pct > brMax[b]) brMax[b] = pct;
                    if (pct > s.Limits.LoadingPercent) { brSec[b] += dt; brPctSec[b] += (pct - s.Limits.LoadingPercent) * dt; }
                }
                double loadMw = 0;
                foreach (Bus b in solved.Buses) loadMw += b.Pd;
                double pvAv = 0, pvSum = 0, pvQSum = 0;
                for (int u = 0; u < _pvP.Length; u++) { pvAv += PvAvailable(u, t); pvSum += _pvP[u]; pvQSum += _pvQ[u]; }
                // Total demand excluding the device injections folded into Pd: add back PV, subtract storage discharge.
                double stSum = 0;
                for (int j = 0; j < _stP.Length; j++) stSum += _stP[j];
                double grossLoad = loadMw + pvSum + stSum;
                double source = 0;
                for (int i = 0; i < r.BusIds.Count; i++)
                    if (_busPos.TryGetValue(r.BusIds[i], out int pos) && _model.Buses[pos].Type == BusType.Slack)
                        source += r.PInjMw[i] + solved.Buses[pos].Pd;
                loadMwh += grossLoad * _dtHours; lossMwh += r.TotalLossMw * _dtHours; sourceMwh += source * _dtHours;
                pvAvMwh += pvAv * _dtHours; pvMwh += pvSum * _dtHours;
                for (int j = 0; j < _stP.Length; j++)
                {
                    if (_stP[j] > 0) disMwh += _stP[j] * _dtHours;
                    else chMwh += -_stP[j] * _dtHours;
                }
                var taps = new double[_model.Oltcs.Count];
                for (int o = 0; o < taps.Length; o++) taps[o] = _oltcBranch[o] >= 0 ? _branches[_oltcBranch[o]].TapRatio : double.NaN;
                records.Add(new QstsStepRecord
                {
                    Step = k, TimeSeconds = t, Settled = outcome.Settled, Solves = outcome.Solves, Iterations = outcome.Iterations,
                    ContinuousIterations = outcome.ContinuousIterations, DiscreteIterations = outcome.DiscreteIterations,
                    LossMw = r.TotalLossMw, LossMvar = r.TotalLossMvar, LoadMw = grossLoad, SourceMw = source,
                    PvAvailableMw = pvAv, PvMw = pvSum, PvMvar = pvQSum, StorageMw = stSum,
                    VminPu = vmin, VminBus = vminBus, VmaxPu = vmax, VmaxBus = vmaxBus,
                    MaxLoadingPercent = maxLoad, MaxLoadingBranch = maxLoadBr,
                    Taps = taps, CapacitorSteps = (int[])_capSteps.Clone(), PvP = (double[])_pvP.Clone(), PvQ = (double[])_pvQ.Clone(),
                    StorageP = (double[])_stP.Clone(), Soc = socStart,
                    Vm = s.RecordStates ? vm : null, LoadingPercent = s.RecordStates ? loading : null,
                    TapsAtLimit = outcome.TapsAtLimit,
                    MonitoredPMw = _monitored.Select(b => r.LineFlows[b].PFromMw).ToArray(),
                });
                prev = r;
            }

            // Storage energy after the last interval (the last step's power held for one step).
            var finalSoc = new double[_stE.Length];
            if (failedStep < 0)
                for (int j = 0; j < _stE.Length; j++)
                    finalSoc[j] = AdvanceEnergy(s.Storage[j], _stE[j], _stP[j], _dtHours) / s.Storage[j].CapacityMwh;
            else
                for (int j = 0; j < _stE.Length; j++) finalSoc[j] = _stE[j] / s.Storage[j].CapacityMwh;

            var busViol = new List<QstsBusViolation>();
            for (int i = 0; i < _nBus; i++)
                if (secAbove[i] > 0 || secBelow[i] > 0)
                    busViol.Add(new QstsBusViolation(_busIds[i], secAbove[i], secBelow[i], puAbove[i], puBelow[i], vmaxSeen[i], vminSeen[i]));
            var brViol = new List<QstsBranchViolation>();
            for (int b = 0; b < nBr; b++)
                if (brSec[b] > 0) brViol.Add(new QstsBranchViolation(b, _model.Branches[b].Name ?? $"{_model.Branches[b].From}-{_model.Branches[b].To}", brSec[b], brPctSec[b], brMax[b]));

            return new QstsResult
            {
                Completed = failedStep < 0, FailedStep = failedStep, FailureReason = failure, Mode = s.Mode, StepSeconds = s.StepSeconds,
                BusIds = _busIds, Steps = records, Operations = _ops,
                OltcNames = _model.Oltcs.Select(o => o.Transformer).ToArray(), TapOperations = _oltcOps,
                CapacitorNames = s.Capacitors.Select(c => c.Name).ToArray(), CapacitorOperations = _capOps,
                PvNames = s.Pv.Select(p => p.Name).ToArray(), StorageNames = s.Storage.Select(p => p.Name).ToArray(),
                FinalSoc = finalSoc, UnsettledSteps = unsettled,
                Energy = new QstsEnergy(loadMwh, lossMwh, sourceMwh, pvAvMwh, pvMwh, pvAvMwh - pvMwh, chMwh, disMwh),
                BusViolations = busViol, BranchViolations = brViol, LastResult = records.Count > 0 ? prev : null,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }

        /// <summary>E after holding power <paramref name="p"/> (MW, discharge positive) for <paramref name="dtHours"/>:
        /// E - p / etaD * dt when discharging, E + etaC * (-p) * dt when charging.</summary>
        internal static double AdvanceEnergy(QstsStorageUnit u, double e, double p, double dtHours)
            => p > 0 ? e - p / u.DischargeEfficiency * dtHours
             : p < 0 ? e + u.ChargeEfficiency * -p * dtHours
             : e;

        private (double lo, double hi) Limits(Bus b)
        {
            QstsLimits l = _s.Limits;
            if (!l.PerBusVoltageLimits) return (l.VminPu, l.VmaxPu);
            return (b.VminPu ?? l.VminPu, b.VmaxPu ?? l.VmaxPu);
        }

        private double[] VmByModelOrder(PowerFlowResult r)
        {
            var vm = new double[_nBus];
            if (r.BusIds.Count == _nBus)
            {
                bool same = true;
                for (int i = 0; i < _nBus; i++) if (r.BusIds[i] != _busIds[i]) { same = false; break; }
                if (same) { Array.Copy(r.Vm, vm, _nBus); return vm; }
            }
            var idx = new Dictionary<int, int>(r.BusIds.Count);
            for (int i = 0; i < r.BusIds.Count; i++) idx.TryAdd(r.BusIds[i], i);
            for (int i = 0; i < _nBus; i++) vm[i] = idx.TryGetValue(_busIds[i], out int j) ? r.Vm[j] : double.NaN;
            return vm;
        }

        // -------------------------------------------------------------------------------------------------------------
        // The step's base: scaled demand per bus (before devices) and generators, plus the warm start.
        // -------------------------------------------------------------------------------------------------------------
        private sealed class StepBase
        {
            public required Bus[] Buses;            // scaled demand and warm start, no devices
            public required double[] AddP;          // load additions per bus position (MW)
            public required double[] AddQ;
            public required IReadOnlyList<Generator>? Generators;   // null = the case's own list
            public required bool AnyBusChange;
        }

        private StepBase BuildStepBase(long t, PowerFlowResult? warm)
        {
            var buses = new Bus[_nBus];
            bool any = false;
            Dictionary<int, int>? warmIdx = null;
            if (warm is not null)
            {
                warmIdx = new Dictionary<int, int>(warm.BusIds.Count);
                for (int i = 0; i < warm.BusIds.Count; i++) warmIdx.TryAdd(warm.BusIds[i], i);
            }
            for (int i = 0; i < _nBus; i++)
            {
                Bus b = _model.Buses[i];
                QstsProfile? pr = _busProfile[i];
                if (pr is not null)
                {
                    double m = pr.ValueAt(t);
                    b = b with { Pd = _busScaleP[i] ? b.Pd * m : b.Pd, Qd = _busScaleQ[i] ? b.Qd * m : b.Qd };
                    any = true;
                }
                // Warm start exactly as GridScenario.WithSolvedVoltages: every bus takes the previous solved Vm and angle.
                if (warmIdx is not null && warmIdx.TryGetValue(b.Id, out int j))
                {
                    b = b with { Vm = warm!.Vm[j], VaDeg = warm.VaDeg[j] };
                    any = true;
                }
                buses[i] = b;
            }
            var addP = new double[_nBus]; var addQ = new double[_nBus];
            foreach (QstsLoadAddition a in _s.LoadAdditions)
            {
                int i = _busPos[a.Bus];
                double m = _s.Profile(a.Profile)?.ValueAt(t) ?? 1.0;
                double p = a.Mw * m;
                addP[i] += p;
                if (a.PowerFactor < 1) addQ[i] += p * Math.Sqrt(1 - a.PowerFactor * a.PowerFactor) / a.PowerFactor;
            }
            IReadOnlyList<Generator>? gens = null;
            if (_s.Generators.Count > 0 || _s.GenerationFollowsLoad)
            {
                var g = _model.Generators.ToArray();
                // Generation follows load (GridScenario.AtLoad's rule, as the chain verb uses): every unit off the slack
                // bus and without a profile of its own is scaled by the demand ratio, then Dispatcher.Balance re-balances
                // with the slack. The ratio is the profile value itself when one profile scales every bus ("all"), so
                // such a step is exactly AtLoad's model; otherwise it is the scaled total demand over the case's.
                double ratio = 1.0;
                int slackBus = -1;
                if (_s.GenerationFollowsLoad)
                {
                    slackBus = _model.Buses.First(b => b.Type == BusType.Slack).Id;
                    if (_s.Loads.Count == 1 && _s.Loads[0].Buses is null && _s.Loads[0].ScaleP)
                        ratio = _s.Profile(_s.Loads[0].Profile)!.ValueAt(t);
                    else
                    {
                        double baseSum = 0, nowSum = 0;
                        for (int i = 0; i < _nBus; i++) { baseSum += _model.Buses[i].Pd; nowSum += buses[i].Pd; }
                        ratio = baseSum != 0 ? nowSum / baseSum : 1.0;
                    }
                }
                for (int k = 0; k < g.Length; k++)
                {
                    QstsProfile? pr = _genProfile[k];
                    if (pr is not null)
                    {
                        double m = pr.ValueAt(t);
                        g[k] = g[k] with { Pg = _genAbsolute[k] ? m : g[k].Pg * m };
                    }
                    else if (_s.GenerationFollowsLoad && g[k].Bus != slackBus) g[k] = g[k] with { Pg = g[k].Pg * ratio };
                }
                gens = g;
                if (_s.GenerationFollowsLoad)
                {
                    // Balance needs the step's demand: the scaled buses (devices and added loads are not in it).
                    GridModel scaled = _model.CopyWith(buses: buses, generators: g);
                    gens = Dispatch.Dispatcher.Balance(scaled).Generators;
                }
            }
            return new StepBase { Buses = buses, AddP = addP, AddQ = addQ, Generators = gens, AnyBusChange = any };
        }

        private double PvAvailable(int u, long t)
        {
            QstsPvUnit pv = _s.Pv[u];
            double m = _s.Profile(pv.Profile)?.ValueAt(t) ?? 1.0;
            return Math.Max(0.0, pv.RatedMw * m);
        }

        /// <summary>The model one solve sees: the step base plus device injections and (with <paramref name="withCaps"/>)
        /// the capacitor steps on Bs, with the carried branches. Buses no device or profile touches keep their record.</summary>
        private GridModel Compose(StepBase sb, bool withCaps)
        {
            Bus[] buses = sb.Buses;
            bool copied = false;
            bool anyDevice = _s.LoadAdditions.Count > 0 || _pvP.Length > 0 || _stP.Length > 0 || (withCaps && _capSteps.Length > 0);
            if (anyDevice)
            {
                buses = (Bus[])sb.Buses.Clone();
                copied = true;
                // Demand: additions, then PV (negative load), then storage (negative load, discharge positive).
                var has = new bool[_nBus];
                for (int i = 0; i < _nBus; i++) if (sb.AddP[i] != 0 || sb.AddQ[i] != 0) { has[i] = true; }
                var pvP = new double[_nBus]; var pvQ = new double[_nBus];
                for (int u = 0; u < _pvP.Length; u++) { int i = _busPos[_s.Pv[u].Bus]; pvP[i] += _pvP[u]; pvQ[i] += _pvQ[u]; has[i] = true; }
                var stP = new double[_nBus];
                for (int j = 0; j < _stP.Length; j++) { int i = _busPos[_s.Storage[j].Bus]; stP[i] += _stP[j]; has[i] = true; }
                // Capacitor banks: the same sum SwitchedShuntPowerFlow.WithShunts forms (banks at step 0 add nothing).
                var capQ = new double[_nBus]; var hasCap = new bool[_nBus];
                if (withCaps)
                    for (int c = 0; c < _capSteps.Length; c++)
                    {
                        if (_capSteps[c] == 0) continue;
                        int i = _busPos[_s.Capacitors[c].Bus];
                        capQ[i] = capQ[i] + _capSteps[c] * _s.Capacitors[c].StepMvar;
                        hasCap[i] = true;
                    }
                for (int i = 0; i < _nBus; i++)
                {
                    if (!has[i] && !hasCap[i]) continue;
                    Bus b = buses[i];
                    if (has[i]) b = b with { Pd = b.Pd + sb.AddP[i] - pvP[i] - stP[i], Qd = b.Qd + sb.AddQ[i] - pvQ[i] };
                    if (hasCap[i]) b = b with { Bs = b.Bs + capQ[i] };
                    buses[i] = b;
                }
            }
            if (!copied && !sb.AnyBusChange && sb.Generators is null && !_branchesChanged) return _model;
            return _model.CopyWith(
                buses: copied || sb.AnyBusChange ? buses : null,
                generators: sb.Generators,
                branches: _branchesChanged ? _branches : null);
        }

        private sealed record StepOutcome(PowerFlowResult Result, GridModel Model, bool Settled, int Solves, int Iterations,
            int ContinuousIterations, int DiscreteIterations, IReadOnlyList<string> TapsAtLimit);

        // -------------------------------------------------------------------------------------------------------------
        // Steady mode: the existing loops, taps and banks carried.
        // -------------------------------------------------------------------------------------------------------------
        private StepOutcome SolveSteady(int k, long t, StepBase sb)
        {
            // Devices are algebraic in steady mode (validated): PV at unity / fixed PF, storage by schedule or self-consumption.
            SetAlgebraicDevices(t, sb);
            GridModel m = Compose(sb, withCaps: false);
            SwitchedShuntPowerFlow? shuntLoop = null;
            IPowerFlowSolver inner = _owner._engine;
            if (_capSteps.Length > 0)
            {
                var banks = new SwitchedShunt[_capSteps.Length];
                for (int c = 0; c < banks.Length; c++)
                {
                    QstsCapacitor q = _s.Capacitors[c];
                    banks[c] = new SwitchedShunt(q.Bus, q.StepMvar, q.MinSteps, q.MaxSteps, _capSteps[c], q.OnValue, q.OffValue, q.Name)
                    { ControlsBus = q.ControlsBus };
                }
                shuntLoop = new SwitchedShuntPowerFlow(inner, banks);
                inner = shuntLoop;
            }
            PowerFlowResult r;
            bool settled = true;
            int solves = 1;
            IReadOnlyList<string> atLimit = Array.Empty<string>();
            GridModel solvedModel = m;
            if (_s.OltcEnabled)
            {
                var ss = new SteadyStateSolver(inner);
                r = ss.Solve(m, _owner._options);
                settled = ss.TapsSettled;
                atLimit = ss.TapsAtLimit;
                solves += ss.OltcPasses;
                solvedModel = ss.RegulatedModel;
                // Carry the taps the loop settled on; count the net tap movement of each record (steady mode sees no
                // intermediate moves).
                if (r.Converged && !ReferenceEquals(solvedModel.Branches, _model.Branches))
                {
                    for (int o = 0; o < _oltcBranch.Length; o++)
                    {
                        int idx = _oltcBranch[o];
                        if (idx < 0) continue;
                        double before = _branches[idx].TapRatio, after = solvedModel.Branches[idx].TapRatio;
                        if (Math.Abs(after - before) > SteadyStateSolver.TapChangeEpsilon)
                        {
                            int n = (int)Math.Round(Math.Abs(after - before) / _model.Oltcs[o].StepPu);
                            _oltcOps[o] += n;
                            _ops.Add(new QstsOperation(k, t, "oltc", _model.Oltcs[o].Transformer, before, after, double.NaN, 0));
                        }
                    }
                    _branches = solvedModel.Branches.ToArray();
                    _branchesChanged = true;
                }
            }
            else r = inner.Solve(m, _owner._options);
            if (shuntLoop is not null)
            {
                settled &= shuntLoop.ShuntsSettled;
                if (!_s.OltcEnabled) solves = shuntLoop.LastPasses;
                if (r.Converged)
                    for (int c = 0; c < _capSteps.Length; c++)
                    {
                        int after = shuntLoop.LastSteps[c];
                        if (after != _capSteps[c])
                        {
                            _capOps[c] += Math.Abs(after - _capSteps[c]);
                            _ops.Add(new QstsOperation(k, t, "capacitor", _s.Capacitors[c].Name, _capSteps[c], after, double.NaN, 0));
                            _capSteps[c] = after;
                        }
                    }
            }
            return new StepOutcome(r, solvedModel, settled, solves, r.Iterations, 0, 0, atLimit);
        }

        /// <summary>Set the PV and storage powers that do not depend on the network state (steady mode, and the start
        /// point of the timed mode's continuous iteration for those units).</summary>
        private void SetAlgebraicDevices(long t, StepBase sb)
        {
            for (int u = 0; u < _pvP.Length; u++)
            {
                InverterSettings inv = _s.Pv[u].Inverter;
                double p = PvAvailable(u, t);
                _pvP[u] = p;
                _pvQ[u] = inv.Function == InverterFunction.FixedPowerFactor ? PfQ(inv, p) : 0.0;
                (_pvP[u], _pvQ[u]) = LimitS(_s.Pv[u], _pvP[u], _pvQ[u], inv.Function == InverterFunction.FixedPowerFactor);
            }
            for (int j = 0; j < _stP.Length; j++)
            {
                QstsStorageUnit u = _s.Storage[j];
                double req = u.Mode switch
                {
                    StorageMode.Schedule => _s.Profile(u.ScheduleProfile)!.ValueAt(t),
                    StorageMode.SelfConsumption => SelfConsumptionRequest(j, sb),
                    _ => _stP[j],
                };
                _stP[j] = ClampStorage(j, req);
            }
        }

        private static double PfQ(InverterSettings inv, double p)
        {
            double pf = inv.PowerFactor;
            double q = pf >= 1 ? 0.0 : p * Math.Sqrt(1 - pf * pf) / pf;
            return inv.Absorbing ? -q : q;
        }

        /// <summary>Hold P and Q within the apparent power rating. Fixed power factor scales both (keeping the ratio);
        /// otherwise var priority caps Q at S then P at sqrt(S^2 - Q^2), watt priority caps Q at sqrt(S^2 - P^2).</summary>
        private static (double P, double Q) LimitS(QstsPvUnit u, double p, double q, bool keepRatio)
        {
            double sMax = u.RatedMva;
            if (p * p + q * q <= sMax * sMax) return (p, q);
            if (keepRatio)
            {
                double f = sMax / Math.Sqrt(p * p + q * q);
                return (p * f, q * f);
            }
            if (u.Inverter.Priority == InverterPriority.Var)
            {
                q = Math.Clamp(q, -sMax, sMax);
                return (Math.Min(p, Math.Sqrt(Math.Max(0.0, sMax * sMax - q * q))), q);
            }
            double qMax = Math.Sqrt(Math.Max(0.0, sMax * sMax - p * p));
            return (p, Math.Clamp(q, -qMax, qMax));
        }

        private double SelfConsumptionRequest(int j, StepBase sb)
        {
            int i = _busPos[_s.Storage[j].Bus];
            double net = sb.Buses[i].Pd + sb.AddP[i];
            for (int u = 0; u < _pvP.Length; u++) if (_s.Pv[u].Bus == _s.Storage[j].Bus) net -= _pvP[u];
            return net;
        }

        /// <summary>Clamp a storage request to the rating and to what the energy allows over the coming step:
        /// discharge at most (E - Emin) * etaD / dt, charge at most (Emax - E) / (etaC * dt).</summary>
        private double ClampStorage(int j, double req)
        {
            QstsStorageUnit u = _s.Storage[j];
            double e = _stE[j];
            double dis = Math.Min(u.RatedMw, Math.Max(0.0, (e - u.SocMin * u.CapacityMwh) * u.DischargeEfficiency / _dtHours));
            double ch = Math.Min(u.RatedMw, Math.Max(0.0, (u.SocMax * u.CapacityMwh - e) / (u.ChargeEfficiency * _dtHours)));
            return Math.Clamp(req, -ch, dis);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Timed mode.
        // -------------------------------------------------------------------------------------------------------------
        private StepOutcome SolveTimed(int k, long t, StepBase sb)
        {
            int solves = 0, iters = 0, cont = 0, disc = 0;
            bool settled = true;

            // Continuous devices: start from their functions evaluated at the warm-start voltages (the case's own at step 0).
            double[] startVm = new double[_nBus];
            for (int i = 0; i < _nBus; i++) startVm[i] = sb.Buses[i].Vm;
            InitialiseContinuous(k, t, sb, startVm);

            GridModel m = Compose(sb, withCaps: true);
            PowerFlowResult r = _owner._engine.Solve(m, _owner._options);
            solves++; iters += r.Iterations;
            var atLimit = new List<string>();
            if (!r.Converged) return new StepOutcome(r, m, false, solves, iters, cont, disc, atLimit);

            int contThisRound = 0;
            while (true)
            {
                // (a) Continuous level to a fixed point: per-unit damping (UpdateContinuous), or, once that has failed to
                // settle within its cap in this run, the accelerated iteration over all units together
                // (UpdateContinuousAccelerated). Every run the per-unit loop settles never reaches the switch, so its
                // numbers are exactly those of the per-unit loop alone.
                if (_accelerated ? UpdateContinuousAccelerated(k, t, sb, r) : UpdateContinuous(k, t, sb, r))
                {
                    cont++; contThisRound++;
                    if (contThisRound > _s.MaxContinuousIterations)
                    {
                        if (_accelerated) { settled = false; break; }
                        // The per-unit loop did not settle within its cap (strongly coupled units: each unit's damping
                        // halves on every round its change does not shrink and never recovers, so units creep). Switch
                        // to the accelerated iteration for the rest of the run, with a fresh round budget; it starts
                        // from the state just reached (the update above stands, the re-solve below evaluates it).
                        _accelerated = true;
                        ClearAcceleration();
                        contThisRound = 0;
                    }
                    m = Compose(sb, withCaps: true);
                    r = _owner._engine.Solve(m, _owner._options);
                    solves++; iters += r.Iterations;
                    if (!r.Converged) return new StepOutcome(r, m, false, solves, iters, cont, disc, atLimit);
                    continue;
                }
                contThisRound = 0;
                // A discrete action changes the map the continuous units iterate, so the accelerated history restarts.
                ClearAcceleration();
                // The discrete iteration cap: stop, and the step is unsettled if any device is still ready to act.
                if (disc >= _s.MaxControlIterations)
                {
                    if (StepCapacitors(k, t, r, 0, dryRun: true, atLimit) || StepOltcs(k, t, r, 0, atLimit, dryRun: true)) settled = false;
                    break;
                }
                // (b) / (c) Discrete levels: capacitor banks first; tap changers only when no bank switched.
                atLimit.Clear();
                bool acted = StepCapacitors(k, t, r, disc + 1, dryRun: false, atLimit);
                if (!acted) acted = StepOltcs(k, t, r, disc + 1, atLimit, dryRun: false);
                if (!acted)
                {
                    if (_capFrozenOutside) settled = false;
                    break;
                }
                disc++;
                m = Compose(sb, withCaps: true);
                r = _owner._engine.Solve(m, _owner._options);
                solves++; iters += r.Iterations;
                if (!r.Converged) return new StepOutcome(r, m, false, solves, iters, cont, disc, atLimit);
            }
            // Carry each discrete controller's excursion into the next interval from the final state.
            ArmTimers(r);
            return new StepOutcome(r, m, settled, solves, iters, cont, disc, atLimit.ToArray());
        }

        private bool _capFrozenOutside;

        private double[] _vmScratch = Array.Empty<double>();

        private double VmAt(PowerFlowResult r, int busId)
        {
            if (_vmScratch.Length != _nBus || !ReferenceEquals(_vmFor, r)) { _vmScratch = VmByModelOrder(r); _vmFor = r; }
            return _busPos.TryGetValue(busId, out int i) ? _vmScratch[i] : double.NaN;
        }

        private PowerFlowResult? _vmFor;

        // ---- continuous: PV inverters and storage -------------------------------------------------------------------
        private double Beta(double responseSeconds, int k)
        {
            if (k == 0 || !(responseSeconds > 0)) return 1.0;
            // Open-loop response time T: 90 % of a step change after T, i.e. a first-order lag with time constant
            // T / ln 10; over one step the response covers 1 - exp(-dt ln10 / T) of the remaining change.
            return 1.0 - DetMath.Exp(-_s.StepSeconds * DetMath.Log(10.0) / responseSeconds);
        }

        /// <summary>The PV unit's (P, Q) target at voltage <paramref name="v"/>, after the response lag.</summary>
        private (double P, double Q, double Pcap) PvTarget(int u, int k, long t, double v)
        {
            QstsPvUnit pv = _s.Pv[u];
            InverterSettings inv = pv.Inverter;
            double pav = PvAvailable(u, t);
            double beta = Beta(inv.ResponseSeconds, k);
            double pcap = double.PositiveInfinity;
            if (inv.Function is InverterFunction.VoltWatt or InverterFunction.VoltVarVoltWatt)
            {
                double target = inv.VoltWattCurveOrDefault.At(v) * pv.RatedMw;
                pcap = beta == 1.0 ? target : _pvPcapPrevStep[u] + beta * (target - _pvPcapPrevStep[u]);
            }
            double p = Math.Min(pav, pcap);
            double q;
            switch (inv.Function)
            {
                case InverterFunction.VoltVar:
                case InverterFunction.VoltVarVoltWatt:
                    {
                        double target = inv.VoltVarCurveOrDefault.At(v) * pv.RatedMva;
                        q = beta == 1.0 ? target : _pvQPrevStep[u] + beta * (target - _pvQPrevStep[u]);
                        break;
                    }
                case InverterFunction.FixedPowerFactor: q = PfQ(inv, p); break;
                default: q = 0.0; break;
            }
            (p, q) = LimitS(pv, p, q, inv.Function == InverterFunction.FixedPowerFactor);
            return (p, q, double.IsPositiveInfinity(pcap) ? pv.RatedMw : pcap);
        }

        private void InitialiseContinuous(int k, long t, StepBase sb, double[] startVm)
        {
            for (int u = 0; u < _pvP.Length; u++)
            {
                int vb = _s.Pv[u].Inverter.VoltageBus ?? _s.Pv[u].Bus;
                (double p, double q, double pcap) = PvTarget(u, k, t, startVm[_busPos[vb]]);
                _pvP[u] = p; _pvQ[u] = q; _pvPcap[u] = pcap;
                _pvAlpha[u] = 1.0; _pvLastDelta[u] = double.PositiveInfinity;
            }
            for (int j = 0; j < _stP.Length; j++)
            {
                QstsStorageUnit u = _s.Storage[j];
                double req = u.Mode switch
                {
                    StorageMode.Schedule => _s.Profile(u.ScheduleProfile)!.ValueAt(t),
                    StorageMode.SelfConsumption => SelfConsumptionRequest(j, sb),
                    _ => _stP[j],   // peak shave: last step's power as the start point
                };
                _stP[j] = ClampStorage(j, req);
                _stAlpha[j] = 1.0; _stLastDelta[j] = double.PositiveInfinity;
            }
            ClearAcceleration();
        }

        /// <summary>One fixed-point update of every continuous device from the solved state <paramref name="r"/>. Each
        /// unit moves a fraction alpha (1 at first, halved whenever its update stops shrinking) of the way to its new
        /// target. Returns true when any unit moved by more than the tolerance (a re-solve is needed).</summary>
        private bool UpdateContinuous(int k, long t, StepBase sb, PowerFlowResult r)
        {
            bool moved = false;
            double tol = _s.ContinuousTolerance;
            for (int u = 0; u < _pvP.Length; u++)
            {
                QstsPvUnit pv = _s.Pv[u];
                if (pv.Inverter.Function is InverterFunction.Unity or InverterFunction.FixedPowerFactor) continue;   // algebraic: set once
                int vb = pv.Inverter.VoltageBus ?? pv.Bus;
                (double p, double q, double pcap) = PvTarget(u, k, t, VmAt(r, vb));
                double dP = p - _pvP[u], dQ = q - _pvQ[u];
                double delta = Math.Max(Math.Abs(dP), Math.Abs(dQ));
                if (delta <= tol * pv.RatedMva) continue;
                if (delta >= _pvLastDelta[u]) _pvAlpha[u] *= 0.5;
                _pvLastDelta[u] = delta;
                double a = _pvAlpha[u];
                _pvP[u] = a == 1.0 ? p : _pvP[u] + a * dP;
                _pvQ[u] = a == 1.0 ? q : _pvQ[u] + a * dQ;
                _pvPcap[u] = pcap;
                moved = true;
            }
            for (int j = 0; j < _stP.Length; j++)
            {
                QstsStorageUnit u = _s.Storage[j];
                double req;
                if (u.Mode == StorageMode.PeakShave)
                {
                    int b = _stBranch[j];
                    double raw = r.LineFlows[b].PFromMw + _stP[j];   // the flow the branch would carry without this unit
                    req = raw > u.HighMw ? raw - u.HighMw : raw < u.LowMw ? -(u.LowMw - raw) : 0.0;
                }
                else if (u.Mode == StorageMode.SelfConsumption) req = SelfConsumptionRequest(j, sb);
                else continue;
                double next = ClampStorage(j, req);
                double d = next - _stP[j];
                if (Math.Abs(d) <= tol * u.RatedMw) continue;
                if (Math.Abs(d) >= _stLastDelta[j]) _stAlpha[j] *= 0.5;
                _stLastDelta[j] = Math.Abs(d);
                _stP[j] = _stAlpha[j] == 1.0 ? next : _stP[j] + _stAlpha[j] * d;
                moved = true;
            }
            return moved;
        }

        // ---- accelerated continuous iteration -----------------------------------------------------------------------
        //
        // Why: the per-unit loop above damps each unit on its own. With many units coupled through the network (about
        // 1,900 volt-var inverters on a national case) the undamped map overshoots in its coupled modes, every unit's
        // factor is halved on the rounds its change grows, and as the factors never recover the last units creep by
        // under 1 kVAr per round: the step does not settle within any practical cap.
        //
        // What: the continuous set points x (P and Q of each reactive-function PV unit, P of each peak-shave or
        // self-consumption storage unit, each divided by its rating) solve x = F(x), where F is "solve the network,
        // then evaluate every unit's target". Anderson acceleration (depth AndersonDepth) takes the step
        //     x' = x + g - (dX + dG) gamma,   g = F(x) - x,   gamma = argmin || g - dG gamma ||_2,
        // with dX, dG the differences of the last iterates and residuals. On a linear map this is GMRES on (I - J) x = b,
        // so the coupled modes the per-unit factors could not reach are removed together; at a kink of a volt-var curve
        // it falls back towards the plain step because the least-squares fit sees the change in slope.
        //
        // Settling: the same test as the per-unit loop (every unit's target within ContinuousTolerance of its rating
        // of its present set point), so a settled step means the same thing in both modes.
        //
        // Determinism: a fixed unit order (model order), plain sequential loops for every dot product and update (no
        // SIMD reductions, no parallelism), modified Gram-Schmidt with a fixed column order and a fixed drop rule, and no
        // transcendental beyond Math.Sqrt (correctly rounded by IEEE 754). The same inputs give the same bits on every
        // run and SIMD tier.
        //
        // Engaged only after the per-unit loop has failed its cap in this run (see SolveTimed), so any run the per-unit
        // loop settles is untouched, bit for bit.

        /// <summary>History depth of the accelerated iteration (the number of past residual differences fitted).</summary>
        private const int AndersonDepth = 8;

        /// <summary>A history column whose part orthogonal to the newer columns is below this fraction of its own norm is
        /// left out of the fit (it adds no new direction and would make the fit ill-conditioned).</summary>
        private const double AndersonDropRatio = 1e-10;

        private bool _accelerated;
        private int[] _accPv = Array.Empty<int>(), _accSt = Array.Empty<int>();
        private double[] _accScale = Array.Empty<double>();
        private double[][] _accX = Array.Empty<double[]>(), _accG = Array.Empty<double[]>();
        private int _accCount;

        /// <summary>Start a fresh history (a new continuous phase, or a new step). Changes no set point.</summary>
        private void ClearAcceleration() => _accCount = 0;

        /// <summary>Build the unit lists, scales and history buffers on first use.</summary>
        private void EnsureAcceleration()
        {
            if (_accX.Length != 0) return;
            var pv = new List<int>();
            for (int u = 0; u < _pvP.Length; u++)
                if (_s.Pv[u].Inverter.Function is not (InverterFunction.Unity or InverterFunction.FixedPowerFactor)) pv.Add(u);
            var st = new List<int>();
            for (int j = 0; j < _stP.Length; j++)
                if (_s.Storage[j].Mode is StorageMode.PeakShave or StorageMode.SelfConsumption) st.Add(j);
            _accPv = pv.ToArray();
            _accSt = st.ToArray();
            int n = 2 * _accPv.Length + _accSt.Length;
            _accScale = new double[n];
            for (int i = 0; i < _accPv.Length; i++)
            {
                double s = _s.Pv[_accPv[i]].RatedMva;
                _accScale[2 * i] = s; _accScale[2 * i + 1] = s;
            }
            for (int i = 0; i < _accSt.Length; i++) _accScale[2 * _accPv.Length + i] = _s.Storage[_accSt[i]].RatedMw;
            _accX = new double[AndersonDepth + 1][];
            _accG = new double[AndersonDepth + 1][];
            for (int h = 0; h <= AndersonDepth; h++) { _accX[h] = new double[n]; _accG[h] = new double[n]; }
            _accCount = 0;
        }

        /// <summary>One accelerated update of every continuous device from the solved state <paramref name="r"/>.
        /// Returns true when any unit's target is more than the tolerance from its set point (the set points were moved
        /// and a re-solve is needed); false when every unit is settled (nothing changed).</summary>
        private bool UpdateContinuousAccelerated(int k, long t, StepBase sb, PowerFlowResult r)
        {
            EnsureAcceleration();
            double tol = _s.ContinuousTolerance;
            int np = _accPv.Length, n = _accScale.Length;
            var target = new double[n];
            var pcaps = new double[np];
            bool moved = false;

            // Targets at the solved state, and the settle test exactly as in the per-unit loop (unscaled).
            for (int i = 0; i < np; i++)
            {
                int u = _accPv[i];
                QstsPvUnit pv = _s.Pv[u];
                int vb = pv.Inverter.VoltageBus ?? pv.Bus;
                (double p, double q, double pcap) = PvTarget(u, k, t, VmAt(r, vb));
                target[2 * i] = p; target[2 * i + 1] = q; pcaps[i] = pcap;
                double delta = Math.Max(Math.Abs(p - _pvP[u]), Math.Abs(q - _pvQ[u]));
                if (delta > tol * pv.RatedMva) moved = true;
            }
            for (int i = 0; i < _accSt.Length; i++)
            {
                int j = _accSt[i];
                QstsStorageUnit u = _s.Storage[j];
                double req;
                if (u.Mode == StorageMode.PeakShave)
                {
                    double raw = r.LineFlows[_stBranch[j]].PFromMw + _stP[j];   // the flow without this unit
                    req = raw > u.HighMw ? raw - u.HighMw : raw < u.LowMw ? -(u.LowMw - raw) : 0.0;
                }
                else req = SelfConsumptionRequest(j, sb);
                double nextP = ClampStorage(j, req);
                target[2 * np + i] = nextP;
                if (Math.Abs(nextP - _stP[j]) > tol * u.RatedMw) moved = true;
            }
            if (!moved) return false;

            // The newest (x, g) pair goes to the end of the history; the oldest drops out when it is full.
            if (_accCount == AndersonDepth + 1)
            {
                double[] ox = _accX[0], og = _accG[0];
                for (int h = 0; h < AndersonDepth; h++) { _accX[h] = _accX[h + 1]; _accG[h] = _accG[h + 1]; }
                _accX[AndersonDepth] = ox; _accG[AndersonDepth] = og;
                _accCount--;
            }
            double[] x = _accX[_accCount], g = _accG[_accCount];
            for (int i = 0; i < np; i++)
            {
                int u = _accPv[i];
                x[2 * i] = _pvP[u] / _accScale[2 * i];
                x[2 * i + 1] = _pvQ[u] / _accScale[2 * i + 1];
            }
            for (int i = 0; i < _accSt.Length; i++) x[2 * np + i] = _stP[_accSt[i]] / _accScale[2 * np + i];
            for (int e = 0; e < n; e++) g[e] = target[e] / _accScale[e] - x[e];
            _accCount++;

            // The plain step, then the least-squares correction over the history differences (newest first, so a
            // dropped column is always the older, redundant one).
            var next = new double[n];
            for (int e = 0; e < n; e++) next[e] = x[e] + g[e];
            int cols = _accCount - 1;
            if (cols > 0)
            {
                var qv = new double[cols][];      // orthonormal columns kept
                var rr = new double[cols, cols];  // R of the kept columns (row = kept index, col = kept index)
                var keep = new int[cols];         // history column index of each kept column
                int kept = 0;
                for (int c = 0; c < cols; c++)
                {
                    int hi = _accCount - 1 - c;   // dG column c = G[hi] - G[hi - 1]
                    double[] gh = _accG[hi], gl = _accG[hi - 1];
                    var v = new double[n];
                    double norm0 = 0.0;
                    for (int e = 0; e < n; e++) { v[e] = gh[e] - gl[e]; norm0 += v[e] * v[e]; }
                    norm0 = Math.Sqrt(norm0);
                    if (!(norm0 > 0.0)) continue;
                    for (int a = 0; a < kept; a++)
                    {
                        double[] qa = qv[a];
                        double dot = 0.0;
                        for (int e = 0; e < n; e++) dot += qa[e] * v[e];
                        rr[a, kept] = dot;
                        for (int e = 0; e < n; e++) v[e] -= dot * qa[e];
                    }
                    double norm = 0.0;
                    for (int e = 0; e < n; e++) norm += v[e] * v[e];
                    norm = Math.Sqrt(norm);
                    if (!(norm > AndersonDropRatio * norm0)) continue;
                    for (int e = 0; e < n; e++) v[e] /= norm;
                    rr[kept, kept] = norm;
                    qv[kept] = v;
                    keep[kept] = c;
                    kept++;
                }
                if (kept > 0)
                {
                    // gamma = R^-1 Q^T g by back substitution.
                    var gamma = new double[kept];
                    for (int a = 0; a < kept; a++)
                    {
                        double dot = 0.0;
                        double[] qa = qv[a];
                        for (int e = 0; e < n; e++) dot += qa[e] * g[e];
                        gamma[a] = dot;
                    }
                    for (int a = kept - 1; a >= 0; a--)
                    {
                        double s = gamma[a];
                        for (int b = a + 1; b < kept; b++) s -= rr[a, b] * gamma[b];
                        gamma[a] = s / rr[a, a];
                    }
                    for (int a = 0; a < kept; a++)
                    {
                        int hi = _accCount - 1 - keep[a];
                        double[] xh = _accX[hi], xl = _accX[hi - 1], gh = _accG[hi], gl = _accG[hi - 1];
                        double ga = gamma[a];
                        for (int e = 0; e < n; e++) next[e] -= ga * ((xh[e] - xl[e]) + (gh[e] - gl[e]));
                    }
                }
            }

            // Write back, held within each unit's rating (P of PV in [0, rated MW], Q in [-rated MVA, rated MVA],
            // storage P in [-rated MW, rated MW]); a non-finite step is a defect and fails loud.
            for (int i = 0; i < np; i++)
            {
                int u = _accPv[i];
                QstsPvUnit pv = _s.Pv[u];
                double p = next[2 * i] * _accScale[2 * i], q = next[2 * i + 1] * _accScale[2 * i + 1];
                if (!double.IsFinite(p) || !double.IsFinite(q))
                    throw new InvalidOperationException($"QSTS accelerated inverter iteration produced a non-finite set point for {pv.Name}.");
                _pvP[u] = Math.Clamp(p, 0.0, pv.RatedMw);
                _pvQ[u] = Math.Clamp(q, -pv.RatedMva, pv.RatedMva);
                _pvPcap[u] = pcaps[i];
            }
            for (int i = 0; i < _accSt.Length; i++)
            {
                int j = _accSt[i];
                double p = next[2 * np + i] * _accScale[2 * np + i];
                if (!double.IsFinite(p))
                    throw new InvalidOperationException($"QSTS accelerated storage iteration produced a non-finite set point for {_s.Storage[j].Name}.");
                _stP[j] = Math.Clamp(p, -_s.Storage[j].RatedMw, _s.Storage[j].RatedMw);
            }
            return true;
        }

        // ---- capacitors ---------------------------------------------------------------------------------------------
        private double SecondOfDay(long t) => (_s.StartSecondOfDay + t) % 86400;

        /// <summary>
        /// One capacitor control iteration at instant <paramref name="t"/>. Each bank reads its quantity; outside its band
        /// it wants one step (in when the voltage is low / the var flow is high / inside the time window, out otherwise,
        /// in the direction its sign moves the voltage); a new direction starts a new excursion (timer from 0); it acts
        /// when its accumulated time covers its delay (on or off) and, switching in, the discharge dead time has passed
        /// since it last switched out. Within a step a bank that reverses direction is frozen (hunting across a band
        /// narrower than one step), as in <see cref="SwitchedShuntPowerFlow"/>. Returns whether any bank switched (with
        /// <paramref name="dryRun"/>: whether any would, changing nothing). A bank waiting for its timer does not stop the
        /// tap changers from acting. A bank at the end of its range with its quantity still outside goes on
        /// <paramref name="atLimit"/>.
        /// </summary>
        private bool StepCapacitors(int k, long t, PowerFlowResult r, int iteration, bool dryRun, List<string> atLimit)
        {
            bool acted = false;
            if (!dryRun) _capFrozenOutside = false;
            for (int c = 0; c < _capSteps.Length; c++)
            {
                QstsCapacitor q = _s.Capacitors[c];
                double sensed;
                int dir;
                switch (q.Control)
                {
                    case CapacitorControl.Voltage:
                        {
                            sensed = VmAt(r, q.ControlsBus ?? q.Bus);
                            int want = sensed < q.OnValue ? +1 : sensed > q.OffValue ? -1 : 0;   // +1 = raise the voltage
                            dir = q.StepMvar > 0 ? want : -want;
                            break;
                        }
                    case CapacitorControl.Var:
                        {
                            sensed = r.LineFlows[_capBranch[c]].QFromMvar;
                            int want = sensed > q.OnValue ? +1 : sensed < q.OffValue ? -1 : 0;   // +1 = inject more
                            dir = q.StepMvar > 0 ? want : -want;
                            break;
                        }
                    default:
                        {
                            sensed = SecondOfDay(t);
                            bool inWindow = q.OnValue < q.OffValue ? sensed >= q.OnValue && sensed < q.OffValue
                                                                   : sensed >= q.OnValue || sensed < q.OffValue;
                            dir = inWindow ? (_capSteps[c] < q.MaxSteps ? +1 : 0) : (_capSteps[c] > q.MinSteps ? -1 : 0);
                            break;
                        }
                }
                if (dir == 0)
                {
                    if (!dryRun) { _capDir[c] = 0; _capAcc[c] = 0; }
                    continue;
                }
                if (_capFrozen[c]) { if (!dryRun) _capFrozenOutside = true; continue; }
                int next = _capSteps[c] + dir;
                if (next < q.MinSteps || next > q.MaxSteps)
                {
                    // At the end of its range with its quantity still outside the band: nothing more it can do.
                    if (!dryRun) { _capDir[c] = 0; _capAcc[c] = 0; atLimit.Add(q.Name); }
                    continue;
                }
                if (_capLastDirThisStep[c] != 0 && dir != _capLastDirThisStep[c])
                {
                    if (!dryRun) { _capFrozen[c] = true; _capFrozenOutside = true; }
                    continue;
                }
                double acc = dir != _capDir[c] ? 0.0 : _capAcc[c];
                if (!dryRun && dir != _capDir[c]) { _capDir[c] = dir; _capAcc[c] = 0; }
                double need = dir > 0 ? q.DelaySeconds : q.OffDelaySeconds;
                bool deadOk = dir < 0 || t - _capLastOut[c] >= q.DeadTimeSeconds;
                if (!(acc >= need) || !deadOk) continue;   // armed, waiting (acts at a later step)
                if (dryRun) return true;
                int from = _capSteps[c];
                _capSteps[c] = next;
                _capLastDirThisStep[c] = dir;
                _capAcc[c] -= need;
                _capOps[c]++;
                if (dir < 0) _capLastOut[c] = t;
                _ops.Add(new QstsOperation(k, t, "capacitor", q.Name, from, next, sensed, iteration));
                acted = true;
            }
            return acted;
        }

        // ---- OLTCs --------------------------------------------------------------------------------------------------
        /// <summary>The voltage an OLTC regulates: the controlled bus voltage, or with line drop compensation
        /// |V - (R + jX) I| where I is the current leaving the transformer into the controlled bus (pu on the system base).</summary>
        private double OltcSensed(int o, PowerFlowResult r)
        {
            Oltc rec = _model.Oltcs[o];
            OltcTiming tm = _oltcTiming[o];
            double v = VmAt(r, rec.ControlsBus);
            if (tm.LdcRPu == 0 && tm.LdcXPu == 0) return v;
            int idx = _oltcBranch[o];
            Branch tx = _branches[idx];
            LineFlow lf = r.LineFlows[idx];
            int pos = -1;
            for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == rec.ControlsBus) { pos = i; break; }
            double va = r.VaDeg[pos] * (Math.PI / 180.0);
            (double sin, double cos) = DetMath.SinCos(va);
            double vr = v * cos, vi = v * sin;
            // Complex power leaving the transformer into the controlled bus (pu): minus the power flowing INTO the branch
            // at that end.
            bool atTo = rec.ControlsBus == tx.To;
            double sp = -(atTo ? lf.PToMw : lf.PFromMw) / _model.BaseMva;
            double sq = -(atTo ? lf.QToMvar : lf.QFromMvar) / _model.BaseMva;
            // I = conj(S / V) = (sp - j sq) / conj(V) ... computed as conj(S) / conj(V) = (sp - j sq) / (vr - j vi).
            double den = vr * vr + vi * vi;
            double ir = (sp * vr + sq * vi) / den;
            double ii = (sp * vi - sq * vr) / den;
            // V_comp = V - Z I, Z = R + jX.
            double cr = vr - (tm.LdcRPu * ir - tm.LdcXPu * ii);
            double ci = vi - (tm.LdcRPu * ii + tm.LdcXPu * ir);
            return Math.Sqrt(cr * cr + ci * ci);
        }

        private double InverseWeight(int o, double v)
        {
            OltcTiming tm = _oltcTiming[o];
            if (tm.Characteristic == OltcCharacteristic.Definite) return 1.0;
            Oltc rec = _model.Oltcs[o];
            double w = Math.Abs(v - rec.TargetVpu) / rec.DeadbandPu;
            if (tm.MinDelaySeconds > 0) w = Math.Min(w, tm.DelaySeconds / tm.MinDelaySeconds);
            return w;
        }

        /// <summary>The time the next operation of an excursion waits: the initial delay for the first (and for every one
        /// under sequential timing), the inter-tap delay for later ones under continuous timing.</summary>
        private double OltcNeed(int o, int opsInExcursion)
        {
            OltcTiming tm = _oltcTiming[o];
            return opsInExcursion == 0 || tm.Sequence == OltcSequence.Sequential ? tm.DelaySeconds : tm.InterTapSeconds;
        }

        /// <summary>The tap one step toward the band, by exactly the rule (and the expressions) of the steady-state tap
        /// loop: lowering the tap raises a controlled bus on the untapped (To) end, raising it raises one on the tapped
        /// (From) end; a tap outside its range holds rather than moving against the wanted direction.</summary>
        private double NewTap(int o, double v)
        {
            Oltc rec = _model.Oltcs[o];
            Branch tx = _branches[_oltcBranch[o]];
            double tap = tx.TapRatio;
            bool controlsTappedEnd = rec.ControlsBus == tx.From && rec.ControlsBus != tx.To;
            double TapDown() => tap <= rec.MinTap ? tap : Math.Max(rec.MinTap, tap - rec.StepPu);
            double TapUp() => tap >= rec.MaxTap ? tap : Math.Min(rec.MaxTap, tap + rec.StepPu);
            double newTap = tap;
            if (v < rec.TargetVpu - rec.DeadbandPu) newTap = controlsTappedEnd ? TapUp() : TapDown();
            else if (v > rec.TargetVpu + rec.DeadbandPu) newTap = controlsTappedEnd ? TapDown() : TapUp();
            return newTap;
        }

        /// <summary>
        /// One OLTC control iteration: which records hold (generator-regulated bus, the tap loop's own rule), the regulated
        /// voltage of each free record, its excursion timer, and one step for each record that is ready (all of them under
        /// independent coordination; only the first by priority under sequential). A transformer steps at most once per
        /// iteration (the first record in model order has it, as in the tap loop).
        /// </summary>
        private bool StepOltcs(int k, long t, PowerFlowResult r, int iteration, List<string> atLimit, bool dryRun)
        {
            if (_model.Oltcs.Count == 0) return false;
            SteadyStateSolver.HeldRecords(_model, _oltcBranch, r, _oltcHeld);
            var ready = new List<(int Rec, double V)>();
            for (int o = 0; o < _model.Oltcs.Count; o++)
            {
                int idx = _oltcBranch[o];
                if (idx < 0 || _oltcHeld[o]) { if (!dryRun) ResetOltc(o); continue; }
                Oltc rec = _model.Oltcs[o];
                double v = OltcSensed(o, r);
                if (double.IsNaN(v)) continue;
                int dir = v < rec.TargetVpu - rec.DeadbandPu ? -1 : v > rec.TargetVpu + rec.DeadbandPu ? +1 : 0;
                if (dir == 0) { if (!dryRun) ResetOltc(o); continue; }
                double acc = dir != _oltcDir[o] ? 0.0 : _oltcAcc[o];
                int opsInExc = dir != _oltcDir[o] ? 0 : _oltcOpsInExc[o];
                if (!dryRun && dir != _oltcDir[o]) { _oltcDir[o] = dir; _oltcAcc[o] = 0; _oltcOpsInExc[o] = 0; }
                if (!(acc >= OltcNeed(o, opsInExc))) continue;   // armed, waiting for its delay (acts at a later step)
                double nt = NewTap(o, v);
                if (Math.Abs(nt - _branches[idx].TapRatio) > SteadyStateSolver.TapChangeEpsilon)
                {
                    if (dryRun) return true;
                    ready.Add((o, v));
                }
                else if (!dryRun) atLimit.Add(rec.Transformer);   // pinned at a tap limit with the bus out of band
            }
            if (ready.Count == 0) return false;
            if (_s.OltcCoordination == OltcCoordination.Sequential)
            {
                (int Rec, double V) first = ready[0];
                foreach ((int Rec, double V) c in ready)
                    if (_oltcTiming[c.Rec].Priority < _oltcTiming[first.Rec].Priority) first = c;
                ready.Clear();
                ready.Add(first);
            }
            var stepped = new HashSet<int>();
            foreach ((int o, double v) in ready)
            {
                int idx = _oltcBranch[o];
                if (!stepped.Add(idx)) continue;   // one step per transformer per iteration (the first record has it)
                Oltc rec = _model.Oltcs[o];
                double from = _branches[idx].TapRatio;
                double nt = NewTap(o, v);
                if (rec.ImpedanceCorrection is { } ic)
                {
                    (double rc, double xc) = ic.At(nt);
                    _branches[idx] = _branches[idx] with { TapRatio = nt, R = rc, X = xc };
                }
                else _branches[idx] = _branches[idx] with { TapRatio = nt };
                _branchesChanged = true;
                _oltcAcc[o] -= OltcNeed(o, _oltcOpsInExc[o]);
                _oltcOpsInExc[o]++;
                _oltcOps[o]++;
                _ops.Add(new QstsOperation(k, t, "oltc", rec.Transformer, from, nt, v, iteration));
            }
            return true;
        }

        private void ResetOltc(int o) { _oltcDir[o] = 0; _oltcAcc[o] = 0; _oltcOpsInExc[o] = 0; }

        /// <summary>After a step settles: each discrete controller still outside its band carries its excursion into the
        /// next interval, with the inverse-time weight of its final deviation.</summary>
        private void ArmTimers(PowerFlowResult r)
        {
            for (int o = 0; o < _model.Oltcs.Count; o++)
            {
                if (_oltcDir[o] == 0 || _oltcBranch[o] < 0) { _oltcLastWeight[o] = 0; continue; }
                double v = OltcSensed(o, r);
                Oltc rec = _model.Oltcs[o];
                int dir = v < rec.TargetVpu - rec.DeadbandPu ? -1 : v > rec.TargetVpu + rec.DeadbandPu ? +1 : 0;
                if (dir != _oltcDir[o]) { ResetOltc(o); _oltcLastWeight[o] = 0; continue; }
                _oltcLastWeight[o] = InverseWeight(o, v);
            }
        }
    }
}
