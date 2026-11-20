using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Analysis.Reliability.Network;

/// <summary>A customer-bearing bus: <see cref="Customers"/> customers and an annual mean demand. A null
/// <see cref="AverageLoadMw"/> reads the bus's Pd.</summary>
public sealed record NetworkLoadPoint(string Id, int Bus, int Customers, double? AverageLoadMw);

/// <summary>A disconnector or switch on a branch (branch by index plus From and To). Opening it makes its branch a fully
/// isolable section (a switch at both ends is the assumption; see the scope list).</summary>
public sealed record SwitchPoint(string Id, int BranchIndex, int From, int To);

/// <summary>A normally-open tie: a branch stored out of service that can be closed to back-feed a de-energised section.</summary>
public sealed record TiePoint(string Id, int BranchIndex, int From, int To);

/// <summary>AC verification of every restored network by the incumbent restoration enumerator. When disabled, restoration is
/// decided by connectivity alone and every event says the AC state was NOT RUN.</summary>
public sealed record AcVerificationOptions(bool Enabled, double MinPu = 0.9, double MaxPu = 1.1);

public enum NetworkMethod { Enumeration, MonteCarlo }

/// <summary>Monte Carlo controls (R4-02 streams, seed mandatory). <see cref="RepairDistribution"/> is
/// <c>exponential</c> (mean = the repair time, the Markov model) or <c>deterministic</c>.</summary>
public sealed record NetworkMonteCarlo(ulong? Seed, long Years, string RepairDistribution = "exponential", string Domain = "reliability-network", int Threads = 0);

/// <summary>The network-joined reliability and restoration study (RC4 R4-13).</summary>
public sealed record NetworkReliabilityRequest(
    GridModel Model,
    BoundGroups Groups,
    IReadOnlyList<FailureGroupData> FailureData,
    IReadOnlyList<FailureOverride> Overrides,
    IReadOnlyList<PlacedDevice> Devices,
    IReadOnlyList<SwitchPoint> Switches,
    IReadOnlyList<TiePoint> Ties,
    IReadOnlyList<NetworkLoadPoint> LoadPoints,
    double? SwitchingHours,
    double HoursPerYear,
    AcVerificationOptions Ac,
    NetworkMethod Method = NetworkMethod.Enumeration,
    NetworkMonteCarlo? MonteCarlo = null,
    int Order = 1);

/// <summary>One restoration step of an event: the action, and the AC verifier's verdict on the network after it.</summary>
public sealed record RestorationPathStep(int Step, string Action, int Branch, string Disposition, int Violations);

/// <summary>What happened to one interrupted load point in one event. <see cref="Restoration"/> is <c>Switching</c>
/// (the source re-closed after isolation), <c>BackFeed</c> (a tie closed; <see cref="Tie"/> names it) or <c>Repair</c>
/// (it waits for the repair; <see cref="Reason"/> says why it could not be restored sooner).</summary>
public sealed record NetworkLoadOutcome(string LoadPoint, string Restoration, double? DurationHours, string? Tie, string? Reason);

/// <summary>One outage event: failure of one branch, in model order.</summary>
public sealed record NetworkOutageEvent(string Component, int Branch, string? Name, string Group, double? FailuresPerYear,
    double? RepairHours, double? SwitchingHours, string ClearedBy, int ClearingBranch, string? IsolatingSwitch,
    IReadOnlyList<int> OpenedForIsolation, IReadOnlyList<int> InterruptedBuses, IReadOnlyList<NetworkLoadOutcome> Loads,
    IReadOnlyList<RestorationPathStep> RestorationPath, string AcVerification, string? AcNote);

/// <summary>Monte Carlo accounting: the R4-02 provenance, the standard error of each system index estimate.</summary>
public sealed record IndexStandardError(string Name, double? StandardError);

public sealed record NetworkMonteCarloSummary(ulong Seed, long Years, string RepairDistribution, string GeneratorVersion,
    IReadOnlyList<string> Domains, string ResultDigest, IReadOnlyList<IndexStandardError> StandardErrors);

/// <summary>
/// The result. <see cref="Status"/>: COMPLETE, INCOMPLETE (rate data missing: computable values still reported, the
/// withheld ones null) or OUT OF SCOPE (<see cref="Reason"/>; no index is produced). Load-point and system indices use the
/// incumbent <see cref="ReliabilityIndex"/> and <see cref="LoadPointReliability"/> shapes.
/// </summary>
public sealed record NetworkReliabilityResult(string Status, string? Reason, string Method,
    IReadOnlyList<ReliabilityIndex> SystemIndices, IReadOnlyList<LoadPointReliability> LoadPoints,
    IReadOnlyList<MissingRateData> Missing, IReadOnlyList<NetworkOutageEvent> Events, double TotalCustomers,
    string BaseNetworkAc, NetworkMonteCarloSummary? MonteCarlo, IReadOnlyList<string> Scope);
