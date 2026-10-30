using GridSim.Core.Analysis;
using GridSim.Core.Assets;
using GridSim.Core.Estimation;
using GridSim.Core.Evidence;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.Physics;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using GridSim.Core.Storage;
using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS13 (R153-R157, R174-R177, R179, R180, R182, R186-R189, R192-R198, R201, R202, R214):
/// bare literals in the analysis, estimation, IO, numerics, physics, power-flow, protection, scenario, simulation,
/// storage and GPU code were named. The naming must not move a single number, so each test pins a new constant to
/// EXACTLY the literal it replaced (exact <c>Assert.Equal</c>), and source-level checks make sure the literal did not
/// survive next to its new name.
/// </summary>
public sealed class Ws13CoreConstantsAntiDriftTests
{
    private static string Src(params string[] rel) =>
        PresentationConstantsAntiDriftTests.Source(new[] { "src", "GridSim.Core" }.Concat(rel).ToArray());

    [Fact]
    public void Analysis_constants_equal_the_literals_they_replaced()
    {
        // R153
        Assert.Equal(1000.0, ContingencyAnalysis.StressVoltageWeightPerPu);
        Assert.Equal(100.0, ContingencyAnalysis.OverloadThresholdPercent);
        Assert.Equal(20, ContingencyAnalysis.DefaultCandidateCount);
        // R154
        Assert.Equal(1e6, DcOptimalPowerFlow.UnboundedPmaxMw);
        Assert.Equal(1e-6, DcOptimalPowerFlow.BalanceToleranceMw);
        Assert.Equal(1e-9, DcOptimalPowerFlow.MeritOrderFillStopMw);
        Assert.Equal(1e-3, DcOptimalPowerFlow.BindingToleranceMw);
        Assert.Equal(50.0, DcOptimalPowerFlow.DefaultCostPerMwh);
        Assert.Equal(50.0, DcOptimalPowerFlow.FuelCostPerMwh(null));
        Assert.Equal(50.0, DcOptimalPowerFlow.FuelCostPerMwh("unobtainium"));
        // R155
        Assert.Equal(1e-9, DistributionFactors.IslandingSensitivityTolerance);
        // R156
        Assert.Equal(1e-9, TapOptimizer.ImprovementEpsilon);
        Assert.Equal(0.02, TapOptimizer.InitialTapStepPu);
        Assert.Equal(1e9, TapOptimizer.NonConvergedPenalty);
        Assert.Equal(1e6, TapOptimizer.TapBoundPenalty);
        Assert.Equal(1e6, TapOptimizer.TapBoundPenaltyPerPu);
        Assert.Equal(0.0125, Oltc.DefaultStepPu);
        Assert.Equal(0.0125, new Oltc("T", 1).StepPu);
        // R157
        Assert.Equal(0.85, WatchList.DefaultThermalWatchFraction);
        Assert.Equal(0.85, new WatchListOptions().ThermalWatchFraction);
        Assert.Equal(0.85, WatchList.BoundaryWatchFraction);
        Assert.Equal(0.6, WatchList.ConvergenceStrainFraction);
        Assert.Equal(5000.0, WatchList.BoundaryConsequenceScaleMw);
        Assert.Equal(0.3, WatchList.BoundaryConsequenceFloor);
        Assert.Equal(0.5, WatchList.VoltageConsequenceBase);
        Assert.Equal(0.5, WatchList.VoltageConsequenceSlope);
    }

