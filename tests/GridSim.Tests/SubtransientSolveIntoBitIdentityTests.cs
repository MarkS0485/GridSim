using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R115: the subtransient RMS step solves the network into reused buffers (<see cref="IRmsNetwork.SolveInto"/>) and
/// builds its EMF phasors into reused buffers, instead of allocating every array on every solve (4 RK stages, a commit
/// and up to 50 converter iterations per step). The digests below are SHA-256 over every sample of every trace
/// (machine angle, speed and the four fluxes; converter voltage, P, Q, Ip, Iq and dip flag) plus the synchronism
/// verdict and the converter-iteration failure count, RECORDED FROM THE ALLOCATING CODE BEFORE THE CHANGE (1.95.073),
/// on the dense and the sparse network, with and without converter plant and with and without field controllers. Any
/// change to any bit of any sample fails them.
/// </summary>
public class SubtransientSolveIntoBitIdentityTests
{
    private static readonly Dictionary<int, double> Xdp = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static (GridModel M, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Case9(bool battery)
    {
        GridModel b = IeeeCases.Case9();
        var m = new GridModel
        {
            BaseMva = b.BaseMva, Buses = b.Buses, Branches = b.Branches, Name = b.Name, FrequencyHz = b.FrequencyHz,
            Generators = b.Generators.Select(g => battery && g.Bus == 3 ? g with { Type = "battery", Name = "BESS" } : g).ToList(),
        };
        var data = m.Generators.Select(g =>
        {
            double x = Xdp[g.Bus];
            return new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
        }).ToList();
        return (m, new NewtonRaphsonPowerFlow().Solve(m), data);
    }

    private static string Digest(MultiMachineSubtransient.Result r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(r.StayedInSynchronism);
            w.Write(r.ConverterIterationFailures);
            foreach (MultiMachineSubtransient.MachineTrace t in r.Machines)
                foreach (IReadOnlyList<double> s in new[] { t.TimeSeconds, t.DeltaRad, t.SpeedPu, t.EqpPu, t.EdpPu, t.EqppPu, t.EddpPu })
                {
                    w.Write(s.Count);
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
                }
            foreach (RmsConverterTrace c in r.Converters ?? Array.Empty<RmsConverterTrace>())
            {
                foreach (IReadOnlyList<double> s in new[] { c.TerminalVoltagePu, c.ActivePowerPu, c.ReactivePowerPu, c.ActiveCurrentPu, c.ReactiveCurrentPu })
                {
                    w.Write(s.Count);
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
                }
                foreach (bool b in c.InDipMode) w.Write(b);
            }
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static IReadOnlyList<IRmsFieldController?>? Controllers(int n, bool on) =>
        on ? Enumerable.Range(0, n).Select(_ => (IRmsFieldController?)RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05,
            fieldMinPu: 0.0, fieldMaxPu: 8.0, stabiliser: null)).ToList() : null;

    private static MultiMachineSubtransient.Result Run(bool sparse, bool battery, bool avr)
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9(battery);
        SubtransientInit.Prepared p;
        // N271: sparse is the production path; the dense rows run on the dense Kron test oracle (was sparseNetwork: false).
        // Same networks, so every digest below is unchanged.
        using (sparse ? null : SubtransientInit.DenseKronReferenceScope()) p = SubtransientInit.FromSolvedCase(m, pf, data, faultBusId: 7);
        int n = p.Machines.Count;
        if (!sparse)
        {
            var stages = new List<MultiMachineSubtransient.NetworkStage> { new(0.1, p.YReducedFaulted!), new(0.2, p.YReduced) };
            return MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0,
                p.Eqpp0, p.Edpp0, p.YReduced, 50.0, 0.005, 1.0, admittanceStages: stages, converters: p.Converters,
                fieldControllers: Controllers(n, avr));
        }
        var sStages = new List<RmsNetworkStage> { new(0.1, p.NetworkFaulted!), new(0.2, p.Network!) };
        return MultiMachineSubtransient.Simulate(p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0,
            p.Eqpp0, p.Edpp0, p.Network!, 50.0, 0.005, 1.0, stages: sStages, converters: p.Converters,
            fieldControllers: Controllers(n, avr));
    }

    [Theory]
    // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the six
    // trajectory digests moved because the subtransient machines, converters and network interface now use GridSim's
    // deterministic fdlibm sin / cos / atan2 instead of the Windows C runtime. Before -> after: 2E7EF479... -> 683C7572...,
    // B0E17941... -> 53163F4F..., A3AB9C05... -> 2D35A7C2..., B81C2D16... -> A26160E5..., 9FA727CF... -> D0E5E11C...,
    // 82ACBD92... -> 0A57D46A... (hashes abbreviated). The dense-vs-sparse
    // SolveInto identity tests below are unchanged.
    [InlineData(false, false, false, "683C75728106D49C0A5F8E026D97670AEEF82578E5A5A5E148A61B7754CBF5F1")]
    [InlineData(false, true, false, "53163F4FEEF55393E1E890103E8153CAFA50DC04EA6C52CCC2267AD63DA46C0F")]
    [InlineData(false, false, true, "2D35A7C248FCD29A18535033B438B6BB3FDB597F8A7608D822477EF682963E56")]
    [InlineData(true, false, false, "A26160E537E339C40FEF47ADEE05A839B1F5E871E707F6C2460F06AE52F00A7A")]
    [InlineData(true, true, false, "D0E5E11C1F43DFBDEE3194DBD2FF011E404E8B76E2C527B62D6F623D04587B6B")]
    [InlineData(true, true, true, "0A57D46A2F14747169156924DA2380FFE39F40A8E2BEBE64754593E815A3A37B")]
    public void Trajectories_are_bit_identical_to_the_allocating_kernel(bool sparse, bool battery, bool avr, string digest)
    {
        MultiMachineSubtransient.Result r = Run(sparse, battery, avr);
        Assert.True(r.Machines[0].DeltaRad.Count > 100);
        if (battery) Assert.NotNull(r.Converters);
        string actual = Digest(r);
        Assert.True(digest == actual, $"trajectory digest {actual}, expected {digest}");
    }

    [Fact]
    public void A_second_run_on_the_same_network_instances_is_identical()
    {
        // The sparse network now keeps a reusable right-hand side; a run must not leave state behind in it.
        Assert.Equal(Digest(Run(true, true, false)), Digest(Run(true, true, false)));
    }

    [Fact]
    public void The_default_SolveInto_of_a_custom_network_copies_its_Solve_result_and_checks_lengths()
    {
        var y = new Complex[,] { { new(1, -2), new(0.5, 0.25) }, { new(0.5, 0.25), new(2, -1) } };
        IRmsNetwork custom = new WrappingNetwork(new DenseMachineNetwork(y));
        Complex[] e = { new(1.0, 0.1), new(0.9, -0.2) };
        var im = new Complex[2];
        custom.SolveInto(e, Array.Empty<Complex>(), im, Array.Empty<Complex>());
        Assert.Equal(new DenseMachineNetwork(y).Solve(e, Array.Empty<Complex>()).MachineCurrents, im);
        Assert.Throws<ArgumentException>(() => custom.SolveInto(e, Array.Empty<Complex>(), new Complex[1], Array.Empty<Complex>()));
        Assert.Throws<ArgumentException>(() => custom.SolveInto(e, Array.Empty<Complex>(), im, new Complex[1]));
    }

    [Fact]
    public void Dense_and_sparse_SolveInto_equal_their_Solve_bit_for_bit()
    {
        (GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9(battery: true);
        SubtransientInit.Prepared sp = SubtransientInit.FromSolvedCase(m, pf, data, faultBusId: 7);   // N271: always sparse
        IRmsNetwork net = sp.Network!;
        Complex[] e = Enumerable.Range(0, net.Machines).Select(i => Complex.FromPolarCoordinates(1.0 + 0.01 * i, 0.1 * i)).ToArray();
        Complex[] ic = Enumerable.Range(0, net.Converters).Select(i => new Complex(0.3, -0.1 * i)).ToArray();
        (Complex[] im0, Complex[] vc0) = net.Solve(e, ic);
        var im = new Complex[net.Machines];
        var vc = new Complex[net.Converters];
        net.SolveInto(e, ic, im, vc);
        net.SolveInto(e, ic, im, vc);   // a second call through the reused right-hand side
        Assert.Equal(im0, im);
        Assert.Equal(vc0, vc);

        var dense = new DenseMachineNetwork(new Complex[,] { { new(1, -2), new(0.5, 0.25) }, { new(0.5, 0.25), new(2, -1) } });
        Complex[] e2 = { new(1.0, 0.1), new(0.9, -0.2) };
        var im2 = new Complex[2];
        dense.SolveInto(e2, Array.Empty<Complex>(), im2, Array.Empty<Complex>());
        Assert.Equal(dense.Solve(e2, Array.Empty<Complex>()).MachineCurrents, im2);
    }

    /// <summary>An implementation that only has <see cref="IRmsNetwork.Solve"/>, as a third-party network would.</summary>
    private sealed class WrappingNetwork(IRmsNetwork inner) : IRmsNetwork
    {
        public int Machines => inner.Machines;
        public int Converters => inner.Converters;
        public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents) =>
            inner.Solve(emf, converterCurrents);
    }
}
