using System.Globalization;
using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// RC3 P6: the dynamics study runner. The order of operations is part of the contract:
/// <list type="number">
/// <item>Validate the request and resolve every equipment reference by native RAW identity (machine bus and ID, branch
/// from, to and circuit, bus number). Any failure is an input error and stops before any work.</item>
/// <item>Build the model coverage report (<see cref="DynamicsModelCoverage"/>). A report that is not simulable stops the
/// study: every row is NOT RUN.</item>
/// <item>Solve the power flow with the declared options. No solution: every row NOT RUN.</item>
/// <item>Prepare the machines from the solved case and run the no-event interval over the study duration; compare the
/// initial-state residuals and the drift with limits derived from the power-flow tolerance. Not held: every row NOT RUN.</item>
/// <item>Run each row from the same solved case with its own fresh controllers (serially or on bounded workers), results
/// in request order. A cancelled run leaves every row not started NOT ATTEMPTED.</item>
/// <item>Aggregate: invalid input stops first; a coverage gap blocks any STABLE / UNSTABLE status; incomplete or failed
/// rows give INCOMPLETE (known UNSTABLE rows are kept); then UNSTABLE; then STABLE (or EVALUATED for a CCT-only batch).</item>
/// </list>
/// Every simulation is the incumbent RMS engine (<see cref="RmsTransientStability.StudyEventsObserved"/>, which is
/// <see cref="RmsTransientStability.StudyEvents"/> with a read-only observer, and
/// <see cref="RmsTransientStability.CriticalClearingBracketEvents"/>), with the machine data the incumbent DYR reader and
/// loader produce.
/// </summary>
public static class DynamicsStudyRunner
{
    /// <summary>The factor by which every drift and residual limit exceeds its first-order estimate (see the
    /// equilibrium justification).</summary>
    public const double EquilibriumSafetyFactor = 10.0;

    private sealed record ResolvedEvent(DynamicsEventRequest Request, RmsEvent Engine, string Equipment, int? TrippedSlotGenerator);

    private sealed record Context(GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data,
        MachineDynamicsSet Set, int[] SlotOfGenerator, int RefSlot, double NominalHz, DynamicsStudyRequest Request,
        bool Indicative, IReadOnlyList<(DynamicsChannelRequest Channel, int? Slot, string Label)> Channels,
        IReadOnlyList<int> ChannelBuses);

    /// <summary>Run the study. Input errors throw <see cref="InvalidDataException"/> naming the item; everything after
    /// the inputs are accepted is reported in the result.</summary>
    public static DynamicsStudyResult Run(DynamicsStudyRequest request, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PsseParseResult raw = request.Raw ?? throw new InvalidDataException("The study needs the RAW parse result.");
        GridModel model = raw.Model;

        // 1. Validate and resolve.
        Validate(request);
        var resolvedCases = new List<IReadOnlyList<ResolvedEvent>?>();
        var resolvedTrips = new List<IReadOnlyList<(int Branch, string Label)>?>();
        foreach (DynamicsCaseRequest c in request.Cases)
        {
            if (c.Kind == DynamicsCaseKind.Events)
            {
                resolvedCases.Add(c.Events!.Select((e, i) => Resolve(raw, e, $"case '{c.Name}' event {i + 1}")).ToList());
                resolvedTrips.Add(null);
            }
            else
            {
                resolvedCases.Add(null);
                DynamicsCctRequest cct = c.Cct!;
                if (!model.Buses.Any(b => b.Id == cct.FaultBus))
                    throw new InvalidDataException($"case '{c.Name}': fault bus {cct.FaultBus} is not a bus of the RAW case.");
                var trips = cct.ClearingTrips.Select((b, i) => (Branch: ResolveBranch(raw, b, $"case '{c.Name}' clearing trip {i + 1}"), Label: b.ToString())).ToList();
                if (trips.Select(t => t.Branch).Distinct().Count() != trips.Count)
                    throw new InvalidDataException($"case '{c.Name}': a clearing trip names the same branch twice.");
                resolvedTrips.Add(trips);
            }
        }
        // R4-14: the device set of the adaptive companion run binds to the case before any work (absent for every incumbent request).
        Protection.Devices.BoundDeviceSet? boundDevices = null;
        if (request.Adaptive?.Devices is { } deviceSet)
        {
            try { boundDevices = Protection.Devices.DeviceSetValidator.Bind(deviceSet, model, request.Adaptive.Curves ?? new Protection.Devices.CurveLibrary()); }
            catch (Protection.Devices.DeviceSetException ex) { throw new InvalidDataException("adaptive.devices: " + ex.Message); }
        }
        // RC5 stage 3: the units' own relays bind to the case's generators before any work (absent for every incumbent request).
        // A unit is named by its generator name or, for a RAW machine (which has none), by its PSS/E key <bus>:<id>.
        // RC5: or for the full-model companion run only (DynamicsStudyRequest.UnitProtection), with or without the adaptive run.
        if (request.UnitProtection is not null && request.Adaptive?.UnitProtection is not null)
            throw new InvalidDataException("unit protection is given twice (for the full-model run and in the adaptive request); give it once.");
        Protection.UnitProtectionSet? fullModelUnits = request.UnitProtection ?? request.Adaptive?.UnitProtection;
        GridModel adaptiveModel = model, unitKeyedModel = model;
        if (fullModelUnits is { } unitProtection)
        {
            unitKeyedModel = UnitKeyed(raw);
            BindUnits(raw, unitKeyedModel, unitProtection);
            if (request.Adaptive?.UnitProtection is not null) adaptiveModel = unitKeyedModel;
        }
        // RC5 wp32b: user relays bind to the case's units (by their PSS/E key <bus>:<id>) and branches before any work (absent for
        // every existing request, and then nothing here runs).
        IReadOnlyList<GridSim.Core.Dynamics.Plugins.UserRelayBinding>? relayBindings = null;
        if (request.RelayModels is { Count: > 0 } relayModels)
        {
            unitKeyedModel = UnitKeyed(raw);
            relayBindings = BindRelays(raw, unitKeyedModel, relayModels);
        }
        int refGen = ResolveMachine(raw, request.AngleReference, "angleReference");
        var channelGen = new List<(DynamicsChannelRequest Channel, int? Gen, string Label)>();
        foreach (DynamicsChannelRequest ch in request.Channels)
        {
            if (ch.Kind == DynamicsChannelKind.BusVoltage)
            {
                if (ch.Bus is not int bus || ch.Machine is not null)
                    throw new InvalidDataException("a busVoltage channel names one bus and no machine.");
                if (!model.Buses.Any(b => b.Id == bus)) throw new InvalidDataException($"channel bus {bus} is not a bus of the RAW case.");
                channelGen.Add((ch, null, $"busVoltage {bus.ToString(CultureInfo.InvariantCulture)}"));
            }
            else
            {
                if (ch.Machine is null || ch.Bus is not null)
                    throw new InvalidDataException($"a {Name(ch.Kind)} channel names one machine and no bus.");
                int g = ResolveMachine(raw, ch.Machine, $"{Name(ch.Kind)} channel");
                channelGen.Add((ch, g, $"{Name(ch.Kind)} {ch.Machine.Bus.ToString(CultureInfo.InvariantCulture)}/'{ch.Machine.Id}'"));
            }
        }
        if (channelGen.Select(c => c.Label).Distinct(StringComparer.Ordinal).Count() != channelGen.Count)
            throw new InvalidDataException("a monitored channel is listed twice.");

        double nominalHz = model.FrequencyHz;
        var engine = new DynamicsEngineDeclaration(
            Engine: "GridSim RMS (RmsTransientStability.StudyEvents / MultiMachineSubtransient, six-state subtransient machines on the sparse network)",
            Scheme: "fixed step: flux states by exact zero-order-hold first-order lags, rotor swing by RK4 with the subtransient EMFs frozen and the network re-solved at each sub-step; the network stage in force is picked at each step start",
            StepSeconds: request.StepSeconds, DurationSeconds: request.DurationSeconds, NominalHz: nominalHz,
            SlipLimitRad: request.Criteria.SlipLimitRad, MaxAngleSpreadDeg: request.Criteria.MaxAngleSpreadDeg,
            EventLandingRule: $"an event takes effect at the first step k whose time k x h is within {MultiMachineSubtransient.StageTimeToleranceSteps.ToString("R", CultureInfo.InvariantCulture)} x h of its requested time or later; events are processed in time order, input order for equal times",
            CctGridRule: "each CCT trial clears after a whole number of integration steps (the search grid is the integration step), by integer bisection with downward verification and an upward pocket probe",
            CctCriterion: $"synchronism only: a machine further than {request.Criteria.SlipLimitRad.ToString("R", CultureInfo.InvariantCulture)} rad from the centre-of-inertia angle within onset + clearing + {RmsTransientStability.PostClearWindowSeconds.ToString("R", CultureInfo.InvariantCulture)} s; the angle spread limit is not applied by the search",
            AngleConvention: "rotor angles in degrees relative to the reference machine; bus voltage angles in degrees in the engine's synchronous frame (the power flow's slack angle at t = 0); speed as pu deviation of synchronous speed; frequency in Hz = f0 x (1 + speed)",
            MaxParallelism: request.MaxParallelism);

        // 2. Model coverage.
        // RC5 WP23: IEC 61400 units are bound by the coverage builder (none on every existing request: the historical call).
        DynamicsModelBinding binding = request.Iec61400Units is { Count: > 0 } iecUnits
            ? DynamicsModelCoverage.Build(raw, request.DyrText, request.DyrName, request.SchemasDir, request.Initialisation.Unmodelled, iecUnits)
            : DynamicsModelCoverage.Build(raw, request.DyrText, request.DyrName, request.SchemasDir, request.Initialisation.Unmodelled);
        // R4-08: declarative user models bound to machines (additive; absent for every existing request).
        if (request.UserModels is { Count: > 0 } userModels && request.PluginModels is not { Count: > 0 }) binding = UserModelOverlay.Apply(raw, binding, userModels);
        // RC5 WP12: plug-in models by the same route, after the declarative ones (absent for every existing request).
        else if (request.PluginModels is { Count: > 0 } pluginModels) binding = UserModelOverlay.ApplyAll(raw, binding, request.UserModels, pluginModels);
        // RC5 WP32: the injection, machine and load models are named in the coverage notes (absent for every existing request).
        if (request.DeviceModels is { Count: > 0 } deviceNotes && binding.Set is not null)
            binding = new DynamicsModelBinding(binding.Report with { ReaderNotes = binding.Report.ReaderNotes.Concat(deviceNotes.Select(a => a.Note)).ToList() }, binding.Set);
        // RC5 wp32b: the user relays likewise (absent for every existing request).
        if (request.RelayModels is { Count: > 0 } relayNotes && binding.Set is not null)
            binding = new DynamicsModelBinding(binding.Report with { ReaderNotes = binding.Report.ReaderNotes.Concat(relayNotes.Select(a => a.Note)).ToList() }, binding.Set);
        DynamicsModelCoverageReport coverage = binding.Report;
        if (!coverage.Simulable || binding.Set is null)
        {
            string why = coverage.Records.Any(r => r.Status == DyrRecordStatus.Unmatched)
                ? "a DYR record names a machine the RAW case does not hold (always refused)"
                : coverage.Units.Any(u => u.Status == "UNBOUND") ? "an in-service unit has no supported machine model"
                : "the model coverage is BLOCKED and the policy is Refuse";
            return Stopped(request, coverage, engine, null, "model coverage: " + why);
        }

        // 3. Machines and slots (from the bound data alone, so these refusals come before any solve).
        MachineDynamicsSet set = binding.Set;
        var machineList = new List<SubtransientInit.MachineData>(model.Generators.Count);
        for (int k = 0; k < model.Generators.Count; k++)
            machineList.Add((set.For(k) ?? throw new InvalidOperationException($"internal error: generator {k} has no bound machine data")).Data);
        // WP6 (RC5): the request's network device models (opt-in); without them the study takes the plain list, as before.
        // RC5 WP32: the injection, machine and load models (opt-in; none on every existing request, and then nothing here runs).
        List<DynamicLoadData>? userLoads = null;
        if (request.DeviceModels is { Count: > 0 } deviceModels)
            foreach (DeviceModelAssignment a in deviceModels)
            {
                if (a.Machine is { } dm)
                {
                    int gi = ResolveMachine(raw, dm, $"deviceModels ({a.Device.Model})");
                    // The unit is converter plant now, so its DYR governor is not used (as its exciter is not: Exciters). The
                    // kernel refuses a governor on a unit it does not integrate as a machine, so leaving it here stopped every
                    // study whose driven unit has a DYR governor at initialisation.
                    machineList[gi] = machineList[gi] with { UserInjection = a.Device, Governor = null };
                }
                else
                {
                    if (!model.Buses.Any(b => b.Id == a.Bus))
                        throw new InvalidDataException($"deviceModels ({a.Device.Model}): bus {a.Bus.ToString(CultureInfo.InvariantCulture)} is not a bus of the RAW case.");
                    (userLoads ??= new List<DynamicLoadData>()).Add(new GridSim.Core.Dynamics.Plugins.UserDynamicLoadData(a.Bus, a.PMw, a.QMvar, a.Device));
                }
            }
        IReadOnlyList<SubtransientInit.MachineData> data = request.Devices is { Count: > 0 } requestDevices
            ? new RmsLoadedMachineData(machineList, Array.Empty<DynamicLoadData>(), requestDevices) : machineList;
        if (userLoads is not null)
            data = new RmsLoadedMachineData(machineList, userLoads, request.Devices ?? Array.Empty<RmsNetworkDeviceData>());
        int[] slotOf = new int[model.Generators.Count];
        int slots = 0;
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator gen = model.Generators[k];
            bool conv = SubtransientInit.IsConverterUnit(gen, data[k]);
            bool held = gen.PowerFlowOnly || (!conv && !(data[k].H > 0.0));
            slotOf[k] = !held && !conv ? slots++ : -1;
        }
        if (slotOf[refGen] < 0)
            throw new InvalidDataException($"angleReference {request.AngleReference} is not integrated as a synchronous machine.");
        var channels = new List<(DynamicsChannelRequest, int?, string)>();
        foreach (var (ch, g, label) in channelGen)
        {
            if (g is int gi && slotOf[gi] < 0)
                throw new InvalidDataException($"channel {label}: the machine is not integrated as a synchronous machine.");
            channels.Add((ch, g is int gg ? slotOf[gg] : null, label));
        }
        // 4. Power flow and the no-event interval.
        var pfOptions = new PowerFlowOptions { Tolerance = request.Initialisation.PowerFlowTolerancePu, MaxIterations = request.Initialisation.PowerFlowMaxIterations };
        IPowerFlowSolver solver = SolverSelector.CreateNewton(model);
        PowerFlowResult pf = solver.Solve(model, pfOptions);
        var pfEvidence = new DynamicsPowerFlowEvidence(pf.Status.ToString(), pf.Converged, pf.Iterations, pf.MaxMismatch,
            pfOptions.Tolerance, pfOptions.MaxIterations, solver.GetType().Name);
        if (!pf.Converged)
            return Stopped(request, coverage, engine, new DynamicsInitialisationEvidence(pfEvidence, Array.Empty<DynamicsInitialMachine>(), null),
                "NO SOLUTION: the power flow did not converge, so there is no initial state");