    [Fact]
    public void Estimation_constants_equal_the_literals_they_replaced()
    {
        // R174
        Assert.Equal(100.0, MeasuredInjectionPrior.MinQCapabilityBaseMva);
        Assert.Equal(0.6, MeasuredInjectionPrior.QCapabilityRatio);
        Assert.Equal(100, MeasuredInjectionPrior.PriorSolveMaxIterations);
        Assert.Equal(0.02, MeasuredInjectionPrior.DefaultVoltagePriorSigmaPu);
        Assert.Equal("pseudo-voltage-schedule", MeasuredInjectionPrior.VoltageScheduleSource);
        Assert.Equal("prior-solved", MeasuredInjectionPrior.SolvedPriorSource);
        Assert.Equal(1e-12, MeasurementModel.ZeroFlowMagnitudePu);
        Assert.Equal(1e-24, MeasurementModel.ZeroFlowMagnitudeSquaredPu);
        Assert.Equal(1.4826, ProjectionStatistics.MadNormalConsistency);
        Assert.Equal(1e-15, ProjectionStatistics.DegenerateScale);
        Assert.Equal(1e-3, FeasibleWlsStep.ThermalHysteresisFraction);
        Assert.Equal(1e-9, MeasurementSet.BaseMvaMatchToleranceMva);
        // R175
        Assert.Equal(10, WlsStateEstimator.IrlsMaxOuterIterations);
        Assert.Equal(1e-3, WlsStateEstimator.LavResidualFloor);
        Assert.Equal(1e-3, WlsStateEstimator.IrlsWeightTolerance);
        Assert.Equal(3.0, WlsStateEstimator.BackfillSigmaMultiplier);
        Assert.Equal(0.05, WlsStateEstimator.BackfillRelativeSigmaFloor);
    }

    [Fact]
    public void Io_and_model_defaults_equal_the_literals_they_replaced()
    {
        // R177 / R179
        Assert.Equal(100.0, GridModel.DefaultBaseMva);
        Assert.Equal(0.001, BridgeProtocol.MinDisplayFlowMw);
        Assert.Equal(100.0, JsonDefaults.BaseMva);
        Assert.Equal(50.0, JsonDefaults.FrequencyHz);
        // Moved 2026-10 (Q13, authorised): R179 makes bus baseKv REQUIRED, so the silent 400 kV default this line
        // pinned is the defect itself and is gone. Before: Assert.Equal(400.0, JsonDefaults.BusBaseKv) and
        // Assert.Equal(400.0, new BusDto().BaseKv). After: there is no default; an absent key is null and the loader
        // refuses it (JsonLoaderBaseKvRequiredTests).
        Assert.Null(new BusDto().BaseKv);
        Assert.Equal("ccgt", JsonDefaults.GeneratorType);
        Assert.Equal(0.97, JsonDefaults.LoadPowerFactor);
        Assert.Equal(0.01, JsonDefaults.MinPowerFactor);
        Assert.Equal("ccgt", new GenDto().Type);
        Assert.Equal(0.97, new DemandDto().PowerFactor);
        Assert.Equal(100.0, new SystemDto().BaseMva);
        Assert.Equal(50.0, new SystemDto().FrequencyHz);
    }

    [Fact]
    public void Numerics_constants_equal_the_literals_they_replaced()
    {
        // R180 (naming only: these steer the simplex pivot path)
        Assert.Equal(1e-9, BoundedSimplex.DefaultTolerance);
        Assert.Equal(20000, BoundedSimplex.DefaultMaxIterations);
        Assert.Equal(1e-7, BoundedSimplex.PhaseOneFeasibilityTolerance);
        Assert.Equal(64, BoundedSimplex.RefactorInterval);
        Assert.Equal(1e-12, BoundedSimplex.RatioTieMargin);
        Assert.Equal(1e-12, BoundedSimplex.EtaPivotFloor);
        Assert.Equal(1e-14, BoundedSimplex.RefactorPivotClamp);
        // R182
        Assert.Equal(40, GeneralEigen.DefaultInverseIterations);
        Assert.Equal(1e-9, GeneralEigen.DefaultBiorthonormalityTolerance);
        Assert.Equal(1e-6, GeneralEigen.BiorthonormalitySlack);
        Assert.Equal(60, GeneralEigen.QrMaxIterationsPerEigenvalue);
        Assert.Equal(1e-10, GeneralEigen.ShiftOffsetRelative);
        Assert.Equal(0.37, GeneralEigen.ShiftImaginarySkew);
        Assert.Equal(0.017, GeneralEigen.StartVectorRealStep);
        Assert.Equal(0.003, GeneralEigen.StartVectorImagStep);
        Assert.Equal(1e-14, GeneralEigen.InverseIterationStopTolerance);
        // R201 / R202: the GPU backends share the Core table (WS5); the one remaining literal is named.
        Assert.Equal(1e-12, GpuContext.Fp64SmokeTolerance);
        Assert.Equal(1e-10, NumericTolerances.DefaultKrylovTolerance);
        Assert.Equal(1e-300, NumericTolerances.UnderflowGuard);
        Assert.Equal(4 * 7 + 200, NumericTolerances.DefaultKrylovMaxIterations(7));
    }

