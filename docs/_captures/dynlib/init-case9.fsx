// Seeds the case9 RMS fleet exactly as `gridsim stability case9 --rms` does and prints machine 1.
// Run from the worktree root:  dotnet fsi docs/_captures/dynlib/init-case9.fsx
#r "../../../src/GridSim.Cli/bin/Release/net10.0/GridSim.Core.dll"
open System
open GridSim.Core.IO
open GridSim.Core.PowerFlow
open GridSim.Core.Dynamics.Models

let model = IeeeCases.Case9()
let pf = NewtonRaphsonPowerFlow().Solve(model, PowerFlowOptions(MaxIterations = 100))
printfn "power flow converged=%b iterations=%d" pf.Converged pf.Iterations
printfn "bus 1 solved: Vm=%.15g VaDeg=%.15g P=%.15g MW Q=%.15g MVAr" pf.Vm.[0] pf.VaDeg.[0] pf.PInjMw.[0] pf.QInjMvar.[0]

// The CLI data: X'd by bus (Anderson-Fouad) and the fixed GENROU ratios of CliDefaults.
let xdpByBus = dict [ 1, 0.0608; 2, 0.1198; 3, 0.1813 ]
let genrou (g: GridSim.Core.Model.Generator) =
    let x = xdpByBus.[g.Bus]
    SubtransientInit.MachineData(g.H, 0.0, 3.0 * x, x, 0.8 * x, 2.5 * x, 1.5 * x, 0.8 * x, 8.0, 0.03, 0.4, 0.05)
let data = [ for g in model.Generators -> genrou g ]
let p = SubtransientInit.FromSolvedCase(model, pf, data, Nullable 7)
printfn "machine 1 (bus %d): H=%.15g s on 100 MVA" p.GeneratorBuses.[0] p.H.[0]
printfn "  delta0=%.15g rad (%.15g deg)" p.Delta0.[0] (p.Delta0.[0] * 180.0 / Math.PI)
printfn "  E'q=%.15g  E'd=%.15g  E''q=%.15g  E''d=%.15g" p.Eqp0.[0] p.Edp0.[0] p.Eqpp0.[0] p.Edpp0.[0]
printfn "  Efd=%.15g  Pm=%.15g" p.Efd.[0] p.Pm.[0]

// Undisturbed run: 2 s at 5 ms, no fault, on the prepared network at the case frequency. A true fixed point does not move.
let quiet = MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network, model.FrequencyHz, 0.005, 2.0)
let maxSpeed = quiet.Machines |> Seq.map (fun t -> t.SpeedPu |> Seq.map abs |> Seq.max) |> Seq.max
let maxAngle =
    quiet.Machines |> Seq.map (fun t -> t.DeltaRad |> Seq.map (fun d -> abs (d - t.DeltaRad.[0])) |> Seq.max) |> Seq.max
printfn "undisturbed 2 s: synchronism=%b max|speed|=%.3e pu max|delta-delta0|=%.3e rad" quiet.StayedInSynchronism maxSpeed maxAngle

// Flat reactances (every X = X'd): the seed collapses onto the classical one.
let flat = [ for g in model.Generators -> let x = xdpByBus.[g.Bus] in SubtransientInit.MachineData(g.H, 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05) ]
let pf0 = SubtransientInit.FromSolvedCase(model, pf, flat, Nullable 7)
printfn "flat reactances, machine 1: delta0=%.4f deg |E|=%.4f pu" (pf0.Delta0.[0] * 180.0 / Math.PI) pf0.Eqpp0.[0]

// The critical clearing time at bus 7, the CLI's own call.
let br = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7)
printfn "bus 7 CCT: stable to %.0f ms, lost at %.0f ms (%d trials)" (br.StableSeconds * 1000.0) (br.UnstableSeconds.Value * 1000.0) br.Trials
