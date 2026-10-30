using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the WSCC / Anderson &amp; Fouad 3-machine 9-bus dynamic data on the physics
/// ground-truth case (<see cref="IeeeCases.Case9"/>). H is stored on each machine's own
/// MBase, so stored energy E = H*MBase; the system total (sum E / 1000) is the headline
/// inertia (GVA*s) that every RoCoF / frequency screen depends on.
/// </summary>
public class Case9InertiaTests
{
    // Published dynamic data: H (s) on the machine's own MBase (MVA).
    private const double H1 = 23.64, MBase1 = 247.5; // gen 1
    private const double H2 = 6.40,  MBase2 = 192.0; // gen 2
    private const double H3 = 3.01,  MBase3 = 128.0; // gen 3

    // Stored energy E = H*MBase (MW*s), and system total in GVA*s.
    private const double E1 = H1 * MBase1;               // ~5850.9
    private const double E2 = H2 * MBase2;               // ~1228.8
    private const double E3 = H3 * MBase3;               // ~385.28
    private const double TotalGvaS = (E1 + E2 + E3) / 1000.0; // ~7.465 GVA*s

    [Fact]
    public void Case9_system_inertia_matches_published_total()
    {
        var model = IeeeCases.Case9();
        Assert.Equal(TotalGvaS, model.SystemInertiaGvaS, 6);
    }

    [Fact]
    public void Case9_each_generator_stored_energy_matches_published_values()
    {
        var gens = IeeeCases.Case9().Generators;
        Assert.Equal(3, gens.Count);

        Assert.Equal(E1, gens[0].H * gens[0].MBase, 6);
        Assert.Equal(E2, gens[1].H * gens[1].MBase, 6);
        Assert.Equal(E3, gens[2].H * gens[2].MBase, 6);
    }

    [Fact]
    public void Case9_generator_H_and_MBase_are_the_published_dynamic_data()
    {
        var gens = IeeeCases.Case9().Generators;

        Assert.Equal(H1, gens[0].H, 6);
        Assert.Equal(MBase1, gens[0].MBase, 6);
        Assert.Equal(H2, gens[1].H, 6);
        Assert.Equal(MBase2, gens[1].MBase, 6);
        Assert.Equal(H3, gens[2].H, 6);
        Assert.Equal(MBase3, gens[2].MBase, 6);
    }

    [Fact]
    public void Case9_branch_thermal_ratings_match_matpower_rateA()
    {
        // Standard MATPOWER case9 rateA column (MVA), in branch order.
        double[] expectedRateA = { 250, 250, 150, 300, 150, 250, 250, 250, 250 };
        var branches = IeeeCases.Case9().Branches;

        Assert.Equal(expectedRateA.Length, branches.Count);
        for (int i = 0; i < expectedRateA.Length; i++)
            Assert.Equal(expectedRateA[i], branches[i].RateAMva, 6);
    }
}
