using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Analysis.Reliability.Network;

/// <summary>
/// RC4 R4-13: reliability and restoration joined on the real network. Failure data bound to the R4-01 groups, the R4-07
/// placed devices and explicit switches, normally-open ties, customers and loads on the case's buses; each component fails
/// in model order, is cleared and isolated by the placed devices, and the de-energised loads are restored by source
/// re-closure or by closing a tie, every restored network verified by the incumbent restoration enumerator
/// (<see cref="Restoration.RestorationStudy"/>) when AC verification is enabled. The RC3 <see cref="ReliabilityStudy"/> is
/// not edited and is not called; the figures are reproduced bit for bit on a network that has no back-feed (a test).
/// </summary>
public static class NetworkReliabilityStudy
{
    public const string FormatVersion = "gridsim-reliability-network/1";

    public static readonly IReadOnlyList<string> StepOrder =
    [
        "ValidateRequest", "BuildSupplyTree", "VerifyIntactNetwork", "BindFailureData", "OutageEvents",
        "IsolateWithPlacedDevices", "IdentifyUnsuppliedLoads", "RestorationAlternatives", "AcVerify", "Accumulate", "Indices"
    ];

    public static readonly IReadOnlyList<string> Scope =
    [
        "Radial in-service network with one source (the slack bus); normally-open ties are branches declared as ties (the study opens them).",
        "Sustained interruptions only; momentary interruptions and reclosing successes are not modelled.",
        "Independent failures, first-order approximation: one component fails at a time, overlapping outages are neglected (second-order enumeration is deferred).",
        "A placed device clears a fault on its own branch and anything below it; the nearest device on the path to the source operates (perfect, selective protection, no stuck devices).",
        "A breaker or recloser placed on a branch, and a declared switch, can open that branch; the branch is then a section isolated at both ends. Fuses and relays clear but are not used to isolate.",
        "One switching time per event: every load restored by source re-closure or by a tie waits that time; the AC verifier orders the steps but its step clock is not reported as the duration.",
        "Back-feed closes at most one tie per de-energised section, the first in model order whose restored network passes the AC verifier; loads of a failed section with no isolating switch wait for the repair.",
        "AC verification is a snapshot at the case's loading (voltage band and RATE_A at the stated limits); the average load used for ENS is the declared annual mean.",
        "Equipment other than the listed groups' in-service branches (sources, buses, busbar faults, protection failure modes, weather-dependent rates) does not fail in this model."
    ];

