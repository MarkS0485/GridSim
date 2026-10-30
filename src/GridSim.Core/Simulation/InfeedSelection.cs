using GridSim.Core.Model;

namespace GridSim.Core.Simulation;

/// <summary>
/// Picks the generator an operator "trip" should remove: the largest unit that is ACTUALLY GENERATING right now.
/// Shared by the WPF map (right-click / T key) and the web map (right-click / T key) so both shells trip the same
/// unit for the same frame.
/// <para>
/// The rule, in plain English:
/// <list type="bullet">
///   <item>Only live output counts (<see cref="Generator.Pg"/> &gt; 0). Nameplate (<see cref="Generator.MBase"/>)
///   is never used: on gb-full the national slack balancer carries a 266,425 MVA nameplate at 0 MW, and 1,072
///   voltage-control devices carry 2,880 MVA nameplates at 0 MW, none of which is an infeed that can be lost.</item>
///   <item>Power-flow-only devices (<see cref="Generator.PowerFlowOnly"/>: the slack balancer and voltage-control
///   units) are skipped even when the solve has given them output, because they are not physical plant. Tripping
///   one would inject the solver's balancing residual as a "loss of infeed".</item>
///   <item>Ties are broken by model order (the first unit in <see cref="GridModel.Generators"/> wins), so the
///   choice is deterministic for a given model.</item>
/// </list>
/// When nothing qualifies the result is <c>null</c>, and the caller must say so ("no generation to trip") rather
/// than fall back to some other figure.
/// </para>
/// </summary>
public static class InfeedSelection
{
    /// <summary>
    /// The largest generating, physical unit in <paramref name="model"/>, optionally restricted to one bus.
    /// </summary>
    /// <param name="model">The frame's model (its <see cref="Generator.Pg"/> is the live dispatch).</param>
    /// <param name="busId">Restrict the search to generators connected at this bus id; <c>null</c> searches the
    /// whole system.</param>
    /// <returns>The unit and its index in <see cref="GridModel.Generators"/>, or <c>null</c> when no unit at the
    /// requested scope is generating.</returns>
    public static (Generator Unit, int Index)? LargestOnline(GridModel model, int? busId = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        Generator? best = null;
        int bestIndex = -1;
        for (int i = 0; i < model.Generators.Count; i++)
        {
            Generator g = model.Generators[i];

            // Restrict to the requested bus, when one was given.
            if (busId is int id && g.Bus != id) continue;

            // Slack balancers and voltage-control devices are solver artefacts, not plant.
            if (g.PowerFlowOnly) continue;

            // Only a unit that is producing power right now is an infeed that can be lost. NaN fails this test
            // too, so a corrupt dispatch value can never be selected.
            if (!(g.Pg > 0.0)) continue;

            // Strictly greater: an equal later unit does not displace the earlier one (model order wins ties).
            if (best is null || g.Pg > best.Pg)
            {
                best = g;
                bestIndex = i;
            }
        }

        return best is null ? null : (best, bestIndex);
    }
}
