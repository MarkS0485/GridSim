using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>How the busbars are placed: by the automatic layered layout, or from the case's own x and y (hand placed, or adopted from the automatic layout).</summary>
public enum SldLayoutMode { Auto, Case }

/// <summary>A diagram ready to show: the model, its geometry, the layout actually used (a request for the case layout on a case that has none falls back to automatic, said in <see cref="Note"/>).</summary>
public sealed record SldDiagram(SldModel Model, SldGeometry Geometry, SldLayoutMode Mode, string Note)
{
    /// <summary>True when busbars can be moved by writing the case's layout: the diagram is placed from the case.</summary>
    public bool CanMove => Mode == SldLayoutMode.Case;
}

/// <summary>The one entry point both clients call to make a diagram, so the Avalonia view and the web page draw the same geometry from the same model.</summary>
public static class SldDiagrams
{
    public static SldDiagram Build(SldModel model, SldLayoutMode mode, SldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        SldOptions o = options ?? new SldOptions();
        if (mode == SldLayoutMode.Case)
        {
            SldPlacement? p = SldGeometryBuilder.PlaceFromCase(model, o);
            if (p is not null) return new SldDiagram(model, SldGeometryBuilder.Build(model, p, o), SldLayoutMode.Case, "Placed from the case's own layout coordinates.");
            return new SldDiagram(model, SldGeometryBuilder.Build(model, SldAutoLayout.Place(model, o), o), SldLayoutMode.Auto,
                model.Grid is null ? "This source carries no layout; the diagram is laid out automatically." : "The case has no layout coordinates (every bus at the origin); the diagram is laid out automatically. Adopt the layout to edit it.");
        }
        return new SldDiagram(model, SldGeometryBuilder.Build(model, SldAutoLayout.Place(model, o), o), SldLayoutMode.Auto, "Laid out automatically from the model.");
    }
}

/// <summary>Result colouring input made from a power flow of the diagram's own model (the same solve the workbench's power-flow study makes).</summary>
public static class SldResults
{
    /// <summary>Solve the bus-branch model of <paramref name="model"/> and give the colouring input; an unconverged solve (or a node-breaker diagram) gives an input with no voltages and no loading, which colours grey.</summary>
    public static SldColourInput PowerFlow(SldModel model, SldColourInput? withAreas = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Grid is not { } grid) return new SldColourInput { NodeArea = withAreas?.NodeArea, AreaNames = withAreas?.AreaNames ?? [] };
        PowerFlowResult r = SolverSelector.CreateNewton(grid).Solve(grid, ContingencyAnalysis.StudyOptions);
        return SldColourInput.FromPowerFlow(model, r, withAreas);
    }
}