    public static NetworkReliabilityResult Run(NetworkReliabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string method = request.Method == NetworkMethod.MonteCarlo ? "monte-carlo" : "enumeration";
        Validate(request);
        if (request.Order != 1)
            return OutOfScope(method, "Second-order (overlapping outage) enumeration is deferred; only order 1 is supported.", "NOT RUN");

        GridModel model = OpenTies(request);
        FeederTree tree;
        try { tree = FeederTree.Build(model); }
        catch (NetworkScopeException ex) { return OutOfScope(method, ex.Message, "NOT RUN"); }

        // Load points on the case.
        var seenLoads = new HashSet<string>(StringComparer.Ordinal);
        var loads = new List<ResolvedLoadPoint>();
        foreach (NetworkLoadPoint lp in request.LoadPoints)
        {
            if (lp is null || string.IsNullOrWhiteSpace(lp.Id)) throw new ArgumentException("Every load point needs an id.");
            if (!seenLoads.Add(lp.Id)) throw new ArgumentException($"Load point id '{lp.Id}' is duplicated.");
            if (!tree.BusPos.TryGetValue(lp.Bus, out int pos)) throw new ArgumentException($"Load point '{lp.Id}': bus {lp.Bus} is not in the case.");
            if (!tree.Reached[pos]) throw new ArgumentException($"Load point '{lp.Id}': bus {lp.Bus} is not supplied from the source in the intact network.");
            if (lp.Customers < 0) throw new ArgumentException($"Load point '{lp.Id}': customers must be non-negative.");
            double avg = lp.AverageLoadMw ?? model.Buses[pos].Pd;
            if (!double.IsFinite(avg) || avg < 0) throw new ArgumentException($"Load point '{lp.Id}': averageLoadMw must be finite and non-negative.");
            loads.Add(new ResolvedLoadPoint(lp.Id, lp.Bus, pos, lp.Customers, avg));
        }
        if (loads.Count == 0) throw new ArgumentException("At least one load point is required.");
        if (loads.Sum(l => (long)l.Customers) <= 0) throw new ArgumentException("The load points must serve at least one customer.");

        // Devices, switches and ties on the case.
        var clearing = new HashSet<int>();
        var switching = new HashSet<int>();
        var deviceLabel = new Dictionary<int, string>();
        var switchLabel = new Dictionary<int, string>();
        var deviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlacedDevice d in request.Devices ?? [])
        {
            DeviceLocation loc = d.Location;
            CheckBranch($"Device '{d.Id}'", model, loc.BranchIndex, loc.From, loc.To);
            if (!deviceIds.Add(d.Id)) throw new ArgumentException($"Device id '{d.Id}' is duplicated.");
            if (!model.Branches[loc.BranchIndex].InService) continue;
            clearing.Add(loc.BranchIndex);
            deviceLabel.TryAdd(loc.BranchIndex, d.Id);
            if (d.Type is DeviceType.Breaker or DeviceType.Recloser) switching.Add(loc.BranchIndex);
        }
        var switchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SwitchPoint s in request.Switches ?? [])
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Id)) throw new ArgumentException("Every switch needs an id.");
            if (!switchIds.Add(s.Id)) throw new ArgumentException($"Switch id '{s.Id}' is duplicated.");
            CheckBranch($"Switch '{s.Id}'", model, s.BranchIndex, s.From, s.To);
            if (!model.Branches[s.BranchIndex].InService)
                throw new ArgumentException($"Switch '{s.Id}': branch {s.BranchIndex} is out of service; a switch isolates an in-service branch.");
            switching.Add(s.BranchIndex);
            switchLabel.TryAdd(s.BranchIndex, s.Id);
        }
        // The intact network must pass the AC verifier before restoration can be judged against it.
        string baseAc = "NOT RUN";
        if (request.Ac.Enabled)
        {
            string? envelope = TransmissionSecurityRunner.EnvelopeRefusal(model);
            if (envelope is not null) return OutOfScope(method, "AC verification is outside the verifier's envelope: " + envelope, "OUT OF SCOPE");
            SecurityState intact = TransmissionSecurityRunner.VerifyState(model, request.Ac.MinPu, request.Ac.MaxPu, PostFaultRating.RateA, null);
            baseAc = intact.Disposition;
            if (intact.Disposition != "SECURE")
                return OutOfScope(method, $"The intact network does not pass the AC verifier ({intact.Disposition}{(intact.Reason is null ? "" : ": " + intact.Reason)}); restoration cannot be judged against it. Fix the case or set acVerification.enabled to false.", baseAc);
        }

        IReadOnlyList<FailureComponent> components = FailureModel.Resolve(model, request.Groups, request.FailureData, request.Overrides);
        if (components.Count == 0) throw new ArgumentException("The failure-data groups contain no in-service branch.");
        var restoration = new ServiceRestoration(model, tree, clearing, switching, deviceLabel, switchLabel, request.Ties ?? [],
            loads.Select(l => l.BusPos).ToArray(), request.Ac);
        var plans = new List<EventPlan>(components.Count);
        foreach (FailureComponent c in components) plans.Add(restoration.Plan(c.Branch));
        var prepared = new PreparedNetworkStudy(components, plans, loads, request.SwitchingHours, request.HoursPerYear);

        (IReadOnlyList<LoadPointReliability> points, IReadOnlyList<MissingRateData> missing, IReadOnlyList<NetworkOutageEvent> events) = OutageEnumeration.Run(prepared);
        IReadOnlyList<ReliabilityIndex> indices = CustomerIndices.System(points, request.HoursPerYear, out double customers);
        bool complete = missing.Count == 0 && indices.All(i => i.Value is not null || i.Name == "CAIDI");

        if (request.Method == NetworkMethod.MonteCarlo)
        {
            NetworkMonteCarlo mc = request.MonteCarlo ?? throw new ArgumentException("method monte-carlo needs the monteCarlo settings (seed, years).");
            if (!complete)
                return new("INCOMPLETE", "Rate or time data is missing; the sampler needs every figure. The analytical load-point values that could be computed are reported.",
                    method, indices, points, missing, events, customers, baseAc, null, Scope);
            var run = NetworkMonteCarloRunner.Run(prepared, mc);
            return new("COMPLETE", null, method, run.Indices, run.Points, missing, events, run.Customers, baseAc, run.Summary, Scope);
        }
        return new(complete ? "COMPLETE" : "INCOMPLETE", null, method, indices, points, missing, events, customers, baseAc, null, Scope);
    }

    /// <summary>Declared ties are normally open: the study opens them (a case file may store them closed, and some loaders cannot
    /// carry an open branch). Identity is checked against the case as given.</summary>
    private static GridModel OpenTies(NetworkReliabilityRequest request)
    {
        GridModel model = request.Model;
        var tieIds = new HashSet<string>(StringComparer.Ordinal);
        var tieBranches = new HashSet<int>();
        foreach (TiePoint t in request.Ties ?? [])
        {
            if (t is null || string.IsNullOrWhiteSpace(t.Id)) throw new ArgumentException("Every tie needs an id.");
            if (!tieIds.Add(t.Id)) throw new ArgumentException($"Tie id '{t.Id}' is duplicated.");
            CheckBranch($"Tie '{t.Id}'", model, t.BranchIndex, t.From, t.To);
            if (!tieBranches.Add(t.BranchIndex)) throw new ArgumentException($"Tie branch {t.BranchIndex} is listed twice.");
        }
        if (tieBranches.Count == 0) return model;
        return model.CopyWith(branches: model.Branches.Select((b, i) => tieBranches.Contains(i) && b.InService ? b with { InService = false } : b).ToList());
    }

    private static NetworkReliabilityResult OutOfScope(string method, string reason, string baseAc) =>
        new("OUT OF SCOPE", reason, method, [], [], [], [], 0, baseAc, null, Scope);

    private static void CheckBranch(string who, GridModel model, int index, int from, int to)
    {
        if (index < 0 || index >= model.Branches.Count) throw new ArgumentException($"{who}: branch index {index} is outside the case.");
        Branch b = model.Branches[index];
        if (b.From != from || b.To != to)
            throw new ArgumentException($"{who}: branch {index} joins {b.From}-{b.To} in the case, not {from}-{to}.");
    }

    private static void Validate(NetworkReliabilityRequest r)
    {
        if (r.Model is null) throw new ArgumentException("A case is required.");
        if (r.Groups is null) throw new ArgumentException("A groups document is required (failure data attaches to groups).");
        if (r.LoadPoints is null || r.LoadPoints.Count == 0) throw new ArgumentException("At least one load point is required.");
        if (!double.IsFinite(r.HoursPerYear) || r.HoursPerYear <= 0) throw new ArgumentException("hoursPerYear must be finite and positive.");
        if (r.SwitchingHours is double s && (!double.IsFinite(s) || s < 0)) throw new ArgumentException("switchingHours must be finite and non-negative.");
        if (r.Ac is null) throw new ArgumentException("acVerification must be stated (enabled true with a voltage band, or false).");
        if (r.Ac.Enabled && (!double.IsFinite(r.Ac.MinPu) || !double.IsFinite(r.Ac.MaxPu) || r.Ac.MinPu <= 0 || r.Ac.MaxPu < r.Ac.MinPu))
            throw new ArgumentException("acVerification voltage bounds must be finite, positive and ordered.");
        if (r.Order < 1) throw new ArgumentException("order must be 1 or more.");
        if (r.Method == NetworkMethod.Enumeration && r.MonteCarlo is not null)
            throw new ArgumentException("monteCarlo settings belong to method monte-carlo.");
    }
}
