using System;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the salient/subtransient multi-machine network interface
/// (<see cref="SubtransientNetworkInterface"/>) against oracles at the machine-epsilon standard:
///   - REDUCTION TO THE ROUND-ROTOR FORM: with every E''d = 0 the air-gap power and the recovered d-axis
///     current agree field-for-field with the trusted <see cref="MultiMachineFluxDecay.NetworkInjection"/>;
///   - POWER CONSERVATION: on a lossless reduced network the per-machine air-gap powers sum to zero;
///   - RECOVERED CURRENTS: Pe_i = E''q_i*Iq_i + E''d_i*Id_i (the internal power identity);
///   - DETERMINISM: two identical calls are bit-for-bit equal.
/// </summary>
public class SubtransientNetworkInterfaceTests
{
    // A small lossy reduced admittance (series links with R and X, plus a shunt to ground per node).
    private static Complex[,] LossyReduced()
    {
        int n = 3;
        var y = new Complex[n, n];
        void Link(int i, int j, double r, double x)
        {
            Complex ys = Complex.One / new Complex(r, x);
            y[i, i] += ys; y[j, j] += ys; y[i, j] -= ys; y[j, i] -= ys;
        }
        Link(0, 1, 0.02, 0.20);
        Link(1, 2, 0.03, 0.25);
        Link(0, 2, 0.01, 0.30);
        y[0, 0] += new Complex(0.0, -3.0);   // X'' shunts to ground (folded), purely reactive
        y[1, 1] += new Complex(0.0, -4.0);
        y[2, 2] += new Complex(0.0, -5.0);
        return y;
    }

    // A lossless reduced admittance (purely imaginary, symmetric) - series reactances plus reactive shunts.
    private static Complex[,] LosslessReduced()
    {
        int n = 3;
        var y = new Complex[n, n];
        void Link(int i, int j, double b)
        {
            Complex ys = new(0.0, -b);       // inductive series admittance
            y[i, i] += ys; y[j, j] += ys; y[i, j] -= ys; y[j, i] -= ys;
        }
        Link(0, 1, 5.0);
        Link(1, 2, 4.0);
        Link(0, 2, 3.0);
        y[0, 0] += new Complex(0.0, -3.0);
        y[1, 1] += new Complex(0.0, -4.0);
        y[2, 2] += new Complex(0.0, -5.0);
        return y;
    }

    // ---- Reduction to the round-rotor form: E''d = 0 => Pe and Id equal MultiMachineFluxDecay ----
    [Fact]
    public void With_no_q_axis_emf_it_reduces_to_the_round_rotor_injection()
    {
        var eqpp = new[] { 1.10, 1.05, 1.20 };
        var edpp = new[] { 0.0, 0.0, 0.0 };
        var delta = new[] { 0.20, -0.15, 0.35 };
        Complex[,] y = LossyReduced();

        (double[] pe, double[] id, _) = SubtransientNetworkInterface.Inject(eqpp, edpp, delta, y);
        (double[] peRef, double[] idRef) = MultiMachineFluxDecay.NetworkInjection(eqpp, delta, y);

        for (int i = 0; i < eqpp.Length; i++)
        {
            Assert.Equal(peRef[i], pe[i], 12);       // air-gap power matches the trusted round-rotor form
            Assert.Equal(idRef[i], id[i], 12);       // recovered d-axis current matches Id = Qe/|E'|
        }
    }

    // ---- Lossless network: the per-machine air-gap powers sum to zero (machines only exchange power) ----
    [Fact]
    public void On_a_lossless_network_the_powers_sum_to_zero()
    {
        var eqpp = new[] { 1.10, 1.05, 1.20 };
        var edpp = new[] { 0.15, -0.10, 0.20 };       // genuine q-axis EMFs (saliency present)
        var delta = new[] { 0.20, -0.15, 0.35 };

        (double[] pe, _, _) = SubtransientNetworkInterface.Inject(eqpp, edpp, delta, LosslessReduced());
        double sum = 0.0;
        foreach (double p in pe) sum += p;
        Assert.Equal(0.0, sum, 12);
    }

    // ---- Internal power identity: Pe_i = E''q_i*Iq_i + E''d_i*Id_i ----
    [Fact]
    public void Air_gap_power_matches_the_internal_emf_current_product()
    {
        var eqpp = new[] { 1.10, 0.95, 1.15 };
        var edpp = new[] { 0.20, 0.10, -0.05 };
        var delta = new[] { 0.10, -0.20, 0.30 };

        (double[] pe, double[] id, double[] iq) = SubtransientNetworkInterface.Inject(eqpp, edpp, delta, LossyReduced());
        for (int i = 0; i < eqpp.Length; i++)
            Assert.Equal(eqpp[i] * iq[i] + edpp[i] * id[i], pe[i], 12);
    }

    // ---- Terminal voltage recovery: with X'' = 0 it is the internal EMF magnitude ----
    [Fact]
    public void Terminal_voltage_with_zero_subtransient_reactance_is_the_internal_emf()
    {
        var eqpp = new[] { 1.10, 1.05, 1.20 };
        var edpp = new[] { 0.15, -0.10, 0.20 };
        var delta = new[] { 0.20, -0.15, 0.35 };
        var xpp = new[] { 0.0, 0.0, 0.0 };

        double[] vt = SubtransientNetworkInterface.TerminalVoltageMagnitudes(eqpp, edpp, delta, LossyReduced(), xpp);
        for (int i = 0; i < eqpp.Length; i++)
            Assert.Equal(Math.Sqrt(eqpp[i] * eqpp[i] + edpp[i] * edpp[i]), vt[i], 12);   // V_t = |E''|
    }

    // ---- Determinism: two identical calls are bit-for-bit equal ----
    [Fact]
    public void Two_identical_calls_are_bit_for_bit_equal()
    {
        var eqpp = new[] { 1.10, 0.95, 1.15 };
        var edpp = new[] { 0.20, 0.10, -0.05 };
        var delta = new[] { 0.10, -0.20, 0.30 };
        Complex[,] y = LossyReduced();

        (double[] peA, double[] idA, double[] iqA) = SubtransientNetworkInterface.Inject(eqpp, edpp, delta, y);
        (double[] peB, double[] idB, double[] iqB) = SubtransientNetworkInterface.Inject(eqpp, edpp, delta, y);
        for (int i = 0; i < eqpp.Length; i++)
        {
            Assert.True(peA[i] == peB[i]);
            Assert.True(idA[i] == idB[i]);
            Assert.True(iqA[i] == iqB[i]);
        }
    }
}