        var channelBuses = channelGen.Where(c => c.Channel.Kind == DynamicsChannelKind.BusVoltage).Select(c => c.Channel.Bus!.Value).ToList();
        bool indicative = !coverage.LikeForLike;
        var ctx = new Context(model, pf, data, set, slotOf, slotOf[refGen], nominalHz, request, indicative, channels, channelBuses);

        RmsTransientStability.ObservedStudy flat;
        try
        {
            flat = RmsTransientStability.StudyEventsObserved(model, pf, data, Array.Empty<RmsEvent>(), model.Buses.Select(b => b.Id).ToList(),
                stepSeconds: request.StepSeconds, durationSeconds: request.DurationSeconds,
                slipLimitRad: request.Criteria.SlipLimitRad, exciters: Exciters(ctx));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Stopped(request, coverage, engine, new DynamicsInitialisationEvidence(pfEvidence, Array.Empty<DynamicsInitialMachine>(), null),
                "the machines cannot be initialised at the solved operating point: " + ex.Message);
        }
        IReadOnlyList<DynamicsInitialMachine> initial = InitialMachines(ctx, raw, flat.Prepared);
        DynamicsEquilibriumEvidence eq = Equilibrium(ctx, flat);
        var init = new DynamicsInitialisationEvidence(pfEvidence, initial, eq);
        if (!eq.Held)
            return Stopped(request, coverage, engine, init, "the no-event interval drifted beyond its limits (" + string.Join("; ", eq.Exceeded) + "), so no disturbance is judged");