    [Fact]
    public void Physics_and_power_flow_constants_equal_the_literals_they_replaced()
    {
        // R186
        Assert.Equal(1e-9, PowerFlowTracing.TraceZeroMw);
        Assert.Equal(1e-6, PowerFlowTracing.AllocationFloorMw);
        Assert.Equal(1e-3, PowerFlowTracing.ShareFloorMw);
        // R187
        Assert.Equal(0.5, WaveField.NominalWaveHz);
        Assert.Equal(1e-3, WaveField.MinModalHz);
        // R188: the generator Q slack IS the solver's Q-limit slack.
        Assert.Equal(1e-9, PhysicsFeasibility.VmBandTolPu);
        Assert.Equal(1e-3, PhysicsFeasibility.GenQTolMvar);
        Assert.Equal(NewtonShell.QLimitTolMva, PhysicsFeasibility.GenQTolMvar);
        Assert.Equal(1e-6, PhysicsFeasibility.RatingTolPercent);
        // R189
        Assert.Equal(1e-6, BackwardForwardSweep.MinControlTolPu);
        Assert.Equal(1e-14, BackwardForwardSweep.LoopPivotFloor);
        // R192: the REST cap stays pinned to the solver default.
        Assert.Equal(25, SteadyStateSolver.DefaultMaxOltcPasses);
        Assert.Equal(1e-12, SteadyStateSolver.TapChangeEpsilon);
        Assert.Equal(SteadyStateSolver.DefaultMaxOltcPasses, GridSim.Web.Api.RestApi.RestOltcPassCap);
    }

