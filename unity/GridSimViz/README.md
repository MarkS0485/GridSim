# GridSimViz  -  Unity visualiser

An interactive view of a GridSim grid: buses coloured by voltage, branches as
lines, animated across a simulated day, with play/pause/scrub/zoom and
click-to-inspect.

It is **decoupled** from the simulator  -  GridSim (net10) exports a JSON document and
Unity reads it. So Unity needs no .NET dependency and any reasonably recent Unity
version works.

---

## Run it

1. **Export the data** from the GridSim repo root:
   ```bash
   dotnet run --project src/GridSim.Cli export unity/GridSimViz/Assets/StreamingAssets/gridviz.json --frames 96
   ```
   (A copy is already committed, so you can skip this the first time.)

2. **Open** `unity/GridSimViz` in Unity Hub (built with 2022.3 LTS; newer offers to
   upgrade  -  fine).

3. Press **Play**. The grid bootstraps itself (no scene setup needed)  -  a manager
   spawns via `[RuntimeInitializeOnLoadMethod]`, loads
   `Assets/StreamingAssets/gridviz.json`, and draws the network.

---

## Controls

| Action | Control |
|---|---|
| Pan | drag with **right** or **middle** mouse |
| Zoom | **scroll** wheel |
| Inspect a bus | **left-click** it (panel shows Vm/Va/P/Q) |
| Play / pause | on-screen button |
| Scrub time | the frame slider |
| Speed | **-/+** buttons |

Nodes are coloured green->yellow->red by how far their voltage strays from 1.0 p.u.;
400 kV buses are drawn larger than 132 kV, and transformer branches are orange.

---

## How it fits together

```
GridSim.Cli export  gridviz.json (scene + per-frame solved snapshots)
                            StreamingAssets/

GridViz.cs (MonoBehaviour)  nodes + lines, animated, interactive
```

- `Assets/Scripts/GridVizData.cs`  -  `[Serializable]` mirrors of the JSON.
- `Assets/Scripts/GridViz.cs`  -  loads, builds geometry, animates, handles input/UI.
- `Assets/Scripts/GridVizBootstrap.cs`  -  auto-spawns the manager on Play.
- `Assets/Scripts/HeatColor.cs`  -  voltage -> colour.

**Render pipeline:** written to work under Built-in or URP (it picks the first available
unlit shader: `Universal Render Pipeline/Unlit`, `Unlit/Color`, `Sprites/Default`, then
`Standard`). Nodes that render untextured or pink mean none of those shaders is in the
build; add one of them to the project's Always Included Shaders (Project Settings,
Graphics), or report the issue with the render pipeline and Unity version in use.

**Converged frames only:** each exported frame carries a `converged` flag. A frame whose
power flow did not converge is not a network state, so the viewer draws every bus grey and
the panel reads `NOT CONVERGED`. An export written before the flag existed has no such key;
the viewer then draws it as before and shows `(export has no convergence flag)`. Re-export
to get the flag.

> Status: the Unity project has not yet been opened and checked in a Unity Editor, so the
> first open is a shakedown. The GridSim export side is covered by the test suite; the
> Unity scripts are not.
