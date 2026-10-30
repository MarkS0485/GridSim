using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;
using GridSim.Ui.Tabs;
using GridSim.Ui.Tabs.Generation;
using GridSim.Ui.Tabs.Inspector;
using GridSim.Ui.Tabs.InverterFleet;
using GridSim.Ui.Tabs.Machine;
using GridSim.Ui.Tabs.MachineEngineer;
using Xunit;
using Old = GridSim.Tests.Ui.OldTabsBatch2;
using OldDash = GridSim.Tests.Ui.OldDashboard;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 1.6 (second tab batch): the logic of THE MACHINE, MECHANICAL, INVERTER FLEET and the drill-down
/// inspector moved from their WPF view-models into the platform-neutral models under src/GridSim.Ui/Tabs, and the
/// GENERATION tab's map-trip swing study moved from the WPF window into <see cref="TripSwingStudy"/>. Each is an
/// orchestration-only move, so every value shown must be exactly what the WPF code showed before. These tests feed the
/// same frames (from <see cref="SimHost"/>, case9 and the GB reduced case, plus variants built from them that reach the
/// branches a plain run does not) and the same calls to the new model and to a verbatim copy of the old WPF code
/// (<c>UiTabsBatch2Oracle.cs</c>, <c>UiTripSwingStudyOracle.cs</c>), then compare EVERY public property, and every row
/// of every list, exactly: strings ordinal, doubles by their bits, colour tokens against the colour of the brush the old
/// code chose, bools against the old Visibility, an action against the old command (both present or both absent). A
/// property added to either side later fails the coverage check until it is compared here.
/// </summary>
public sealed class UiTabsBatch2ParityTests
{
    private const int Ticks = 12;

    public static TheoryData<string> Cases => new() { "case9", "uk" };

    // ---------------------------------------------------------------------------------------------------------------
    // Frames
    // ---------------------------------------------------------------------------------------------------------------

    private static ModelSource SourceOf(string name)
    {
        if (name == "case9")
        {
            string file = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case9.m");
            Assert.True(File.Exists(file), $"bundled case not found: {file}");
            return new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
        }
        string dir = Path.Combine(AppContext.BaseDirectory, "data", "uk");
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
        return new ModelSource("GB reduced  10-zone", dir);
    }