    [Fact]
    public void Protection_constants_equal_the_literals_they_replaced()
    {
        // R193
        var s = new ProtectionSettings();
        Assert.Equal(0.0, s.DistanceZone1TimeS);
        Assert.Equal(0.3, s.DistanceZone2TimeS);
        Assert.Equal(0.8, s.DistanceZone3TimeS);
        Assert.Equal(1e-9, DistanceRelay.MinLineImpedancePu);
        Assert.Equal(1e-9, ProtectionContext.MinVoltageForCurrentPu);
        Assert.Equal(1e-12, ProtectionContext.MinApparentPowerPu);
        // Moved 2026-10 (Q13, authorised): R193 removes the fabricated placeholder branch (a flow that matches no
        // branch now fails loud), so the constant this line pinned no longer exists. Before:
        // Assert.Equal(0.0001, ProtectionContext.PlaceholderBranchXPu). After: removed; the fail-loud behaviour is
        // pinned in Q13WsThirteenDecisionTests.
        Assert.Equal(12, ProtectionCascade.DefaultMaxSteps);
        Assert.Equal(100, ProtectionCascade.ResolveMaxIterations);
        // The 51 relay now reads the ONE IEC curve table; its standard-inverse entry is the old literal pair.
        Assert.Equal((0.14, 0.02), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecStandardInverse));
    }

    [Fact]
    public void Scenario_replay_and_scheduling_constants_equal_the_literals_they_replaced()
    {
        // R176 / R194
        Assert.Equal(30.0, ReplayValidator.PostEventTailSeconds);
        Assert.Equal(1.5, ScenarioCorners.BestDampingScale);
        Assert.Equal(1.5, ScenarioCorners.BestResponsiveScale);
        Assert.Equal(0.7, ScenarioCorners.BestGovernorTauScale);
        Assert.Equal(0.6, ScenarioCorners.WorstDampingScale);
        Assert.Equal(0.6, ScenarioCorners.WorstResponsiveScale);
        Assert.Equal(1.4, ScenarioCorners.WorstGovernorTauScale);
        Assert.Equal("gfm-vsm-study-2024", H0StudySpec.DefaultGfmClass);
        Assert.Equal("gfl-wind-2024", H0StudySpec.DefaultGflClass);
        Assert.Equal(50.0, AnalysisJobLibrary.SeedNMinus1HotMs);
        Assert.Equal(5.0, AnalysisJobLibrary.SeedNMinus1PerBranchMs);
        Assert.Equal(40.0, AnalysisJobLibrary.SeedHarmonicsMs);
        Assert.Equal(20.0, AnalysisJobLibrary.SeedRocofMs);
        Assert.Equal(5.0, AnalysisJobLibrary.SeedPhysicsAuditMs);
        Assert.Equal(2.0, AnalysisJobLibrary.SeedWatchListMs);
        Assert.Equal(0.5, AnalysisJobLibrary.HarmonicsSourceCurrentPu);
    }

    [Fact]
    public void ExtendToCover_reproduces_the_old_window_rule_exactly()
    {
        // R176: max(Duration, last event + 30 s), Max over the list (so order does not matter), never shorter.
        var cfg = new GridSim.Core.Dynamics.DynamicsConfig { DurationSeconds = 30.0 };
        var unsorted = new[]
        {
            new GridSim.Core.Dynamics.FrequencyEvent(90.0, -100.0),
            new GridSim.Core.Dynamics.FrequencyEvent(1.0, -50.0),
        };
        Assert.Equal(90.0 + 30.0, ReplayValidator.ExtendToCover(cfg, unsorted).DurationSeconds);
        Assert.Equal(30.0, ReplayValidator.ExtendToCover(cfg, Array.Empty<GridSim.Core.Dynamics.FrequencyEvent>()).DurationSeconds);
        var longCfg = cfg with { DurationSeconds = 500.0 };
        Assert.Equal(500.0, ReplayValidator.ExtendToCover(longCfg, unsorted).DurationSeconds);
    }

    [Fact]
    public void Simulation_and_storage_constants_equal_the_literals_they_replaced()
    {
        // R195
        Assert.Equal(1500, LiveSolveSettings.SparseThresholdBuses);
        Assert.Equal(40, LiveSolveSettings.SparseMaxIterations);
        Assert.Equal(25, LiveSolveSettings.OltcPasses);
        Assert.Equal(20260605, LiveSolveSettings.LiveSimSeed);
        Assert.Equal(480.0, LiveSolveSettings.StartMinuteOfDay);
        Assert.Equal(20.0, LiveSolveSettings.DefaultStepMinutes);
        Assert.Equal(100.0, LiveSolveSettings.MinInertiaMwS);
        Assert.Equal(4.0, LiveSolveSettings.FallbackInertiaH);
        Assert.Equal(0.0008, LiveSolveSettings.AmbientNoiseFraction);
        Assert.Equal(0.5, LiveSolveSettings.TripDeficitZeroMw);
        Assert.Equal(0.5, LiveSolveSettings.GdaSingleStepHours);
        Assert.Equal(1e-6, LiveSolveSettings.GdaMinStepHours);
        // R197
        Assert.Equal(0.2, AgeingModel.EndOfLifeFade);
        Assert.Equal(0.8, AgeingModel.MaxCapacityFade);
        Assert.Equal(0.05, AgeingModel.MinSocStressFactor);
        Assert.Equal(0.5, AgeingModel.SocStressReference);
        Assert.Equal(86400.0, AgeingModel.SecondsPerDay);
        Assert.Equal(2.0, AgeingModel.ThroughputPerFullCycle);
        Assert.Equal(30.0, CellModel.SelfDischargeMonthDays);
        Assert.Equal(30.0 * 86400.0, CellModel.SelfDischargeMonthDays * AgeingModel.SecondsPerDay);
        // R198
        Assert.Equal(1e-9, PackModel.DivisorGuard);
        Assert.Equal(1e-3, PackModel.SocEdgeProximity);
        Assert.Equal(1e-9, PackModel.LimitMatchToleranceA);
        Assert.Equal(1e-6, PackModel.LimitedRelativeTolerance);
        Assert.Equal(20.0, PackModel.MaxSearchCRate);
        Assert.Equal(1.0, PackModel.ProbePowerW);
        Assert.Equal(50, PackModel.MaxBisectionIterations);
        Assert.Equal(1e-4, PackModel.BisectionRelativeTolerance);
        Assert.Equal(0.5, StorageEnvelope.PlanningSoc);
        Assert.Equal(1800.0, StorageEnvelope.MaxSopHorizonSeconds);
        Assert.Equal(20, StorageEnvelope.SopSubsteps);
        Assert.Equal(1e-9, StorageEnvelope.MinRatedCellCurrentA);
        Assert.Equal(1e-6, StorageEnvelope.MinCellEmfV);
        Assert.Equal(0.5, StorageEnvelope.MinCellEfficiency);
        Assert.Equal(30, StorageFleet.DefaultSubstepsPerStep);
        Assert.Equal(100, StorageTimeSeries.DefaultMaxIterations);
        Assert.Equal(0.98, BatteryDcSource.DefaultConverterEfficiency);
        Assert.Equal(0.5, BatteryDcSource.MinConverterEfficiency);
    }

    [Fact]
    public void Named_literals_are_gone_from_the_edited_sources()
    {
        Assert.DoesNotContain("+ 30.0", Src("Evidence", "EvidenceReport.cs"));
        Assert.DoesNotContain("+ 30.0", Src("Replay", "ReplayValidator.cs"));
        Assert.DoesNotContain("+ 30.0", Src("Scenarios", "ScenarioEngine.cs"));
        Assert.DoesNotContain("+ 30.0", Src("Scenarios", "H0StudyEngine.cs"));
        Assert.DoesNotContain("?? 50.0", Src("Simulation", "PresolvedReplay.cs"));
        Assert.DoesNotContain("?? 50.0", Src("IO", "BridgeProtocol.cs"));
        Assert.DoesNotContain("LocalHz: 50.0", Src("Assets", "PassivePlantFleet.cs"));
        Assert.DoesNotContain("f0Hz = 50.0", Src("Protection", "ShortCircuit", "Iec60909.cs"));
        Assert.DoesNotContain("tms * 0.14", Src("Protection", "ProtectionRelays.cs"));
        Assert.DoesNotContain("sparseThreshold: 1500", Src("Simulation", "LiveSim.cs"));
        Assert.DoesNotContain("sparseThreshold: 1500", Src("Simulation", "GdaReplayLoop.cs"));
        Assert.DoesNotContain("double tol = 1e-3;", Src("Physics", "PhysicsFeasibility.cs"));
        Assert.DoesNotContain("1e-9", Src("Physics", "Experimental", "PowerFlowTracing.cs").Split("TraceZeroMw = 1e-9;")[1]);

        // R214: no machine-specific default path in the lake tool; the CLI is resolved per OS.
        string root = PresentationConstantsAntiDriftTests.RepoRoot();
        string lake = File.ReadAllText(Path.Combine(root, "tools", "crossval", "lake_conformance.py"));
        Assert.DoesNotContain(@"W:\Code", lake);
        Assert.Contains("GDA_ROOT", lake);
        string mc = File.ReadAllText(Path.Combine(root, "tools", "crossval", "mc_estimate_sweep.py"));
        Assert.Contains("sys.platform", mc);
        string freq = File.ReadAllText(Path.Combine(root, "tools", "crossval", "crossval_freq.py"));
        Assert.Contains("FFR_FULL_DELIVERY_DEVIATION_HZ = 0.5", freq);
        Assert.DoesNotContain("(ffr / 0.5)", freq);
    }
}