        // 5. The rows.
        int n = request.Cases.Count;
        var results = new DynamicsCaseResult?[n];
        void RunRow(int i)
        {
            DynamicsCaseRequest c = request.Cases[i];
            if (cancellation.IsCancellationRequested)
            {
                results[i] = new DynamicsCaseResult(i, c.Name, c.Kind, DynamicsDisposition.NotAttempted, "the run was cancelled before this row started",
                    indicative, null, null, null, null);
                return;
            }
            results[i] = c.Kind == DynamicsCaseKind.Events
                ? EventRow(ctx, i, c, resolvedCases[i]!)
                : CctRow(ctx, i, c, resolvedTrips[i]!);
        }
        if (request.MaxParallelism <= 1)
            for (int i = 0; i < n; i++) RunRow(i);
        else
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = request.MaxParallelism }, RunRow);

        // 6. Aggregate.
        DynamicsStudyResult aggregated = Aggregate(request, coverage, engine, init, results.Select(r => r!).ToList());

        // 7. R4-14: the opt-in variable-step companion run (never part of the incumbent path; absent leaves the result untouched).
        if (request.Adaptive is null)
        {
            // RC5 wp32b: user relays (with or without unit protection): the full-model companion rows with them acting.
            if (relayBindings is not null)
                return aggregated with
                {
                    FullModelUnitRelays = FullModelUnitRelayRows(ctx, fullModelUnits is null ? unitKeyedModel : unitKeyedModel.WithUnitProtection(fullModelUnits),
                        resolvedCases, cancellation, relayBindings),
                };
            if (fullModelUnits is null) return aggregated;
            // RC5: unit protection without the adaptive run: the full-model companion rows only (step 8 below, same code).
            return aggregated with { FullModelUnitRelays = FullModelUnitRelayRows(ctx, unitKeyedModel.WithUnitProtection(fullModelUnits), resolvedCases, cancellation) };
        }
        DynamicsAdaptiveEvidence adaptive = DynamicsAdaptiveRunner.Run(adaptiveModel, pf, data, request,
            resolvedCases.Select(l => (IReadOnlyList<RmsEvent>?)l?.Select(e => e.Engine).ToList()).ToArray(), resolvedTrips.ToArray(),
            channels, slotOf, slotOf[refGen], nominalHz, boundDevices, cancellation);
        if (fullModelUnits is not { } units) return aggregated with { Adaptive = adaptive };

        // 8. RC5 stage 6: with unit protection, each events row also runs on the full-model fixed-step engine with the units'
        //    own relays in it (incl. 40). Additional evidence only: the rows above are unchanged.
        return aggregated with { Adaptive = adaptive, FullModelUnitRelays = FullModelUnitRelayRows(ctx, unitKeyedModel.WithUnitProtection(units), resolvedCases, cancellation) };
    }

    /// <summary>RC5 stage 6: the full-model unit relay companion rows (see <see cref="DynamicsFullModelUnitRelayEvidence"/>).</summary>
    private static DynamicsFullModelUnitRelayEvidence FullModelUnitRelayRows(Context ctx, GridModel keyed,
        IReadOnlyList<IReadOnlyList<ResolvedEvent>?> resolvedCases, CancellationToken cancellation,
        IReadOnlyList<GridSim.Core.Dynamics.Plugins.UserRelayBinding>? userRelays = null)
    {
        DynamicsStudyRequest request = ctx.Request;
        int n = request.Cases.Count;
        var rows = new DynamicsFullModelUnitRelayRow?[n];
        var notEvaluated = new IReadOnlyList<Dynamics.Models.Adaptive.UnitRelayNotEvaluated>?[n];
        void RunRow(int i)
        {
            DynamicsCaseRequest c = request.Cases[i];
            if (cancellation.IsCancellationRequested)
            {
                rows[i] = new(i, c.Name, DynamicsDisposition.NotAttempted, "the run was cancelled before this row started", null, null, null, null);
                return;
            }
            if (c.Kind != DynamicsCaseKind.Events)
            {
                rows[i] = new(i, c.Name, DynamicsDisposition.NotRun, "a critical clearing time row does not run unit relays", null, null, null, null);
                return;
            }
            try
            {
                // RC5 wp32b: with user relays, the run that takes them (the units' own relays act too when the model carries them).
                RmsTransientStability.UnitRelayStudy s = userRelays is null
                    ? RmsTransientStability.StudyEventsWithUnitRelays(keyed, ctx.Pf, ctx.Data,
                        resolvedCases[i]!.Select(e => e.Engine).ToList(), stepSeconds: request.StepSeconds, durationSeconds: request.DurationSeconds,
                        slipLimitRad: request.Criteria.SlipLimitRad, exciters: Exciters(ctx))
                    : RmsTransientStability.StudyEventsWithUserRelays(keyed, ctx.Pf, ctx.Data,
                        resolvedCases[i]!.Select(e => e.Engine).ToList(), userRelays, stepSeconds: request.StepSeconds, durationSeconds: request.DurationSeconds,
                        slipLimitRad: request.Criteria.SlipLimitRad, exciters: Exciters(ctx));
                notEvaluated[i] = s.NotEvaluated;
                rows[i] = new(i, c.Name, DynamicsDisposition.Evaluated, null, s.Result.StayedInSynchronism, s.EndedAtSeconds, s.EndReason, s.UnitRelays)
                {
                    BranchRelays = userRelays is not null && userRelays.Any(b => b.BranchIndex is not null) ? s.BranchRelays : null,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                rows[i] = new(i, c.Name, DynamicsDisposition.Failed, "the engine refused the row: " + ex.Message, null, null, null, null);
            }
        }
        if (request.MaxParallelism <= 1)
            for (int i = 0; i < n; i++) RunRow(i);
        else
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = request.MaxParallelism }, RunRow);

        var trips = new List<DynamicsFullModelUnitTrip>();
        foreach (DynamicsFullModelUnitRelayRow r in rows.Select(x => x!))
            foreach (FullModelUnitRelayRecord u in r.UnitRelays ?? Array.Empty<FullModelUnitRelayRecord>())
                if (u.Tripped)
                    trips.Add(new DynamicsFullModelUnitTrip(r.Index, r.Name, u.Unit, u.Device, u.OperateSeconds!.Value, u.DisconnectedSeconds!.Value,
                        u.OperateQuantity, u.ImpedanceRPu, u.ImpedanceXPu, u.Settings));
        // The stages not evaluated depend on the model and the run's nominal frequency only, so every row lists the same.
        IReadOnlyList<Dynamics.Models.Adaptive.UnitRelayNotEvaluated> skipped = notEvaluated.FirstOrDefault(x => x is not null)
            ?? Array.Empty<Dynamics.Models.Adaptive.UnitRelayNotEvaluated>();
        string hStep = request.StepSeconds.ToString("R", CultureInfo.InvariantCulture);
        if (userRelays is not null)
        {
            // RC5 wp32b: the companion run with user relays names its entry point and adds the user relay rule; branch trips are listed.
            var branchTrips = new List<DynamicsFullModelBranchTrip>();
            foreach (DynamicsFullModelUnitRelayRow r in rows.Select(x => x!))
                foreach (FullModelBranchRelayRecord b in r.BranchRelays ?? Array.Empty<FullModelBranchRelayRecord>())
                    if (b.Tripped)
                        branchTrips.Add(new DynamicsFullModelBranchTrip(r.Index, r.Name, b.Branch, b.Device, b.OperateSeconds!.Value, b.DisconnectedSeconds!.Value,
                            b.ReasonCode, b.Settings));
            return new DynamicsFullModelUnitRelayEvidence(
                Engine: $"GridSim RMS full model (RmsTransientStability.StudyEventsWithUserRelays / MultiMachineSubtransient, six-state machines with flux decay and their exciters and governors, fixed step {hStep} s)",
                Rule: keyed.UnitProtection is null ? RmsTransientStability.UserRelayRule
                    : RmsTransientStability.FullModelUnitRelayRule + "; " + RmsTransientStability.UserRelayRule,
                StatusRule: "the fixed-step rows (cases) are run without unit or user relays, unchanged, and decide the study status; these rows are the same events re-run with the relays acting, as additional evidence",
                NotEvaluated: skipped, Trips: trips, Rows: rows.Select(r => r!).ToList())
            {
                BranchTrips = userRelays.Any(b => b.BranchIndex is not null) ? branchTrips : null,
            };
        }
        return new DynamicsFullModelUnitRelayEvidence(
            Engine: $"GridSim RMS full model (RmsTransientStability.StudyEventsWithUnitRelays / MultiMachineSubtransient, six-state machines with flux decay and their exciters and governors, fixed step {hStep} s)",
            Rule: RmsTransientStability.FullModelUnitRelayRule,
            StatusRule: "the fixed-step rows (cases) are run without unit relays, unchanged, and decide the study status; these rows are the same events re-run with the units' own relays acting, as additional evidence",
            NotEvaluated: skipped, Trips: trips, Rows: rows.Select(r => r!).ToList());
    }

    private static void Validate(DynamicsStudyRequest r)
    {
        if (r.Initialisation is null || r.Criteria is null || r.AngleReference is null || r.Channels is null || r.Cases is null
            || r.DyrText is null || r.DyrName is null || r.SchemasDir is null)
            throw new InvalidDataException("The study request is missing a required part.");
        if (!(r.StepSeconds > 0.0) || !double.IsFinite(r.StepSeconds))
            throw new InvalidDataException("integration.stepSeconds must be a positive, finite number.");
        if (!(r.DurationSeconds > 0.0) || !double.IsFinite(r.DurationSeconds) || r.DurationSeconds < r.StepSeconds)
            throw new InvalidDataException("integration.durationSeconds must be finite and at least one step.");
        double steps = r.DurationSeconds / r.StepSeconds;
        if (Math.Abs(steps - Math.Round(steps)) > 1e-9 * Math.Max(1.0, steps))
            throw new InvalidDataException("integration.durationSeconds must be a whole number of steps (the engine rounds otherwise).");
        if (!(r.Initialisation.PowerFlowTolerancePu > 0.0) || !(r.Initialisation.PowerFlowTolerancePu <= PowerFlowOptions.MaxTolerance))
            throw new InvalidDataException($"initialisation.powerFlow.tolerancePu must be above 0 and at most {PowerFlowOptions.MaxTolerance} pu.");
        if (r.Initialisation.PowerFlowMaxIterations < 1)
            throw new InvalidDataException("initialisation.powerFlow.maxIterations must be at least 1.");
        if (!(r.Criteria.SlipLimitRad > 0.0) || !double.IsFinite(r.Criteria.SlipLimitRad))
            throw new InvalidDataException("criteria.slipLimitRad must be a positive, finite number.");
        if (!(r.Criteria.MaxAngleSpreadDeg > 0.0) || !double.IsFinite(r.Criteria.MaxAngleSpreadDeg))
            throw new InvalidDataException("criteria.maxAngleSpreadDeg must be a positive, finite number.");
        if (r.MaxParallelism < 1) throw new InvalidDataException("execution.maxParallelism must be at least 1.");
        if (r.Adaptive is { } ad)
        {
            try { ad.Options.Validate(); }
            catch (ArgumentException ex) { throw new InvalidDataException("adaptive: " + ex.Message); }
            if (!(ad.CctToleranceSeconds > 0.0) || !double.IsFinite(ad.CctToleranceSeconds))
                throw new InvalidDataException("adaptive.cctToleranceSeconds must be a positive, finite number.");
        }
        if (r.StepSchedule is not null || r.ErrorControl is not null)
        {
            // WP21 (RC5): the opt-in step schedule and error-controlled step (both absent on every existing request).
            if (r.StepSchedule is not null && r.ErrorControl is not null)
                throw new InvalidDataException("integration: give stepSchedule or errorControl, not both.");
            if (r.UserModels is { Count: > 0 } || r.PluginModels is { Count: > 0 })
                throw new InvalidDataException("integration: a step schedule or the error-controlled step does not run user or plug-in models, because GridSim cannot check that they take the step on each call.");
            if (r.StepSchedule is { } sch)
            {
                try { _ = sch.Expand(r.DurationSeconds); }
                catch (ArgumentException ex) { throw new InvalidDataException("integration.stepSchedule: " + ex.Message); }
            }
            if (r.ErrorControl is { } ec)
            {
                try { ec.Validate(); }
                catch (ArgumentException ex) { throw new InvalidDataException("integration.errorControl: " + ex.Message); }
            }
        }
        if (r.Iec61400Units is { Count: > 0 } iecUnits)
        {
            // RC5 WP23: IEC 61400 units (absent on every existing request).
            if (r.Adaptive is not null)
                throw new InvalidDataException("iec61400: the variable-step companion run (adaptive) holds no converter units; remove one of the two.");
            if (r.StepSchedule is not null || r.ErrorControl is not null)
                throw new InvalidDataException("iec61400: an IEC 61400 unit runs at one step size (its timers count whole steps), so it does not run with a step schedule or the error-controlled step.");
            var seen = new HashSet<(int, string)>();
            foreach (Iec61400StudyUnit u in iecUnits)
            {
                if (u?.Machine is null || u.Data is null) throw new InvalidDataException("iec61400: every unit needs a machine and its data.");
                if (!seen.Add((u.Machine.Bus, u.Machine.Id))) throw new InvalidDataException($"iec61400: {u.Machine} is given twice.");
                if (r.UserModels?.Any(a => a.Machine == u.Machine) == true || r.PluginModels?.Any(a => a.Machine == u.Machine) == true)
                    throw new InvalidDataException($"iec61400: {u.Machine} also has a user or plug-in model; an IEC 61400 unit has no governor or exciter.");
                IReadOnlyList<string> problems = Dynamics.Models.Iec61400.Iec61400Data.Problems(u.Data);
                if (problems.Count > 0) throw new InvalidDataException($"iec61400: {u.Machine}: " + string.Join(" ", problems));
            }
        }
        if (r.DeviceModels is { Count: > 0 } deviceModels)
        {
            // RC5 WP32: injection, machine and load models (absent on every existing request).
            if (r.Adaptive is not null)
                throw new InvalidDataException("deviceModels: the variable-step companion run (adaptive) holds no converter units or user loads; remove one of the two.");
            if (r.StepSchedule is not null || r.ErrorControl is not null)
                throw new InvalidDataException("deviceModels: a step schedule or the error-controlled step does not run user or plug-in models, because GridSim cannot check that they take the step on each call.");
            var units = new HashSet<(int, string)>();
            foreach (DeviceModelAssignment a in deviceModels)
            {
                if (a is null) throw new InvalidDataException("deviceModels: an entry is null.");
                if (a.Machine is not { } m) continue;
                string who = $"deviceModels: {m}";
                if (!units.Add((m.Bus, m.Id))) throw new InvalidDataException($"{who} is given two injection or machine models.");
                if (r.UserModels?.Any(u => u.Machine == m) == true || r.PluginModels?.Any(u => u.Machine == m) == true)
                    throw new InvalidDataException($"{who} also has a governor or exciter model; a unit driven by an injection or machine model has none.");
                if (r.Iec61400Units?.Any(u => u.Machine == m) == true)
                    throw new InvalidDataException($"{who} is also an IEC 61400 unit; a unit takes one model.");
                if (r.AngleReference == m)
                    throw new InvalidDataException($"{who} is the angleReference; a unit driven by an injection or machine model is not integrated as a synchronous machine.");
            }
        }
        if (r.RelayModels is { Count: > 0 } relayModels)
        {
            // RC5 wp32b: user relays (absent on every existing request).
            if (r.Adaptive is not null)
                throw new InvalidDataException("relay models: the variable-step companion run (adaptive) does not run user relays; remove one of the two.");
            if (r.StepSchedule is not null || r.ErrorControl is not null)
                throw new InvalidDataException("relay models: a relay's timers count whole steps of one size, so it does not run with a step schedule or the error-controlled step.");
            foreach (RelayModelAssignment a in relayModels)
                if (a is null || (a.Machine is null) == (a.Branch is null)) throw new InvalidDataException("relay models: every relay is bound to one unit or one branch.");
        }
        if (r.Cases.Count == 0) throw new InvalidDataException("cases must hold at least one row.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (DynamicsCaseRequest c in r.Cases)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Name)) throw new InvalidDataException("every case needs a non-empty name.");
            if (!names.Add(c.Name)) throw new InvalidDataException($"case name '{c.Name}' is used twice.");
            if (c.Kind == DynamicsCaseKind.Events)
            {
                if (c.Events is null || c.Events.Count == 0 || c.Cct is not null)
                    throw new InvalidDataException($"case '{c.Name}': an events row needs a non-empty event list and no cct block.");
                foreach (DynamicsEventRequest e in c.Events)
                    if (e is null || !double.IsFinite(e.AtSeconds) || !(e.AtSeconds > 0.0) || e.AtSeconds > r.DurationSeconds)
                        throw new InvalidDataException($"case '{c.Name}': every event time must be finite, after 0 s and within the duration.");
                if (r.StepSchedule is { } schedule)
                    foreach (DynamicsEventRequest e in c.Events)
                    {
                        // WP21: an event strictly inside a coarse window is refused before any work.
                        try { schedule.RequireEventPlacement(e.AtSeconds, $"the {e.Kind} event"); }
                        catch (ArgumentException ex) { throw new InvalidDataException($"case '{c.Name}': integration.stepSchedule: {ex.Message}"); }
                    }
            }
            else
            {
                if (c.Cct is null || c.Events is not null) throw new InvalidDataException($"case '{c.Name}': a CCT row needs a cct block and no event list.");
                DynamicsCctRequest q = c.Cct;
                if (q.ClearingTrips is null) throw new InvalidDataException($"case '{c.Name}': cct.clearingTrips is required (it may be empty).");
                if (!(q.OnsetSeconds > 0.0) || !double.IsFinite(q.OnsetSeconds))
                    throw new InvalidDataException($"case '{c.Name}': cct.onsetSeconds must be positive and finite.");
                if (!(q.MaxClearingSeconds >= r.StepSeconds) || !double.IsFinite(q.MaxClearingSeconds))
                    throw new InvalidDataException($"case '{c.Name}': cct.maxClearingSeconds must be finite and at least one step.");
                if (q.RefinedStepSeconds is double rs && (!(rs > 0.0) || !(rs < r.StepSeconds)))
                    throw new InvalidDataException($"case '{c.Name}': cct.refinedStepSeconds must be positive and smaller than the study step.");
                if (q.Iterations < 1 || q.ScanSteps < 0)
                    throw new InvalidDataException($"case '{c.Name}': cct.iterations must be at least 1 and cct.scanSteps 0 or more.");
            }
        }
    }

    private static string Name(DynamicsChannelKind k) => k switch
    {
        DynamicsChannelKind.RotorAngle => "rotorAngle",
        DynamicsChannelKind.Speed => "speed",
        DynamicsChannelKind.Frequency => "frequency",
        _ => "busVoltage",
    };

    /// <summary>The index into <c>model.Generators</c> of the in-service unit with this native identity.</summary>
    /// <summary>
    /// RC5: the RAW case's model with each unnamed generator named by its PSS/E machine key <c>&lt;bus&gt;:&lt;id&gt;</c>
    /// (<see cref="IO.Json.UnitProtectionFile.MachineKey"/>; the RAW reader names no machine), so unit protection binds to
    /// RAW machines by that key. A generator that has a name keeps it. Only names change: every number, list and order is
    /// the RAW model's. Used for the unit relay companion runs only, when unit protection is asked for.
    /// </summary>
    internal static GridModel UnitKeyed(PsseParseResult raw)
    {
        GridModel model = raw.Model;
        if (raw.MachineKeys.Count != model.AllGenerators.Count)
            throw new InvalidDataException("adaptive.unitProtection: the RAW case's machine keys do not match its generators, so units cannot be named by <bus>:<id>.");
        if (model.AllGenerators.All(g => g.Name is not null)) return model;
        var gens = new List<Generator>(model.AllGenerators.Count);
        for (int k = 0; k < model.AllGenerators.Count; k++)
        {
            Generator g = model.AllGenerators[k];
            gens.Add(g.Name is null ? g with { Name = IO.Json.UnitProtectionFile.MachineKey(raw.MachineKeys[k].Bus, raw.MachineKeys[k].Id) } : g);
        }
        return model.CopyWith(generators: gens, generatorsAreWholeFleet: true);
    }

    // RC5: every unit the set names must be a unit of the case (fail loud, every unknown unit listed), then the model's own
    // check (a unique name).
    private static void BindUnits(PsseParseResult raw, GridModel keyed, Protection.UnitProtectionSet units)
    {
        var names = new HashSet<string>(keyed.AllGenerators.Where(g => g.Name is not null).Select(g => g.Name!), StringComparer.Ordinal);
        var unknown = units.Resolved.Select(u => u.Unit).Where(u => !names.Contains(u)).ToList();
        if (unknown.Count > 0)
        {
            string example = raw.MachineKeys.Count > 0 ? IO.Json.UnitProtectionFile.MachineKey(raw.MachineKeys[0].Bus, raw.MachineKeys[0].Id) : "101:1";
            throw new InvalidDataException("adaptive.unitProtection: " + string.Join("; ", unknown.Select(u => $"unit '{u}' is not a machine of the RAW case"))
                + $". A RAW-bound study names a unit by its PSS/E machine key <bus>:<id>, the generator record's bus number and machine ID as written in the RAW file (for example '{example}'; the ID is case-sensitive).");
        }
        try { _ = keyed.WithUnitProtection(units); }
        catch (InvalidDataException ex) { throw new InvalidDataException("adaptive.unitProtection: " + ex.Message); }
    }

    /// <summary>RC5 wp32b: each user relay bound to the keyed model: a unit by its key (the generator's name in
    /// <paramref name="keyed"/>), a branch by its index, read at its From bus as written. Every unit and branch must be in
    /// service in the case.</summary>
    private static List<GridSim.Core.Dynamics.Plugins.UserRelayBinding> BindRelays(PsseParseResult raw, GridModel keyed,
        IReadOnlyList<RelayModelAssignment> relays)
    {
        var list = new List<GridSim.Core.Dynamics.Plugins.UserRelayBinding>(relays.Count);
        foreach (RelayModelAssignment a in relays)
        {
            string what = $"relay models ({a.Device})";
            try
            {
                if (a.Machine is { } m)
                {
                    int gi = ResolveMachine(raw, m, what);
                    Generator g = raw.Model.Generators[gi];
                    int k = -1;
                    for (int j = 0; j < raw.Model.AllGenerators.Count; j++) if (ReferenceEquals(raw.Model.AllGenerators[j], g)) { k = j; break; }
                    string unit = keyed.AllGenerators[k].Name
                        ?? throw new InvalidOperationException("internal error: a keyed unit has no name");
                    list.Add(a.Plugin is { } p ? new GridSim.Core.Dynamics.Plugins.UserRelayBinding(unit, p)
                        : GridSim.Core.Dynamics.Plugins.UserRelayBinding.ForUnit(unit, a.Declared!));
                }
                else
                {
                    DynamicsBranchId b = a.Branch!;
                    int bi = ResolveBranch(raw, b, what);
                    string label = b.ToString();
                    list.Add(a.Plugin is { } p ? GridSim.Core.Dynamics.Plugins.UserRelayBinding.ForBranch(bi, b.From, p, label)
                        : GridSim.Core.Dynamics.Plugins.UserRelayBinding.ForBranch(bi, b.From, a.Declared!, label));
                }
            }
            catch (ArgumentException ex) { throw new InvalidDataException($"{what}: {ex.Message}"); }
        }
        return list;
    }

    private static int ResolveMachine(PsseParseResult raw, DynamicsMachineId id, string what)
    {
        if (id is null || id.Id is null) throw new InvalidDataException($"{what}: a machine needs a bus and an ID.");
        GridModel model = raw.Model;
        int found = -1;
        for (int k = 0; k < raw.MachineKeys.Count; k++)
            if (raw.MachineKeys[k].Bus == id.Bus && raw.MachineKeys[k].Id == id.Id.Trim()) { found = k; break; }
        if (found < 0) throw new InvalidDataException($"{what}: {id} is not a machine of the RAW case.");
        Generator g = model.AllGenerators[found];
        if (!g.InService) throw new InvalidDataException($"{what}: {id} is out of service in the RAW case.");
        for (int j = 0; j < model.Generators.Count; j++) if (ReferenceEquals(model.Generators[j], g)) return j;
        throw new InvalidOperationException("internal error: an in-service unit is not among the model's generators");
    }

    /// <summary>The index into <c>model.Branches</c> of the in-service branch with this native identity (either
    /// orientation; three-winding windings are not addressable in v1).</summary>
    private static int ResolveBranch(PsseParseResult raw, DynamicsBranchId id, string what)
    {
        if (id is null || id.Circuit is null) throw new InvalidDataException($"{what}: a branch needs from, to and circuit.");
        var matches = new List<int>();
        for (int j = 0; j < raw.BranchKeys.Count; j++)
        {
            PsseBranchKey k = raw.BranchKeys[j];
            if (k.Winding != 0 || k.Circuit.Trim() != id.Circuit.Trim()) continue;
            if ((k.FromBus == id.From && k.ToBus == id.To) || (k.FromBus == id.To && k.ToBus == id.From)) matches.Add(j);
        }
        if (matches.Count == 0)
        {
            bool skipped = raw.SkippedBranchKeys.Any(k => k.Circuit.Trim() == id.Circuit.Trim()
                && ((k.FromBus == id.From && k.ToBus == id.To) || (k.FromBus == id.To && k.ToBus == id.From)));
            throw new InvalidDataException($"{what}: {id} is " + (skipped ? "out of service in the RAW case." : "not a branch of the RAW case."));
        }
        if (matches.Count > 1) throw new InvalidDataException($"{what}: {id} matches {matches.Count} records of the RAW case; it is ambiguous.");
        if (!raw.Model.Branches[matches[0]].InService) throw new InvalidDataException($"{what}: {id} is out of service in the RAW case.");
        return matches[0];
    }

    private static ResolvedEvent Resolve(PsseParseResult raw, DynamicsEventRequest e, string what)
    {
        GridModel model = raw.Model;
        int Bus()
        {
            if (e.Bus is not int b || e.Branch is not null || e.Machine is not null)
                throw new InvalidDataException($"{what}: a {e.Kind} event names one bus and nothing else.");
            if (!model.Buses.Any(x => x.Id == b)) throw new InvalidDataException($"{what}: bus {b} is not a bus of the RAW case.");
            return b;
        }
        int Branch()
        {
            if (e.Branch is null || e.Bus is not null || e.Machine is not null)
                throw new InvalidDataException($"{what}: a {e.Kind} event names one branch and nothing else.");
            return ResolveBranch(raw, e.Branch, what);
        }
        if (e.Kind != DynamicsEventKind.LoadStep && (e.PMw is not null || e.QMvar is not null))
            throw new InvalidDataException($"{what}: only a loadStep event takes pMw and qMvar.");
        bool faultKind = e.Kind is DynamicsEventKind.SequenceFault or DynamicsEventKind.LineFault;
        if (!faultKind && (e.FaultType is not null || e.FaultRPu is not null || e.FaultXPu is not null))
            throw new InvalidDataException($"{what}: only a sequenceFault or lineFault event takes type, rPu and xPu.");
        if (e.Kind != DynamicsEventKind.LineFault && e.Fraction is not null)
            throw new InvalidDataException($"{what}: only a lineFault event takes fraction.");
        string b = e.Bus?.ToString(CultureInfo.InvariantCulture) ?? "";
        switch (e.Kind)
        {
            case DynamicsEventKind.BusFault: { int bus = Bus(); return new(e, new RmsBusFault(e.AtSeconds, bus), $"bus {b}", null); }
            case DynamicsEventKind.FaultClear: { int bus = Bus(); return new(e, new RmsFaultClear(e.AtSeconds, bus), $"bus {b}", null); }
            case DynamicsEventKind.BranchTrip: { int j = Branch(); return new(e, new RmsBranchTrip(e.AtSeconds, j), e.Branch!.ToString(), null); }
            case DynamicsEventKind.BranchReclose: { int j = Branch(); return new(e, new RmsBranchReclose(e.AtSeconds, j), e.Branch!.ToString(), null); }
            case DynamicsEventKind.MachineTrip:
            {
                if (e.Machine is null || e.Bus is not null || e.Branch is not null)
                    throw new InvalidDataException($"{what}: a MachineTrip event names one machine and nothing else.");
                int g = ResolveMachine(raw, e.Machine, what);
                return new(e, new RmsGeneratorTrip(e.AtSeconds, g), e.Machine.ToString(), g);
            }
            case DynamicsEventKind.SequenceFault:
            {
                int bus = Bus();
                if (e.FaultType is not GridSim.Core.Protection.FaultType t) throw new InvalidDataException($"{what}: a sequenceFault event needs a type.");
                return new(e, new RmsSequenceFault(e.AtSeconds, bus, t, e.FaultRPu ?? 0.0, e.FaultXPu ?? 0.0), $"bus {b}", null);
            }
            case DynamicsEventKind.LineFault:
            {
                int j = Branch();
                if (e.FaultType is not GridSim.Core.Protection.FaultType t) throw new InvalidDataException($"{what}: a lineFault event needs a type.");
                if (e.Fraction is not double d) throw new InvalidDataException($"{what}: a lineFault event needs a fraction.");
                return new(e, new RmsBranchSequenceFault(e.AtSeconds, j, d, t, e.FaultRPu ?? 0.0, e.FaultXPu ?? 0.0), e.Branch!.ToString(), null);
            }
            case DynamicsEventKind.LineFaultClear: { int j = Branch(); return new(e, new RmsBranchFaultClear(e.AtSeconds, j), e.Branch!.ToString(), null); }
            case DynamicsEventKind.LoadStep:
            {
                int bus = Bus();
                if (e.PMw is not double p || e.QMvar is not double q || !double.IsFinite(p) || !double.IsFinite(q))
                    throw new InvalidDataException($"{what}: a loadStep event needs finite pMw and qMvar.");
                return new(e, new RmsLoadStep(e.AtSeconds, bus, p, q), $"bus {b}", null);
            }
            default: throw new InvalidDataException($"{what}: event kind {e.Kind} is not supported.");
        }
    }

    /// <summary>A fresh exciter per bound machine (an exciter is stateful), aligned to <c>model.Generators</c>; null when
    /// no machine has one (the held-field path, as the RMS verb takes it).</summary>
    private static List<RmsExciter?>? Exciters(Context ctx)
    {
        bool userExciters = ctx.Request.UserModels?.Any(a => a.Model.Role == "exciter") == true   // R4-08
            || ctx.Request.PluginModels?.Any(a => a.Model.Role == GridSim.Core.Dynamics.Plugins.UserModelRole.Exciter) == true;   // RC5 WP12
        if (!ctx.Set.Entries.Any(e => e.Exciter is not null) && !userExciters) return null;
        var list = Enumerable.Repeat<RmsExciter?>(null, ctx.Model.Generators.Count).ToList();
        foreach (MachineDynamicsEntry e in ctx.Set.Entries)
            if (e.Exciter is not null) list[e.GeneratorIndex] = e.Exciter.Create();
        if (userExciters) UserModelOverlay.ApplyAllExciters(ctx.Request.Raw, list, ctx.Request.UserModels, ctx.Request.PluginModels);
        // RC5 WP32: a unit driven by an injection or machine model is converter plant, which takes no exciter.
        if (ctx.Request.DeviceModels is { Count: > 0 } deviceModels)
            foreach (DeviceModelAssignment a in deviceModels)
                if (a.Machine is { } dm) list[ResolveMachine(ctx.Request.Raw, dm, "deviceModels")] = null;
        return list;
    }

    private static IReadOnlyList<DynamicsInitialMachine> InitialMachines(Context ctx, PsseParseResult raw, SubtransientInit.Prepared p)
    {
        var list = new List<DynamicsInitialMachine>();
        GridModel model = ctx.Model;
        for (int k = 0; k < model.Generators.Count; k++)
        {
            int s = ctx.SlotOfGenerator[k];
            if (s < 0) continue;
            Generator g = model.Generators[k];
            if (p.GeneratorBuses[s] != g.Bus)
                throw new InvalidOperationException($"internal error: machine slot {s} is at bus {p.GeneratorBuses[s]}, generator {k} at bus {g.Bus}");
            PsseMachineKey key = KeyOf(raw, g);
            int bi = IndexOfBus(ctx.Pf, g.Bus);
            MachineDynamicsEntry? e = ctx.Set.For(k);
            list.Add(new DynamicsInitialMachine(s, key.Bus, key.Id, k, p.Delta0[s], p.Eqp0[s], p.Edp0[s], p.Eqpp0[s], p.Edpp0[s],
                p.Pm[s], p.Efd[s], p.H[s], ctx.Pf.Vm[bi], ctx.Pf.VaDeg[bi], e?.Exciter?.Model, e?.Data.Governor?.Model));
        }
        return list;
    }

    private static PsseMachineKey KeyOf(PsseParseResult raw, Generator g)
    {
        for (int k = 0; k < raw.Model.AllGenerators.Count; k++)
            if (ReferenceEquals(raw.Model.AllGenerators[k], g)) return raw.MachineKeys[k];
        throw new InvalidOperationException("internal error: generator not in the RAW fleet");
    }

    private static int IndexOfBus(PowerFlowResult pf, int bus)
    {
        for (int i = 0; i < pf.BusIds.Count; i++) if (pf.BusIds[i] == bus) return i;
        throw new InvalidOperationException($"internal error: bus {bus} not in the power-flow result");
    }

    /// <summary>
    /// The no-event interval: residuals at t = 0 and drift over the duration, against limits derived from the Newton
    /// tolerance. With eps = max(tolerance, achieved mismatch) (pu, system base) and kappa = <see cref="EquilibriumSafetyFactor"/>:
    /// an accelerating-power residual of at most kappa eps on the smallest system-base inertia H gives, ignoring the
    /// restoring synchronising torque (so the bound is conservative), a speed deviation of at most kappa eps T / (2 H) and
    /// a relative rotor angle of at most omega_s kappa eps T^2 / (2 H) after T seconds (the sum of two machines' worst
    /// cases omega_s kappa eps T^2 / (4 H)). The network voltages at t = 0 reproduce the solved voltages to within the
    /// mismatch over a driving-point admittance of at least 1 pu (kappa eps); their drift is that plus the angle bound
    /// (a voltage moves at most 1 pu per rad of internal angle).
    /// </summary>
    private static DynamicsEquilibriumEvidence Equilibrium(Context ctx, RmsTransientStability.ObservedStudy flat)
    {
        double tol = ctx.Request.Initialisation.PowerFlowTolerancePu;
        double eps = Math.Max(tol, ctx.Pf.MaxMismatch);
        double kappa = EquilibriumSafetyFactor;
        double T = ctx.Request.DurationSeconds, h = ctx.Request.StepSeconds;
        SubtransientInit.Prepared p = flat.Prepared;
        int real = p.Machines.Count - (p.IslandReferences?.Count ?? 0);
        double hMin = double.PositiveInfinity;
        for (int s = 0; s < real; s++) hMin = Math.Min(hMin, p.H[s]);
        double ws = 2.0 * Math.PI * ctx.NominalHz;

        // Initial voltage residual against the solved case.
        double vRes = 0.0; int vBus = 0;
        RmsTransientStability.ObservedBusVoltages buses = flat.Buses;
        for (int i = 0; i < buses.BusIds.Count; i++)
        {
            int bi = IndexOfBus(ctx.Pf, buses.BusIds[i]);
            double va = ctx.Pf.VaDeg[bi] * Math.PI / 180.0;
            (double sn, double cs) = DetMath.SinCos(va);
            var vPf = new Complex(ctx.Pf.Vm[bi] * cs, ctx.Pf.Vm[bi] * sn);
            double ang0 = buses.AngleRad[i][0], mag0 = buses.MagnitudePu[i][0];
            (double sn0, double cs0) = DetMath.SinCos(ang0);
            double dr = mag0 * cs0 - vPf.Real, di = mag0 * sn0 - vPf.Imaginary;
            double d = Math.Sqrt(dr * dr + di * di);
            if (d > vRes || i == 0) { vRes = d; vBus = buses.BusIds[i]; }
        }

        // Initial accelerating-power residual from the first step, and the drift.
        var traces = flat.Result.Machines;
        double accel = 0.0, wDrift = 0.0, aDrift = 0.0;
        int refSlot = ctx.RefSlot;
        for (int s = 0; s < real; s++)
        {
            var w = traces[s].SpeedPu;
            if (w.Count > 1) accel = Math.Max(accel, 2.0 * p.H[s] * Math.Abs(w[1] - w[0]) / h);
            double rel0 = traces[s].DeltaRad[0] - traces[refSlot].DeltaRad[0];
            for (int k = 0; k < w.Count; k++)
            {
                wDrift = Math.Max(wDrift, Math.Abs(w[k]));
                aDrift = Math.Max(aDrift, Math.Abs(traces[s].DeltaRad[k] - traces[refSlot].DeltaRad[k] - rel0));
            }
        }
        double vDrift = 0.0;
        for (int i = 0; i < buses.BusIds.Count; i++)
            for (int k = 0; k < buses.MagnitudePu[i].Length; k++)
                vDrift = Math.Max(vDrift, Math.Abs(buses.MagnitudePu[i][k] - buses.MagnitudePu[i][0]));

        double vLim = kappa * eps, accelLim = kappa * eps;
        double wLim = kappa * eps * T / (2.0 * hMin);
        double aLim = ws * kappa * eps * T * T / (2.0 * hMin);
        double vdLim = kappa * eps + aLim;
        var exceeded = new List<string>();
        void Check(string what, double v, double lim) { if (!(v <= lim)) exceeded.Add($"{what} {v.ToString("R", CultureInfo.InvariantCulture)} above {lim.ToString("R", CultureInfo.InvariantCulture)}"); }
        Check("initial voltage residual (pu)", vRes, vLim);
        Check("initial accelerating-power residual (pu)", accel, accelLim);
        Check("speed drift (pu)", wDrift, wLim);
        Check("relative rotor angle drift (rad)", aDrift, aLim);
        Check("bus voltage magnitude drift (pu)", vDrift, vdLim);
        if (!flat.Result.StayedInSynchronism) exceeded.Add("the no-event run lost synchronism");
        if (flat.Result.ConverterIterationFailures > 0) exceeded.Add("converter iteration failures in the no-event run");
        string just = $"eps = max(Newton tolerance {tol.ToString("R", CultureInfo.InvariantCulture)}, achieved mismatch {ctx.Pf.MaxMismatch.ToString("R", CultureInfo.InvariantCulture)}) pu; "
            + $"kappa = {kappa.ToString("R", CultureInfo.InvariantCulture)}; H_min = {hMin.ToString("R", CultureInfo.InvariantCulture)} s (system base); T = {T.ToString("R", CultureInfo.InvariantCulture)} s; "
            + "initial voltage residual <= kappa eps (driving-point admittance at least 1 pu); accelerating power <= kappa eps; "
            + "speed <= kappa eps T / (2 H_min); relative angle <= omega_s kappa eps T^2 / (2 H_min) (no synchronising torque assumed, so conservative); "
            + "voltage magnitude drift <= kappa eps + the angle bound (at most 1 pu per rad).";
        return new DynamicsEquilibriumEvidence(T, h, eps, kappa, hMin, vRes, vLim, vBus, accel, accelLim, wDrift, wLim, aDrift, aLim,
            vDrift, vdLim, flat.Result.StayedInSynchronism, exceeded.Count == 0, exceeded, just);
    }

    /// <summary>The first step k with k h + h 1e-6 at or after t: the kernel's stage rule.</summary>
    public static int AppliedStep(double atSeconds, double stepSeconds)
    {
        double eps = stepSeconds * MultiMachineSubtransient.StageTimeToleranceSteps;
        int k = (int)Math.Ceiling((atSeconds - eps) / stepSeconds);
        if (k < 0) k = 0;
        while (k * stepSeconds + eps < atSeconds) k++;
        while (k > 0 && (k - 1) * stepSeconds + eps >= atSeconds) k--;
        return k;
    }

    private static IReadOnlyList<DynamicsAppliedEvent> Applied(IReadOnlyList<ResolvedEvent> events, double h)
    {
        var order = events.Select((e, i) => (e, i)).OrderBy(x => x.e.Request.AtSeconds).ToList();
        var result = new DynamicsAppliedEvent[events.Count];
        int group = -1; double last = double.NaN;
        for (int o = 0; o < order.Count; o++)
        {
            (ResolvedEvent e, int i) = order[o];
            if (!(e.Request.AtSeconds == last)) { group++; last = e.Request.AtSeconds; }
            int k = AppliedStep(e.Request.AtSeconds, h);
            double applied = k * h;
            double shift = applied - e.Request.AtSeconds;
            result[i] = new DynamicsAppliedEvent(i, o, group, KindName(e.Request.Kind), e.Equipment, e.Engine.Describe(),
                e.Request.AtSeconds, applied, k, Math.Abs(shift) <= h * MultiMachineSubtransient.StageTimeToleranceSteps, shift);
        }
        return result;
    }

    private static string KindName(DynamicsEventKind k) => k switch
    {
        DynamicsEventKind.BusFault => "busFault",
        DynamicsEventKind.FaultClear => "faultClear",
        DynamicsEventKind.BranchTrip => "branchTrip",
        DynamicsEventKind.BranchReclose => "branchReclose",
        DynamicsEventKind.MachineTrip => "machineTrip",
        DynamicsEventKind.SequenceFault => "sequenceFault",
        DynamicsEventKind.LineFault => "lineFault",
        DynamicsEventKind.LineFaultClear => "lineFaultClear",
        _ => "loadStep",
    };

    private static DynamicsCaseResult EventRow(Context ctx, int index, DynamicsCaseRequest c, IReadOnlyList<ResolvedEvent> events)
    {
        // WP21 (RC5): the opt-in step schedule or error-controlled step (absent on every existing request: the body below).
        if (ctx.Request.StepSchedule is not null || ctx.Request.ErrorControl is not null) return SteppedEventRow(ctx, index, c, events);
        IReadOnlyList<DynamicsAppliedEvent> applied = Applied(events, ctx.Request.StepSeconds);
        RmsTransientStability.ObservedStudy run;
        try
        {
            run = RmsTransientStability.StudyEventsObserved(ctx.Model, ctx.Pf, ctx.Data, events.Select(e => e.Engine).ToList(), ctx.ChannelBuses,
                stepSeconds: ctx.Request.StepSeconds, durationSeconds: ctx.Request.DurationSeconds,
                slipLimitRad: ctx.Request.Criteria.SlipLimitRad, exciters: Exciters(ctx));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new DynamicsCaseResult(index, c.Name, c.Kind, DynamicsDisposition.Failed, "the engine refused the row: " + ex.Message,
                ctx.Indicative, applied, null, null, null);
        }
        return JudgeEventRow(ctx, index, c, events, applied, run, null);
    }

    /// <summary>WP21 (RC5): an events row on the request's step schedule or with the error-controlled step.</summary>
    private static DynamicsCaseResult SteppedEventRow(Context ctx, int index, DynamicsCaseRequest c, IReadOnlyList<ResolvedEvent> events)
    {
        DynamicsStudyRequest rq = ctx.Request;
        var engineEvents = events.Select(e => e.Engine).ToList();
        IReadOnlyList<DynamicsAppliedEvent>? applied = rq.StepSchedule is { } given ? AppliedScheduled(events, given, rq.DurationSeconds) : null;
        RmsTransientStability.ObservedStudy run;
        DynamicsStepControlEvidence evidence;
        RmsErrorControlResult<RmsTransientStability.ObservedStudy>? controlled = null;
        try
        {
            if (rq.StepSchedule is { } schedule)
            {
                run = RmsTransientStability.StudyEventsObservedScheduled(ctx.Model, ctx.Pf, ctx.Data, engineEvents, ctx.ChannelBuses, schedule,
                    durationSeconds: rq.DurationSeconds, slipLimitRad: rq.Criteria.SlipLimitRad, exciters: Exciters(ctx));
                evidence = new DynamicsStepControlEvidence("schedule",
                    schedule.Windows.Select(w => new DynamicsStepWindow(w.FromSeconds, w.StepSeconds, null)).ToList(),
                    run.Result.Machines[0].TimeSeconds.Count - 1, null, null, null, null, null);
            }
            else
            {
                controlled = RmsTransientStability.StudyEventsObservedErrorControlled(ctx.Model, ctx.Pf, ctx.Data, engineEvents, ctx.ChannelBuses,
                    rq.ErrorControl!, () => Exciters(ctx), durationSeconds: rq.DurationSeconds, slipLimitRad: rq.Criteria.SlipLimitRad);
                run = controlled.Run;
                applied = AppliedScheduled(events, controlled.Schedule, rq.DurationSeconds);
                evidence = new DynamicsStepControlEvidence("errorControl",
                    controlled.Segments.Select(g => new DynamicsStepWindow(g.FromSeconds, g.StepSeconds, g.ScaledError)).ToList(),
                    run.Result.Machines[0].TimeSeconds.Count - 1, controlled.ToleranceMet, controlled.MaxScaledError, controlled.Passes,
                    controlled.Runs, controlled.Note) { Status = RmsErrorControl.ExperimentalNote };
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new DynamicsCaseResult(index, c.Name, c.Kind, DynamicsDisposition.Failed, "the engine refused the row: " + ex.Message,
                ctx.Indicative, applied, null, null, null);
        }
        DynamicsCaseResult row = JudgeEventRow(ctx, index, c, events, applied, run, evidence);
        if (controlled is { ToleranceMet: false })
        {
            // A run whose step control missed its tolerance is not a settled answer. A loss of synchronism seen in both runs of
            // the last pass stands (the difference then grows without bound); anything else is INCOMPLETE.
            bool bothLost = !run.Result.StayedInSynchronism && controlled.CoarseRun is { } coarse && !coarse.Result.StayedInSynchronism;
            if (row.Disposition == DynamicsDisposition.Unstable && bothLost)
                row = row with { Reason = row.Reason + "; " + controlled.Note + " (the loss of synchronism is in both runs of the last pass)" };
            else if (row.Disposition is DynamicsDisposition.Stable or DynamicsDisposition.Unstable)
                row = row with
                {
                    Disposition = DynamicsDisposition.Incomplete,
                    Reason = "the error-controlled step did not settle: " + controlled.Note + (row.Reason is null ? "" : "; the run itself reads: " + row.Reason),
                };
        }
        return row;
    }

    /// <summary>WP21: the applied events of a step-schedule run (the kernel's stage rule, on each step's own length).</summary>
    private static IReadOnlyList<DynamicsAppliedEvent> AppliedScheduled(IReadOnlyList<ResolvedEvent> events, RmsStepSchedule schedule, double duration)
    {
        (double[] times, double[] steps) = schedule.Expand(duration);
        var order = events.Select((e, i) => (e, i)).OrderBy(x => x.e.Request.AtSeconds).ToList();
        var result = new DynamicsAppliedEvent[events.Count];
        int group = -1; double last = double.NaN;
        for (int o = 0; o < order.Count; o++)
        {
            (ResolvedEvent e, int i) = order[o];
            if (!(e.Request.AtSeconds == last)) { group++; last = e.Request.AtSeconds; }
            (int k, double applied) = RmsStepSchedule.AppliedAt(times, steps, e.Request.AtSeconds);
            double shift = applied - e.Request.AtSeconds;
            double dt = steps[Math.Min(k, steps.Length - 1)];
            result[i] = new DynamicsAppliedEvent(i, o, group, KindName(e.Request.Kind), e.Equipment, e.Engine.Describe(),
                e.Request.AtSeconds, applied, k, Math.Abs(shift) <= dt * MultiMachineSubtransient.StageTimeToleranceSteps, shift);
        }
        return result;
    }

    /// <summary>The judgement of an events row from its run (the body of <see cref="EventRow"/>, moved here unchanged,
    /// orchestration only), with the WP21 step evidence (null on the plain path).</summary>
    private static DynamicsCaseResult JudgeEventRow(Context ctx, int index, DynamicsCaseRequest c, IReadOnlyList<ResolvedEvent> events,
        IReadOnlyList<DynamicsAppliedEvent>? applied, RmsTransientStability.ObservedStudy run, DynamicsStepControlEvidence? stepControl)
    {
        // Angle spread over the connected real machines (a tripped machine is left out of the spread from the start).
        var tripped = new HashSet<int>(events.Where(e => e.TrippedSlotGenerator is int).Select(e => ctx.SlotOfGenerator[e.TrippedSlotGenerator!.Value]));
        SubtransientInit.Prepared p = run.Prepared;
        int real = p.Machines.Count - (p.IslandReferences?.Count ?? 0);
        var traces = run.Result.Machines;
        IReadOnlyList<double> time = traces[0].TimeSeconds;
        double spread = 0.0, spreadAt = 0.0;
        for (int k = 0; k < time.Count; k++)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int s = 0; s < real; s++)
            {
                if (tripped.Contains(s)) continue;
                double d = traces[s].DeltaRad[k];
                if (!double.IsFinite(d)) { lo = double.NegativeInfinity; hi = double.PositiveInfinity; break; }
                lo = Math.Min(lo, d); hi = Math.Max(hi, d);
            }
            double sp = (hi - lo) * 180.0 / Math.PI;
            if (!(sp <= spread)) { spread = sp; spreadAt = time[k]; }
        }
        List<DynamicsChannelTrace> channels = Channels(ctx, run);
        var evidence = new DynamicsRunEvidence(run.Result.StayedInSynchronism, spread, spreadAt, run.Result.ConverterIterationFailures,
            time.Count, time, channels);
        DynamicsPostEventTopology post = PostEvent(ctx, events);

        string disposition; string? reason;
        if (run.Result.ConverterIterationFailures > 0)
        { disposition = DynamicsDisposition.Incomplete; reason = $"the converter current iteration did not settle on {run.Result.ConverterIterationFailures} step(s)"; }
        else if (!run.Result.StayedInSynchronism)
        { disposition = DynamicsDisposition.Unstable; reason = $"loss of synchronism: a machine left the {ctx.Request.Criteria.SlipLimitRad.ToString("R", CultureInfo.InvariantCulture)} rad centre-of-inertia band"; }
        else if (!(spread <= ctx.Request.Criteria.MaxAngleSpreadDeg))
        { disposition = DynamicsDisposition.Unstable; reason = $"rotor angle spread {spread.ToString("R", CultureInfo.InvariantCulture)} deg at {spreadAt.ToString("R", CultureInfo.InvariantCulture)} s exceeds {ctx.Request.Criteria.MaxAngleSpreadDeg.ToString("R", CultureInfo.InvariantCulture)} deg"; }
        else { disposition = DynamicsDisposition.Stable; reason = null; }
        // CLI-27: a pass cannot be claimed for a network whose post-event steady state could not be established. The
        // post-event power flow ran and did not converge (it hit the iteration cap or went non-finite): the row is
        // INCOMPLETE with that stated. An islanded post-event network is NOT a failure by itself (clearing by islanding can
        // be a legitimate outcome): its "NOT SOLVED (islanded)" stays information only. Instability, a converter failure
        // or an earlier INCOMPLETE keep their own, stronger, disposition.
        if (disposition == DynamicsDisposition.Stable && PostEventNotConverged(post))
        {
            disposition = DynamicsDisposition.Incomplete;
            reason = $"the post-event network's power flow did not converge ({post.PowerFlowStatus}); its steady state could not be established, so stability is not claimed";
        }
        return new DynamicsCaseResult(index, c.Name, c.Kind, disposition, reason, ctx.Indicative, applied, evidence, post, null) { StepControl = stepControl };
    }

    /// <summary>CLI-27: true when the post-event check ran a power flow and it did not converge. The intact-network answer
    /// ("Converged (...)") and the islanded answer ("NOT SOLVED (islanded)", information only) are not non-convergence.</summary>
    internal static bool PostEventNotConverged(DynamicsPostEventTopology post) =>
        !post.PowerFlowStatus.StartsWith("Converged", StringComparison.Ordinal)
        && !string.Equals(post.PowerFlowStatus, "NOT SOLVED (islanded)", StringComparison.Ordinal);

    private static List<DynamicsChannelTrace> Channels(Context ctx, RmsTransientStability.ObservedStudy run)
    {
        var list = new List<DynamicsChannelTrace>();
        var traces = run.Result.Machines;
        foreach (var (ch, slot, label) in ctx.Channels)
        {
            switch (ch.Kind)
            {
                case DynamicsChannelKind.RotorAngle:
                {
                    var d = traces[slot!.Value].DeltaRad; var r = traces[ctx.RefSlot].DeltaRad;
                    var v = new double[d.Count];
                    for (int k = 0; k < v.Length; k++) v[k] = (d[k] - r[k]) * 180.0 / Math.PI;
                    list.Add(new DynamicsChannelTrace(label, "rotorAngle", "rotor angle relative to the reference machine", "deg", v));
                    break;
                }
                case DynamicsChannelKind.Speed:
                    list.Add(new DynamicsChannelTrace(label, "speed", "rotor speed deviation", "pu", traces[slot!.Value].SpeedPu.ToArray()));
                    break;
                case DynamicsChannelKind.Frequency:
                {
                    var w = traces[slot!.Value].SpeedPu;
                    var v = new double[w.Count];
                    for (int k = 0; k < v.Length; k++) v[k] = ctx.NominalHz * (1.0 + w[k]);
                    list.Add(new DynamicsChannelTrace(label, "frequency", "rotor electrical frequency", "Hz", v));
                    break;
                }
                default:
                {
                    int i = -1;
                    for (int b = 0; b < run.Buses.BusIds.Count; b++) if (run.Buses.BusIds[b] == ch.Bus) { i = b; break; }
                    list.Add(new DynamicsChannelTrace(label, "busVoltage", "voltage magnitude", "pu", run.Buses.MagnitudePu[i]));
                    var a = run.Buses.AngleRad[i];
                    var deg = new double[a.Length];
                    for (int k = 0; k < deg.Length; k++) deg[k] = a[k] * 180.0 / Math.PI;
                    list.Add(new DynamicsChannelTrace(label, "busVoltage", "voltage angle (engine synchronous frame)", "deg", deg));
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>The post-event network: the branches still out after every event, checked by a power flow of its own.</summary>
    private static DynamicsPostEventTopology PostEvent(Context ctx, IReadOnlyList<ResolvedEvent> events)
    {
        var outSet = new List<int>();
        foreach (ResolvedEvent e in events.OrderBy(x => x.Request.AtSeconds))
        {
            if (e.Engine is RmsBranchTrip t) outSet.Add(t.BranchIndex);
            else if (e.Engine is RmsBranchReclose rc) outSet.Remove(rc.BranchIndex);
        }
        string note = events.Any(e => e.Engine is RmsLoadStep or RmsGeneratorTrip)
            ? "load steps and machine trips are not part of this check: it is the post-event branch topology at the initial dispatch"
            : "the post-event branch topology at the initial dispatch";
        GridModel model = ctx.Model;
        var labels = outSet.Select(j => $"{model.Branches[j].From}-{model.Branches[j].To} (index {j.ToString(CultureInfo.InvariantCulture)})").ToList();
        var branches = model.Branches.Select((b, j) => outSet.Contains(j) ? b with { InService = false } : b).ToList();
        int islands = Islands(model, branches);
        if (outSet.Count == 0) return new DynamicsPostEventTopology(labels, islands, "Converged (intact network: the initial solve)", ctx.Pf.MaxMismatch, note);
        if (islands > 1) return new DynamicsPostEventTopology(labels, islands, "NOT SOLVED (islanded)", null, note);
        GridModel post = model.CopyWith(branches: branches);
        PowerFlowResult pf = SolverSelector.CreateNewton(post).Solve(post, new PowerFlowOptions
        { Tolerance = ctx.Request.Initialisation.PowerFlowTolerancePu, MaxIterations = ctx.Request.Initialisation.PowerFlowMaxIterations });
        return new DynamicsPostEventTopology(labels, islands, pf.Status.ToString(), pf.MaxMismatch, note);
    }

    private static int Islands(GridModel model, IReadOnlyList<Branch> branches)
    {
        var index = new Dictionary<int, int>();
        foreach (Bus b in model.Buses) index[b.Id] = index.Count;
        var parent = Enumerable.Range(0, index.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (Branch br in branches)
            if (br.InService && index.TryGetValue(br.From, out int a) && index.TryGetValue(br.To, out int b)) parent[Find(a)] = Find(b);
        return Enumerable.Range(0, index.Count).Select(Find).Distinct().Count();
    }

    private static DynamicsCaseResult CctRow(Context ctx, int index, DynamicsCaseRequest c, IReadOnlyList<(int Branch, string Label)> trips)
    {
        DynamicsCctRequest q = c.Cct!;
        DynamicsCctBracket? coarse, refined = null;
        try
        {
            coarse = Bracket(ctx, q, trips, ctx.Request.StepSeconds);
            if (q.RefinedStepSeconds is double rs) refined = Bracket(ctx, q, trips, rs);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new DynamicsCaseResult(index, c.Name, c.Kind, DynamicsDisposition.Failed, "the engine refused the row: " + ex.Message,
                ctx.Indicative, null, null, null, null);
        }
        double? stableShift = refined is null ? null : refined.StableSeconds - coarse.StableSeconds;
        double? unstableShift = refined?.UnstableSeconds is double ru && coarse.UnstableSeconds is double cu ? ru - cu : null;
        var ev = new DynamicsCctEvidence(q.FaultBus, trips.Select(t => t.Label).ToList(), coarse, refined, stableShift, unstableShift,
            "the shifts are this engine's own timestep sensitivity (refined minus coarse); no cross-tool comparison is made");

        string disposition; string? reason = null;
        DynamicsCctBracket[] all = refined is null ? new[] { coarse } : new[] { coarse, refined };
        if (all.Any(b => b.TrialsWithConverterIterationFailures > 0 || b.StableEndpointVerified == false || b.UnstableEndpointVerified == false))
        { disposition = DynamicsDisposition.Incomplete; reason = "an endpoint did not re-verify or a trial had unsettled converter steps"; }
        else if (coarse.UnstableAtZero) disposition = DynamicsDisposition.UnstableAtZero;
        else if (coarse.UnstableSteps is null) disposition = DynamicsDisposition.StableThroughWindow;
        else if (all.Any(b => b.NonMonotone)) { disposition = DynamicsDisposition.CctNonMonotone; reason = "a stable pocket was seen above the first loss; no global stability claim is made"; }
        else disposition = DynamicsDisposition.CctBracketed;
        return new DynamicsCaseResult(index, c.Name, c.Kind, disposition, reason, ctx.Indicative, null, null, null, ev);
    }

    private static DynamicsCctBracket Bracket(Context ctx, DynamicsCctRequest q, IReadOnlyList<(int Branch, string Label)> trips, double step)
    {
        var actions = trips.Select(t => (RmsEvent)new RmsBranchTrip(0.0, t.Branch)).ToList();
        CctBracket b = RmsTransientStability.CriticalClearingBracketEvents(ctx.Model, ctx.Pf, ctx.Data, q.FaultBus, actions,
            stepSeconds: step, maxSeconds: q.MaxClearingSeconds, iterations: q.Iterations, onsetSeconds: q.OnsetSeconds,
            slipLimitRad: ctx.Request.Criteria.SlipLimitRad, exciters: Exciters(ctx), scanSteps: q.ScanSteps);

        // Re-run both endpoints exactly as a search trial runs them (the same events, timing and duration rule).
        bool? Trial(int steps)
        {
            double at = q.OnsetSeconds + steps * step;
            var events = new List<RmsEvent> { new RmsBusFault(q.OnsetSeconds, q.FaultBus), new RmsFaultClear(at, q.FaultBus) };
            foreach (RmsEvent a in actions) events.Add(a with { AtSeconds = at });
            MultiMachineSubtransient.Result r = RmsTransientStability.StudyEvents(ctx.Model, ctx.Pf, ctx.Data, events, null, step,
                at + RmsTransientStability.PostClearWindowSeconds, ctx.Request.Criteria.SlipLimitRad, Exciters(ctx));
            return r.StayedInSynchronism;
        }
        bool? stableOk = b.UnstableAtZero ? null : Trial(b.StableSteps);
        bool? unstableOk = b.UnstableAtZero || b.UnstableSteps is not int us ? null : !Trial(us);
        return new DynamicsCctBracket(step, q.OnsetSeconds, q.MaxClearingSeconds, RmsTransientStability.PostClearWindowSeconds,
            q.Iterations, q.ScanSteps, $"synchronism (slip limit {ctx.Request.Criteria.SlipLimitRad.ToString("R", CultureInfo.InvariantCulture)} rad from the centre of inertia)",
            b.StableSteps, b.UnstableSteps, b.StableSeconds, b.UnstableSeconds, b.UnstableAtZero, b.AtStepResolution, b.NonMonotone,
            b.PocketStableSeconds, b.Trials, b.TrialsWithConverterIterationFailures, stableOk, unstableOk);
    }

    private static DynamicsStudyResult Stopped(DynamicsStudyRequest request, DynamicsModelCoverageReport coverage,
        DynamicsEngineDeclaration engine, DynamicsInitialisationEvidence? init, string reason)
    {
        var rows = request.Cases.Select((c, i) => new DynamicsCaseResult(i, c.Name, c.Kind, DynamicsDisposition.NotRun, reason,
            !coverage.LikeForLike, null, null, null, null)).ToList();
        var reasons = new List<string> { reason };
        reasons.AddRange(coverage.BlockingReasons);
        return new DynamicsStudyResult(DynamicsDisposition.Incomplete, false, reasons, coverage, engine, init, rows,
            new DynamicsCaseCounts(rows.Count, 0, 0, 0, rows.Count, 0));
    }

    private static DynamicsStudyResult Aggregate(DynamicsStudyRequest request, DynamicsModelCoverageReport coverage,
        DynamicsEngineDeclaration engine, DynamicsInitialisationEvidence init, IReadOnlyList<DynamicsCaseResult> rows)
    {
        int notAttempted = rows.Count(r => r.Disposition == DynamicsDisposition.NotAttempted);
        int failed = rows.Count(r => r.Disposition == DynamicsDisposition.Failed);
        int incomplete = rows.Count(r => r.Disposition == DynamicsDisposition.Incomplete);
        int attempted = rows.Count - notAttempted;
        var counts = new DynamicsCaseCounts(rows.Count, attempted, attempted - failed, failed, 0, notAttempted);
        var reasons = new List<string>();
        if (!coverage.LikeForLike)
        {
            reasons.Add("the model coverage is BLOCKED: rows were simulated with unmodelled dynamics left out, so every disposition is indicative and no like-for-like verdict is given");
            reasons.AddRange(coverage.BlockingReasons);
        }
        if (notAttempted > 0) reasons.Add($"{notAttempted} row(s) not attempted (cancelled): " + string.Join(", ", rows.Where(r => r.Disposition == DynamicsDisposition.NotAttempted).Select(r => $"'{r.Name}'")));
        if (failed > 0) reasons.Add($"{failed} row(s) failed: " + string.Join(", ", rows.Where(r => r.Disposition == DynamicsDisposition.Failed).Select(r => $"'{r.Name}'")));
        if (incomplete > 0) reasons.Add($"{incomplete} row(s) incomplete: " + string.Join(", ", rows.Where(r => r.Disposition == DynamicsDisposition.Incomplete).Select(r => $"'{r.Name}'")));
        int unstable = rows.Count(r => r.Disposition == DynamicsDisposition.Unstable);
        string status;
        if (!coverage.LikeForLike || notAttempted > 0 || failed > 0 || incomplete > 0) status = DynamicsDisposition.Incomplete;
        else if (unstable > 0) { status = DynamicsDisposition.Unstable; reasons.Add($"{unstable} row(s) unstable: " + string.Join(", ", rows.Where(r => r.Disposition == DynamicsDisposition.Unstable).Select(r => $"'{r.Name}'"))); }
        else status = rows.Any(r => r.Kind == DynamicsCaseKind.Events) ? DynamicsDisposition.Stable : DynamicsDisposition.Evaluated;
        return new DynamicsStudyResult(status, coverage.LikeForLike, reasons, coverage, engine, init, rows, counts);
    }
}