    /// <summary>The live host loaded with a case.</summary>
    internal static SimHost Host(string name)
    {
        var host = new SimHost();
        LiveLoadResult loaded = host.LoadLive(SourceOf(name), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
        Assert.True(loaded.Loaded, loaded.Refusal);
        return host;
    }

    /// <summary>The host's next solved frame.</summary>
    internal static HostFrame Next(SimHost host)
    {
        HostFrame hf = Assert.IsType<HostFrame>(host.ProduceNext());
        Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
        return hf;
    }

    /// <summary>
    /// <paramref name="f"/> with its generators given reactive output (a third absorbing, a third producing), one unit
    /// pushed to 96 % and one to 87 % of its rating (the LIMIT and NEAR risk bands), every output scaled by
    /// <paramref name="pgScale"/> (a step of more than 40 MW is a torsional pulse), the frequency moved by
    /// <paramref name="dHz"/> and the RoCoF set to <paramref name="rocof"/>: branches a steady run does not reach.
    /// </summary>
    private static SimFrame Stressed(SimFrame f, double pgScale, double dHz, double rocof)
    {
        IReadOnlyList<Generator> gens = f.Model.Generators;
        var changed = new List<Generator>(gens.Count);
        for (int i = 0; i < gens.Count; i++)
        {
            // An untyped unit (case9's are all untyped, so not synchronous) is given a spinning fuel, so the MECHANICAL
            // tab has machines to list and THE MACHINE tab more than one bucket.
            Generator g = gens[i] with
            {
                Pg = gens[i].Pg * pgScale,
                Type = string.IsNullOrEmpty(gens[i].Type) ? (i % 2 == 0 ? "CCGT" : "nuclear") : gens[i].Type,
            };
            g = (i % 3) switch
            {
                0 => g with { Qg = -0.3 * g.Pg },
                1 => g with { Qg = 0.4 * g.Pg },
                _ => g,
            };
            if (i == 1 && double.IsFinite(g.Pmax) && g.Pmax > 0) g = g with { Pg = 0.96 * g.Pmax };
            if (i == 2 && double.IsFinite(g.Pmax) && g.Pmax > 0) g = g with { Pg = 0.87 * g.Pmax };
            changed.Add(g);
        }
        return f with
        {
            Model = f.Model.CopyWith(generators: changed),
            FrequencyHz = f.FrequencyHz + dHz,
            RocofHzPerS = rocof,
            TapsSettled = false,
            TapsAtLimit = pgScale > 1 ? new[] { "T1", "T2" } : null,
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // THE MACHINE
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_machine_tab_shows_exactly_what_the_old_wpf_view_model_showed_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var neu = new MachineModel();
        var old = new Old.MachineVm();
        AssertSame(old, neu, $"{name} before the first frame");

        for (int tick = 1; tick <= Ticks; tick++)
        {
            SimFrame f = Next(host).Frame!;
            neu.Update(f); old.Update(f);
            AssertSame(old, neu, $"{name} tick {tick}");

            switch (tick)
            {
                case 3:
                    // Fast, slow and far-off frequencies (the torque story's three verdicts, the clock's two signs, the
                    // battery charging and discharging), reactive output and a tap loop that did not settle.
                    foreach ((double scale, double dHz) in new[] { (1.0, 0.35), (1.6, -0.6), (0.7, 0.05), (1.0, double.NaN) })
                    {
                        SimFrame v = Stressed(f, scale, dHz, 0.3) with { Hour = f.Hour + 0.25, Tick = f.Tick + 1 };
                        neu.Update(v); old.Update(v);
                        AssertSame(old, neu, $"{name} tick {tick} variant x{scale} {dHz:R} Hz");
                    }
                    break;
                case 5:
                    // A replay stepping backwards: a discontinuity, nothing integrated.
                    SimFrame back = f with { Tick = 1, Hour = f.Hour - 1 };
                    neu.Update(back); old.Update(back);
                    AssertSame(old, neu, $"{name} tick {tick} backwards");
                    break;
                case 7:
                    neu.Reset(); old.Reset();
                    AssertSame(old, neu, $"{name} tick {tick} reset");
                    break;
            }
        }
    }

    [Fact]
    public void The_machine_tab_rebuilds_its_rows_exactly_as_before_across_model_switches()
    {
        var neu = new MachineModel();
        var old = new Old.MachineVm();
        foreach (string name in new[] { "case9", "uk", "case9" })
        {
            SimHost host = Host(name);
            neu.Reset(); old.Reset();
            for (int tick = 1; tick <= 4; tick++)
            {
                SimFrame f = Next(host).Frame!;
                neu.Update(f); old.Update(f);
                AssertSame(old, neu, $"{name} tick {tick} after a switch");
            }
        }
    }

    [Fact]
    public void The_machine_tab_constants_and_buckets_are_the_old_ones()
    {
        Assert.Equal(Old.MachineVm.BarTrack, MachineModel.BarTrack);
        Assert.Equal(Old.MachineVm.TwoPoleSyncRpm, MachineModel.TwoPoleSyncRpm);
        Assert.Equal(Old.MachineVm.TorqueNarrativeBandMw, MachineModel.TorqueNarrativeBandMw);
        Assert.Equal(Old.MachineVm.VoltageWeightEpsilonMw, MachineModel.VoltageWeightEpsilonMw);
        Assert.Equal(Old.MachineVm.DefaultOltcDeadbandPu, MachineModel.DefaultOltcDeadbandPu);
        Assert.Equal(Old.MachineVm.TapAtLimitStepFraction, MachineModel.TapAtLimitStepFraction);
        Assert.Equal(Old.MachineVm.BessInitialSocPct, MachineModel.BessInitialSocPct);
        // Every bucket the old private Bucket table names, through the old and the neutral classification.
        MethodInfo oldBucket = typeof(Old.MachineVm).GetMethod("Bucket", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (string? type in new[] { null, "", "ccgt", "ocgt", "gas", "chp", "coal", "nuclear", "wind", "wind onshore", "offshore wind",
                     "solar", "hydro", "pumped storage", "tidal", "biomass", "interconnector", "grid infeed", "battery", "storage", "hydrogen", "recip", "unknown" })
        {
            var o = ((string, UiColour))oldBucket.Invoke(null, new object[] { OldDash.Fuels.Classify(type) })!;
            (string name, UiColour col) n = MachineModel.Bucket(UiFuels.Classify(type));
            Assert.Equal(o.Item1, n.name);
            Assert.Equal(o.Item2, n.col);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // MECHANICAL / PLANT ENGINEER, with its inspector
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_mechanical_tab_shows_exactly_what_the_old_wpf_view_model_showed_frame_for_frame(string name)
    {
        SimHost host = Host(name);
        var neu = new MachineEngineerModel();
        var old = new Old.MachineEngineerVm();
        AssertSame(old, neu, $"{name} before the first frame");

        // Before any frame there is no row: an inspect of nothing does nothing on either side.
        neu.InspectMachine(null); old.InspectMachineCommand.Execute(null);
        AssertSame(old, neu, $"{name} inspect of no row");

        for (int tick = 1; tick <= Ticks; tick++)
        {
            SimFrame f = Next(host).Frame!;
            string clock = TabClock.Of(f);
            neu.Update(f, clock); old.Update(f, clock);
            AssertSame(old, neu, $"{name} tick {tick}");

            switch (tick)
            {
                case 2:
                    // The lake's bottom-up inertia (bridge only), then cleared by a reset below.
                    neu.SetIntrinsic(123.456); old.SetIntrinsic(123.456);
                    break;
                case 3:
                    // Ramps up and down (pulses), the LIMIT and NEAR bands, reactive output, RoCoF across both relay edges.
                    foreach ((double scale, double rocof) in new[] { (1.6, 0.11), (0.5, -0.9), (1.0, 1.4), (1.0, 0.0) })
                    {
                        SimFrame v = Stressed(f, scale, 0, rocof) with { Hour = f.Hour + 0.5 };
                        neu.Update(v, clock); old.Update(v, clock);
                        AssertSame(old, neu, $"{name} tick {tick} variant x{scale} RoCoF {rocof:R}");
                    }
                    // The inspector, on the last variant's machines: open on the most-stressed machine, push a second
                    // page, back, back (closes), reopen, close.
                    Assert.NotEmpty(neu.Machines);
                    Inspect(0); AssertSame(old, neu, $"{name} tick {tick} inspect 0");
                    Inspect(neu.Machines.Count - 1); AssertSame(old, neu, $"{name} tick {tick} inspect last");
                    Assert.True(neu.Inspector.CanBack);
                    neu.Inspector.Back(); old.Inspector.BackCommand.Execute(null); AssertSame(old, neu, $"{name} tick {tick} back");
                    neu.Inspector.Back(); old.Inspector.BackCommand.Execute(null); AssertSame(old, neu, $"{name} tick {tick} back closes");
                    Assert.False(neu.Inspector.IsOpen);
                    Inspect(0);
                    neu.Inspector.Close(); old.Inspector.CloseCommand.Execute(null); AssertSame(old, neu, $"{name} tick {tick} close");
                    break;
                case 6:
                    neu.Reset(); old.Reset();
                    AssertSame(old, neu, $"{name} tick {tick} reset");
                    break;
            }
        }

        void Inspect(int i)
        {
            neu.InspectMachine(neu.Machines[i]);
            old.InspectMachineCommand.Execute(old.Machines[i]);
        }
    }

    [Fact]
    public void The_mechanical_tab_constants_are_the_old_ones()
    {
        Assert.Equal(Old.MachineEngineerVm.DefaultPeriodMin, MachineEngineerModel.DefaultPeriodMin);
        Assert.Equal(Old.MachineEngineerVm.RampDeadbandMwPerMin, MachineEngineerModel.RampDeadbandMwPerMin);
        Assert.Equal(Old.MachineEngineerVm.TorsionalPulseStepMw, MachineEngineerModel.TorsionalPulseStepMw);
        Assert.Equal(Old.MachineEngineerVm.StressLimitFraction, MachineEngineerModel.StressLimitFraction);
        Assert.Equal(Old.MachineEngineerVm.StressNearFraction, MachineEngineerModel.StressNearFraction);
        Assert.Equal(Old.MachineEngineerVm.InertiaProjectionMinutes, MachineEngineerModel.InertiaProjectionMinutes);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The inspector on its own (as the NESO RP1 tab drives it: its own tiles, rows that drill, a long trail)
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_inspector_pages_back_stack_and_drill_rows_are_the_old_ones()
    {
        var neu = new InspectorModel();
        var old = new Old.InspectorVm();
        AssertSame(old, neu, "closed");

        UiColour green = ColourRamps.FromHex("#2FD27F"), swatch = ColourRamps.FromHex("#7E57C2");
        int drills = 0;
        // A page as NESO RP1 builds one: a tile with the default colour and one coloured, a row that drills (into the
        // next page) and one that does not; the same page on each side, each drilling only on its own side.
        void Page(int n) { NeuPage(n); OldPage(n); }
        void NeuPage(int n) => neu.Show($"PAGE {n}", $"sub {n}",
            new[] { neu.CreateStat("UNITS", $"{n}"), neu.CreateStat("INERTIA", $"{n}.5 GVA.s", green) },
            new[]
            {
                neu.CreateRow($"row {n}", $"{n * 10} MW", swatch, green, sub: "drills", onDrill: () => { drills++; NeuPage(n + 1); }),
                neu.CreateRow("plain", "-", DashboardColours.Transparent, InspectStatModel.White, secondary: "s"),
            });
        void OldPage(int n) => old.Show($"PAGE {n}", $"sub {n}",
            new[] { new Old.InspectStat { Label = "UNITS", Value = $"{n}" }, new Old.InspectStat { Label = "INERTIA", Value = $"{n}.5 GVA.s", ValueBrush = Old.Palette.Frozen(green) } },
            new[]
            {
                new Old.InspectRow { Primary = $"row {n}", Value = $"{n * 10} MW", Sub = "drills", Swatch = Old.Palette.Frozen(swatch), ValueBrush = Old.Palette.Frozen(green), Drill = new Old.RelayCommand(() => OldPage(n + 1)) },
                new Old.InspectRow { Primary = "plain", Value = "-", Secondary = "s" },
            });

        Page(1);
        AssertSame(old, neu, "page 1");
        // Drill three deep through the rows (the old command and the new action), then come all the way back.
        for (int d = 0; d < 3; d++)
        {
            neu.Rows[0].DrillIn();
            old.Rows[0].Drill!.Execute(null);
            AssertSame(old, neu, $"drilled {d + 1}");
        }
        Assert.Equal(3, drills);
        neu.Rows[1].DrillIn(); old.Rows[1].Drill?.Execute(null);   // a plain row does nothing
        AssertSame(old, neu, "plain row clicked");
        for (int b = 0; b < 5; b++)
        {
            neu.Back(); old.BackCommand.Execute(null);
            AssertSame(old, neu, $"back {b + 1}");
        }
        Page(7);
        neu.Close(); old.CloseCommand.Execute(null);
        AssertSame(old, neu, "closed again");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // INVERTER FLEET
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_inverter_fleet_tab_shows_exactly_what_the_old_wpf_view_model_showed_frame_for_frame(string name)
    {
        // The bundled cases carry no asset-class library, so their frames have no fleet (the tab reads OFF); the fleet
        // frames are the same frames with authored roll-ups attached, the shape LiveSim gives them.
        SimHost host = Host(name);
        var neu = new InverterFleetModel();
        var old = new Old.InverterFleetVm();
        AssertSame(old, neu, $"{name} before the first frame");
        AssetClassRollup[][] fleets =
        {
            new[] { new AssetClassRollup("Domestic PV", "grid-following", 1200, 345.678, 0, 0) },
            new[]
            {
                new AssetClassRollup("BESS grid-forming", "grid-forming", 12, -45.04, 0, 63.4),
                new AssetClassRollup("Embedded PV", "grid-following", 4000, 812.25, 37, 0),
                new AssetClassRollup("Wind (LoM relay)", "grid-following", 3, 0.04, 3, 0.4),
            },
            new[] { new AssetClassRollup("Solo", "grid-forming", 1, -0.04, 1, 99.6) },
        };
        for (int tick = 1; tick <= Ticks; tick++)
        {
            SimFrame f = Next(host).Frame!;
            neu.Update(f); old.Update(f);
            AssertSame(old, neu, $"{name} tick {tick}");
            AssetClassRollup[] fleet = fleets[tick % fleets.Length];
            SimFrame withFleet = f with { AssetFleet = fleet, AssetFleetNetMw = (tick - 6) * 37.5, AssetTrippedMw = tick % 2 == 0 ? 0 : tick * 12.34 };
            neu.Update(withFleet); old.Update(withFleet);
            AssertSame(old, neu, $"{name} tick {tick} with a fleet");
            if (tick % 4 == 0)
            {
                SimFrame empty = f with { AssetFleet = Array.Empty<AssetClassRollup>() };
                neu.Update(empty); old.Update(empty);
                AssertSame(old, neu, $"{name} tick {tick} empty fleet (OFF again)");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The frame clock a tab is handed
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_tab_clock_is_the_dashboard_clock_for_every_minute_and_awkward_hours()
    {
        SimFrame f = Next(Host("case9")).Frame!;
        var dash = new DashboardModel();
        var hours = new List<double>();
        for (int minute = 0; minute < 24 * 60; minute++) hours.Add(minute / 60.0);
        hours.AddRange(new[] { 0.0, 1e-12, 23.999999999, 24.0, 24.5, 47.25, 7.0166666666666666, 12.9999, 0.0166666, -0.5, 1e6 + 0.75 });
        foreach (double h in hours)
        {
            SimFrame at = f with { Hour = h };
            dash.Update(at, Array.Empty<GridSim.Core.Analysis.BoundaryFlow>());
            Assert.Equal(dash.Clock, TabClock.Of(at));
            Assert.Equal(dash.Clock, TabClock.Of(h));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The WPF adapters (source contract: the WPF project is not loaded by the test host)
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_wpf_adapter_maps_every_colour_token_to_its_old_brush_name()
    {
        // Every UiColour property of each neutral model and row is announced to the XAML under its old WPF name, as a
        // frozen brush of that token; and every token property has an entry here (none forgotten).
        foreach ((Type type, string file, (string Neutral, string Adapter)[] map) in AdapterMaps)
        {
            string wpf = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", file);
            foreach ((string neutral, string adapter) in map)
                Assert.True(
                    wpf.Contains($"public Brush {adapter} => Palette.Frozen({neutral});", StringComparison.Ordinal)
                    || wpf.Contains($"get => _{char.ToLowerInvariant(adapter[0])}{adapter[1..]} ?? Palette.Frozen({neutral});", StringComparison.Ordinal),
                    $"{file}: no brush {adapter} from {neutral}");
            string[] tokens = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(UiColour)).Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(tokens, map.Select(m => m.Neutral).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }
        // The visibilities and commands the XAML binds.
        string insp = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "InspectorVm.cs");
        Assert.Contains("public Visibility Visibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;", insp, StringComparison.Ordinal);
        Assert.Contains("public Visibility BackVisibility => CanBack ? Visibility.Visible : Visibility.Collapsed;", insp, StringComparison.Ordinal);
        Assert.Contains("public Visibility Chevron => HasDrill ? Visibility.Visible : Visibility.Collapsed;", insp, StringComparison.Ordinal);
        Assert.Contains("CloseCommand = new RelayCommand(Close);", insp, StringComparison.Ordinal);
        Assert.Contains("BackCommand = new RelayCommand(Back);", insp, StringComparison.Ordinal);
        Assert.Contains("InspectMachineCommand = new GridSim.Wpf.Views.Assets.RelayCommand<MachineRow>(InspectMachine);",
            PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "MachineEngineerVm.cs"), StringComparison.Ordinal);
        // Each WPF view-model is a thin adapter: it derives from the neutral model and keeps no Update of its own.
        foreach ((string file, string decl) in new[]
                 {
                     ("MachineVm.cs", "public sealed class MachineVm : MachineModel"),
                     ("MachineEngineerVm.cs", "public sealed class MachineEngineerVm : MachineEngineerModel"),
                     ("InverterFleetVm.cs", "public sealed class InverterFleetVm : InverterFleetModel"),
                     ("InspectorVm.cs", "public sealed class InspectorVm : InspectorModel"),
                 })
        {
            string src = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", file);
            Assert.Contains(decl, src, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"public\s+void\s+Update\s*\(", src);
            Assert.DoesNotMatch(@"public\s+void\s+Reset\s*\(", src);
        }
    }

    /// <summary>Each neutral type, its WPF adapter file, and its colour tokens with the old brush names.</summary>
    private static readonly (Type Type, string File, (string Neutral, string Adapter)[] Map)[] AdapterMaps =
    {
        (typeof(MachineModel), "MachineVm.cs", new[] { ("DivColour", "DivBrush"), ("AccumColour", "AccumBrush"), ("BessColour", "BessBrush"), ("VoltColour", "VoltBrush"), ("QColour", "QBrush") }),
        (typeof(StreamRowModel), "MachineVm.cs", new[] { ("Colour", "Color") }),
        (typeof(VoltageLayerModel), "MachineVm.cs", new[] { ("Colour", "Color"), ("DevColour", "DevBrush") }),
        (typeof(TapRowModel), "MachineVm.cs", new[] { ("Colour", "Color") }),
        (typeof(MachineEngineerModel), "MachineEngineerVm.cs", new[] { ("RocofLegacyColour", "RocofLegacyBrush"), ("RocofModernColour", "RocofModernBrush"), ("ProjColour", "ProjBrush") }),
        (typeof(MachineRowModel), "MachineEngineerVm.cs", new[] { ("SwatchColour", "Swatch"), ("RiskColour", "RiskBrush"), ("RampColour", "RampBrush") }),
        (typeof(InverterFleetModel), "InverterFleetVm.cs", new[] { ("TrippedColour", "TrippedBrush") }),
        (typeof(InverterFleetRowModel), "InverterFleetVm.cs", new[] { ("ModeColour", "ModeBrush"), ("TrippedColour", "TrippedBrush") }),
        (typeof(InspectStatModel), "InspectorVm.cs", new[] { ("ValueColour", "ValueBrush") }),
        (typeof(InspectRowModel), "InspectorVm.cs", new[] { ("SwatchColour", "Swatch"), ("ValueColour", "ValueBrush") }),
    };

    // ---------------------------------------------------------------------------------------------------------------
    // The comparison
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The old property each neutral one stands in for, by the neutral object's type.</summary>
    private static string OldName(object neu, string name) => (neu, name) switch
    {
        (MachineModel, "DivColour") => "DivBrush",
        (MachineModel, "AccumColour") => "AccumBrush",
        (MachineModel, "BessColour") => "BessBrush",
        (MachineModel, "VoltColour") => "VoltBrush",
        (MachineModel, "QColour") => "QBrush",
        (StreamRowModel or TapRowModel or VoltageLayerModel, "Colour") => "Color",
        (VoltageLayerModel, "DevColour") => "DevBrush",
        (MachineEngineerModel, "RocofLegacyColour") => "RocofLegacyBrush",
        (MachineEngineerModel, "RocofModernColour") => "RocofModernBrush",
        (MachineEngineerModel, "ProjColour") => "ProjBrush",
        (MachineRowModel, "SwatchColour") => "Swatch",
        (MachineRowModel, "RiskColour") => "RiskBrush",
        (MachineRowModel, "RampColour") => "RampBrush",
        (InverterFleetModel or InverterFleetRowModel, "TrippedColour") => "TrippedBrush",
        (InverterFleetRowModel, "ModeColour") => "ModeBrush",
        (InspectStatModel or InspectRowModel, "ValueColour") => "ValueBrush",
        (InspectRowModel, "SwatchColour") => "Swatch",
        (InspectRowModel, "OnDrill") => "Drill",
        (InspectRowModel, "HasDrill") => "Chevron",
        _ => name,
    };

    /// <summary>
    /// Old properties with no neutral property of the same role, by the old object's type, each checked here against
    /// the neutral object: the inspector's two visibilities (its two bools) and its two commands, and the MECHANICAL
    /// tab's row command (the neutral InspectMachine, exercised above through it).
    /// </summary>
    private static bool CheckOldOnly(object old, object neu, string name, string at)
    {
        switch (old, name)
        {
            case (Old.InspectorVm o, "Visibility"):
                Assert.True(o.Visibility == (((InspectorModel)neu).IsOpen ? Old.Visibility.Visible : Old.Visibility.Collapsed), $"{at}: Visibility");
                return true;
            case (Old.InspectorVm o, "BackVisibility"):
                Assert.True(o.BackVisibility == (((InspectorModel)neu).CanBack ? Old.Visibility.Visible : Old.Visibility.Collapsed), $"{at}: BackVisibility");
                return true;
            case (Old.InspectorVm o, "CloseCommand" or "BackCommand"):
                Assert.NotNull(name == "CloseCommand" ? o.CloseCommand : o.BackCommand);
                return true;
            case (Old.MachineEngineerVm o, "InspectMachineCommand"):
                Assert.NotNull(o.InspectMachineCommand);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Every public property of <paramref name="neu"/> equals its old counterpart, and every old property is compared.</summary>
    internal static void AssertSame(object old, object neu, string at)
    {
        PropertyInfo[] neuProps = neu.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract").ToArray();
        PropertyInfo[] oldProps = old.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract").ToArray();
        var oldByName = oldProps.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo np in neuProps)
        {
            string oldName = OldName(neu, np.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? op), $"{at}: the old {old.GetType().Name} has no {oldName} for {np.Name}");
            covered.Add(oldName);
            SameValue(op!.GetValue(old), np.GetValue(neu), $"{at}: {np.Name}");
        }
        foreach (PropertyInfo op in oldProps)
            if (!covered.Contains(op.Name) && CheckOldOnly(old, neu, op.Name, at)) covered.Add(op.Name);
        Assert.Equal(oldProps.Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal), covered.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>The colour of an old brush (either oracle's shim).</summary>
    private static UiColour OldBrushColour(object? ov, string what) => ov switch
    {
        Old.SolidColorBrush b => b.Color,
        OldDash.SolidColorBrush b => b.Color,
        _ => throw new Xunit.Sdk.XunitException($"{what}: the old value is not a solid brush but {ov?.GetType().Name ?? "null"}"),
    };

    /// <summary>One value of the old code against the neutral value that replaced it.</summary>
    internal static void SameValue(object? ov, object? nv, string what)
    {
        switch (nv)
        {
            case null:
                Assert.True(ov is null, $"{what}: want {ov}, got null");
                return;
            case string s:
                Assert.True(ov is string os && string.Equals(os, s, StringComparison.Ordinal), $"{what}: want \"{ov}\", got \"{s}\"");
                return;
            case double d:
                Assert.True(ov is double od && BitConverter.DoubleToInt64Bits(od) == BitConverter.DoubleToInt64Bits(d), $"{what}: want {ov:R}, got {d:R}");
                return;
            case int i:
                Assert.True(ov is int oi && oi == i, $"{what}: want {ov}, got {i}");
                return;
            case UiColour c:
                UiColour oc = OldBrushColour(ov, what);
                Assert.True(oc == c, $"{what}: want {ColourRamps.ToHex(oc)}, got {ColourRamps.ToHex(c)}");
                return;
            case bool b when ov is Old.Visibility vis:
                Assert.True((b ? Old.Visibility.Visible : Old.Visibility.Collapsed) == vis, $"{what}: want {vis}, got {b}");
                return;
            case bool b when ov is OldDash.Visibility dvis:
                Assert.True((b ? OldDash.Visibility.Visible : OldDash.Visibility.Collapsed) == dvis, $"{what}: want {dvis}, got {b}");
                return;
            case bool b:
                Assert.True(ov is bool ob && ob == b, $"{what}: want {ov}, got {b}");
                return;
            case long or float or decimal or char or byte or DateTime or TimeSpan:
                // Other plain values: equal by value and of the same type (no property walk: a DateTime's Date is a
                // DateTime, so walking it would never end).
                Assert.True(ov is not null && ov.GetType() == nv.GetType() && ov.Equals(nv), $"{what}: want {ov}, got {nv}");
                return;
            case Action:
                // A row's drill: the neutral action stands in for the old command; both present (both absent is the
                // null case above, which then requires the old command to be null too).
                Assert.True(ov is System.Windows.Input.ICommand, $"{what}: the new row drills but the old row has no command");
                return;
            case MapColourMode mode:
                var ramp = Assert.IsType<OldDash.LinearGradientBrush>(ov);
                IReadOnlyList<(UiColour Colour, double Offset)> stops = DashboardModel.LegendRampStops(mode);
                Assert.Equal(ramp.Stops.Count, stops.Count);
                for (int k = 0; k < stops.Count; k++)
                {
                    Assert.True(ramp.Stops[k].Color == stops[k].Colour, $"{what}: stop {k} colour");
                    SameValue(ramp.Stops[k].Offset, stops[k].Offset, $"{what}: stop {k} offset");
                }
                return;
            case Enum:
                // Any other enum (after the legend mode above): equal by value and of the same type.
                Assert.True(ov is not null && ov.GetType() == nv.GetType() && ov.Equals(nv), $"{what}: want {ov}, got {nv}");
                return;
            case FrequencyResponse r:
                var orr = Assert.IsType<FrequencyResponse>(ov);
                SameValue(orr.InitialRocofHzPerS, r.InitialRocofHzPerS, what + ".InitialRocofHzPerS");
                SameValue(orr.NadirHz, r.NadirHz, what + ".NadirHz");
                SameValue(orr.NadirSeconds, r.NadirSeconds, what + ".NadirSeconds");
                SameValue(orr.SettleHz, r.SettleHz, what + ".SettleHz");
                Assert.Equal(orr.Points.Count, r.Points.Count);
                for (int k = 0; k < r.Points.Count; k++)
                {
                    SameValue(orr.Points[k].Time, r.Points[k].Time, $"{what}.Points[{k}].Time");
                    SameValue(orr.Points[k].Hz, r.Points[k].Hz, $"{what}.Points[{k}].Hz");
                    SameValue(orr.Points[k].RocofHzPerS, r.Points[k].RocofHzPerS, $"{what}.Points[{k}].RocofHzPerS");
                }
                return;
            case FrequencyBands fb:
                var ob2 = Assert.IsType<FrequencyBands>(ov);
                foreach (PropertyInfo p in typeof(FrequencyBands).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.PropertyType == typeof(double)) SameValue(p.GetValue(ob2), p.GetValue(fb), $"{what}.{p.Name}");
                return;
            case IList list:
                var olist = Assert.IsAssignableFrom<IList>(ov);
                Assert.True(olist.Count == list.Count, $"{what}: want {olist.Count} rows, got {list.Count}");
                for (int k = 0; k < list.Count; k++) SameValue(olist[k], list[k], $"{what}[{k}]");
                return;
            default:
                // A row or a nested model (the selected machine, the inspector, a tile, an alarm): every property.
                // The path grows with each level, so a type that contains itself fails loud here instead of overflowing.
                Assert.True(what.Length < 2000, $"{what}: the comparison nests too deep (a {nv.GetType().Name} that contains itself?)");
                Assert.NotNull(ov);
                if (ov is OldDash.DashboardViewModel || nv is DashboardModel) { AssertSameDashboard((OldDash.DashboardViewModel)ov!, (DashboardModel)nv, what); return; }
                if (IsDashboardRow(nv)) { SameDashboardRow(ov!, nv, what); return; }
                AssertSame(ov!, nv, what);
                return;
        }
    }

    // ---- the dashboard (for the trip study: its whole state after the study, the log line and the alarm) ----

    /// <summary>Each neutral dashboard token / bool / legend property and the old WPF property it stands in for (the
    /// same list UiDashboardParityTests pins).</summary>
    private static readonly Dictionary<string, string> DashRenamed = new(StringComparer.Ordinal)
    {
        ["FreqColour"] = "FreqBrush", ["InertiaNoteColour"] = "InertiaNoteBrush", ["StatusColour"] = "StatusBrush",
        ["QueryVerdictColour"] = "QueryVerdictBrush", ["FragilityColour"] = "FragilityBrush",
        ["FragilityTrendColour"] = "FragilityTrendBrush", ["BridgeStatusColour"] = "BridgeStatusBrush",
        ["StabilityColour"] = "StabilityBrush", ["TripVerdictColour"] = "TripVerdictBrush", ["Legend"] = "LegendBrush",
        ["InvalidState"] = "InvalidStateVis", ["SelectionVisible"] = "SelectionVis", ["QueryVisible"] = "QueryVis",
        ["QueryViolationsEmpty"] = "QueryViolationsEmptyVis", ["HudVisible"] = "HudVis",
        ["BridgeStatusVisible"] = "BridgeStatusVis", ["AlarmsEmpty"] = "AlarmsEmptyVis",
    };

    internal static void AssertSameDashboard(OldDash.DashboardViewModel old, DashboardModel neu, string at)
    {
        PropertyInfo[] neuProps = typeof(DashboardModel).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo[] oldProps = typeof(OldDash.DashboardViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var oldByName = oldProps.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo np in neuProps)
        {
            string oldName = DashRenamed.GetValueOrDefault(np.Name, np.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? op), $"{at}: the old dashboard has no {oldName}");
            covered.Add(oldName);
            SameValue(op!.GetValue(old), np.GetValue(neu), $"{at}: {np.Name}");
        }
        Assert.Equal(oldProps.Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal), covered.OrderBy(s => s, StringComparer.Ordinal));
    }

    private static bool IsDashboardRow(object nv) =>
        nv is BoundaryRowModel or ImportLinkRowModel or FuelRowModel or VoltageBandDialModel or AlarmEntry or ViolationEntry or KeyVal;

    private static string OldDashRowName(object neuRow, string name) => (neuRow, name) switch
    {
        (BoundaryRowModel or ImportLinkRowModel, "BarColour") => "Bar",
        (ImportLinkRowModel, "BadgeColour") => "BadgeBrush",
        (FuelRowModel, "Colour") => "Color",
        (VoltageBandDialModel or AlarmEntry or ViolationEntry, "Colour") => "Brush",
        _ => name,
    };

    private static void SameDashboardRow(object orow, object nrow, string what)
    {
        PropertyInfo[] np = nrow.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract").ToArray();
        PropertyInfo[] op = orow.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract").ToArray();
        var oldByName = op.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo p in np)
        {
            string oldName = OldDashRowName(nrow, p.Name);
            Assert.True(oldByName.TryGetValue(oldName, out PropertyInfo? o), $"{what}: the old row has no {oldName}");
            covered.Add(oldName);
            SameValue(o!.GetValue(orow), p.GetValue(nrow), $"{what}.{p.Name}");
        }
        Assert.Equal(op.Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal), covered.OrderBy(s => s, StringComparer.Ordinal));
    }
}
