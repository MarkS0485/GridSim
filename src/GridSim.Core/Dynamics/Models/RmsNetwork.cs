using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using GridSim.Core.Protection;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The network an RMS run integrates against: a linear map from the machine internal EMF phasors <c>E</c>
/// (network frame, behind each machine's round <c>X''</c>) and the converter current injections <c>Ic</c> to
/// the machine currents <c>I_m</c> and the converter bus voltages <c>Vc</c>. The kernel
/// (<see cref="MultiMachineSubtransient"/>) needs nothing else from the network, so a dense Kron-reduced
/// admittance and a sparse full network are interchangeable.
/// </summary>
public interface IRmsNetwork
{
    /// <summary>Number of machines (EMFs).</summary>
    int Machines { get; }

    /// <summary>Number of converter injection nodes.</summary>
    int Converters { get; }

    /// <summary>Machine currents (injected into the network) and converter bus voltages.</summary>
    (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents);

    /// <summary>
    /// The same solve as <see cref="Solve"/>, written into caller-owned arrays instead of new ones (R115: the RMS
    /// kernel solves the network four times per RK step, once to commit, and up to 50 times in the converter
    /// iteration). The values are bit-identical to <see cref="Solve"/>'s. <paramref name="machineCurrents"/> must
    /// hold <see cref="Machines"/> entries and <paramref name="converterVoltages"/> <see cref="Converters"/>
    /// (<see cref="ArgumentException"/> otherwise); neither may be one of the inputs. This default copies the result
    /// of <see cref="Solve"/>, so a network that only implements <see cref="Solve"/> works unchanged; the built-in
    /// networks override it without allocating their outputs.
    /// </summary>
    void SolveInto(Complex[] emf, Complex[] converterCurrents, Complex[] machineCurrents, Complex[] converterVoltages)
    {
        ArgumentNullException.ThrowIfNull(machineCurrents);
        ArgumentNullException.ThrowIfNull(converterVoltages);
        (Complex[] im, Complex[] vc) = Solve(emf, converterCurrents);
        RmsNetworkBuffers.Check(im.Length, vc.Length, machineCurrents, converterVoltages);
        Array.Copy(im, machineCurrents, im.Length);
        Array.Copy(vc, converterVoltages, vc.Length);
    }
}

/// <summary>Shared argument checks for <see cref="IRmsNetwork.SolveInto"/>.</summary>
internal static class RmsNetworkBuffers
{
    public static void Check(int machines, int converters, Complex[] machineCurrents, Complex[] converterVoltages)
    {
        if (machineCurrents.Length != machines)
            throw new ArgumentException(
                $"machineCurrents holds {machineCurrents.Length} entries; the network has {machines} machines.", nameof(machineCurrents));
        if (converterVoltages.Length != converters)
            throw new ArgumentException(
                $"converterVoltages holds {converterVoltages.Length} entries; the network has {converters} converters.", nameof(converterVoltages));
    }
}

/// <summary>From <see cref="AtSeconds"/> on, the run integrates against <see cref="Network"/> (until the next
/// stage whose time has arrived) - a fault applied, cleared, a branch tripped.</summary>
public sealed record RmsNetworkStage(double AtSeconds, IRmsNetwork Network);

/// <summary>
/// The classic machine-only reduced admittance: <c>I_m = Y E</c>. The product is formed with exactly the
/// arithmetic of <see cref="SubtransientNetworkInterface"/>, so a run through this network is bit-for-bit the
/// historical kernel. Since R116 (October 2026) it runs on the vector units through
/// <see cref="MachineAdmittanceColumns"/>, which keeps each row's summation order and so every bit; the admittance is
/// copied at construction (the network multiplies by the matrix it was built with).
/// </summary>
public sealed class DenseMachineNetwork : IRmsNetwork
{
    private readonly Complex[,] _y;
    private readonly MachineAdmittanceColumns _columns;

    public DenseMachineNetwork(Complex[,] yReduced)
    {
        ArgumentNullException.ThrowIfNull(yReduced);
        if (yReduced.GetLength(0) != yReduced.GetLength(1)) throw new ArgumentException("yReduced must be square.");
        _y = yReduced;
        _columns = new MachineAdmittanceColumns(yReduced);
    }

    public int Machines => _y.GetLength(0);
    public int Converters => 0;

    public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents)
    {
        ArgumentNullException.ThrowIfNull(emf);
        if (emf.Length != Machines)
            throw new ArgumentException($"emf holds {emf.Length} entries; the network has {Machines} machines.", nameof(emf));
        var im = new Complex[emf.Length];
        _columns.MultiplyInto(emf, im);
        return (im, Array.Empty<Complex>());
    }

    /// <inheritdoc />
    public void SolveInto(Complex[] emf, Complex[] converterCurrents, Complex[] machineCurrents, Complex[] converterVoltages)
    {
        ArgumentNullException.ThrowIfNull(emf);
        ArgumentNullException.ThrowIfNull(machineCurrents);
        ArgumentNullException.ThrowIfNull(converterVoltages);
        RmsNetworkBuffers.Check(Machines, 0, machineCurrents, converterVoltages);
        if (emf.Length != Machines)
            throw new ArgumentException($"emf holds {emf.Length} entries; the network has {Machines} machines.", nameof(emf));
        if (ReferenceEquals(emf, machineCurrents))
            throw new ArgumentException("machineCurrents must not be the emf array (Y E reads every EMF for each row).", nameof(machineCurrents));
        _columns.MultiplyInto(emf, machineCurrents);
    }

    /// <summary><c>Y E</c> with the kernel's historical real-arithmetic order: the scalar reference that
    /// <see cref="MachineAdmittanceColumns"/> reproduces bit for bit (R116; kept for the tests).</summary>
    internal static Complex[] MultiplyMachine(Complex[,] y, Complex[] e)
    {
        var im = new Complex[e.Length];
        MultiplyMachineInto(y, e, im);
        return im;
    }

    /// <summary><see cref="MultiplyMachine"/> into a caller array (same loop, same order, same bits).</summary>
    internal static void MultiplyMachineInto(Complex[,] y, Complex[] e, Complex[] im)
    {
        int n = e.Length;
        for (int i = 0; i < n; i++)
        {
            double sr = 0.0, si = 0.0;
            for (int j = 0; j < n; j++)
            {
                double g = y[i, j].Real, b = y[i, j].Imaginary;
                sr += g * e[j].Real - b * e[j].Imaginary;
                si += g * e[j].Imaginary + b * e[j].Real;
            }
            im[i] = new Complex(sr, si);
        }
    }
}

/// <summary>
/// The full (unreduced) network as a sparse system - the RMS network at every size (N271, October 2026: it was the
/// large-case network only, gb-full, with a dense Kron reduction below 1,500 buses). Every machine is a
/// Norton source at its terminal bus: admittance <c>y_i = 1/(jX''_i)</c> on the diagonal and current
/// <c>E_i y_i</c> injected. Loads and held units are constant shunts, converters inject their currents at their
/// buses. One solve <c>Y V = sum E_i y_i + Ic</c> then gives every machine current
/// <c>I_i = (E_i - V_t,i) y_i</c> and every converter bus voltage. The network is linear, so this is exactly the
/// Kron-reduced model without forming it: it is factored once per stage (sparse LU) and each evaluation is one
/// back-substitution.
/// </summary>
public sealed class SparseRmsNetwork : IRmsNetwork
{
    private readonly int _n;
    private readonly FaultNetworkSystem _lu;
    private readonly int[] _terminal;
    private readonly Complex[] _yGen;
    private readonly int[] _convBus;

    // R115: a spare bus-sized right-hand side for SolveInto (56.6 KB on gb-full, allocated every solve before), and
    // (N060, S5 October 2026) a spare bus-sized solution vector, which the factorisation used to allocate on every
    // solve. Taken together with an atomic exchange and put back after the solve, so two threads solving the same
    // network at once never share them (the second simply allocates its own pair); a single-threaded run allocates
    // them once.
    private sealed class Scratch
    {
        public readonly Complex[] Rhs, V;
        public Scratch(int n) { Rhs = new Complex[n]; V = new Complex[n]; }
    }
    private Scratch? _spare;

    /// <param name="busCount">Network size.</param>
    /// <param name="triplets">Network admittance stamps (bus index pairs), including load and held-unit shunts,
    /// the machines' <c>1/(jX'')</c> on their terminals, and any fault shunt.</param>
    /// <param name="machineTerminal">Each machine's terminal bus index.</param>
    /// <param name="machineAdmittance">Each machine's <c>1/(jX'')</c> (system base).</param>
    /// <param name="converterBus">Each converter's bus index.</param>
    public SparseRmsNetwork(int busCount, IReadOnlyList<(int Row, int Col, Complex V)> triplets,
        int[] machineTerminal, Complex[] machineAdmittance, int[] converterBus)
    {
        ArgumentNullException.ThrowIfNull(triplets);
        _n = busCount;
        _terminal = machineTerminal ?? throw new ArgumentNullException(nameof(machineTerminal));
        _yGen = machineAdmittance ?? throw new ArgumentNullException(nameof(machineAdmittance));
        _convBus = converterBus ?? throw new ArgumentNullException(nameof(converterBus));
        if (_terminal.Length != _yGen.Length) throw new ArgumentException("One admittance per machine terminal.");
        // N233 (October 2026): FaultNetworkSystem is the sparse engine at every size; this network always forced the sparse
        // path (its old forceSparse argument defaulted to true and no caller passed false), so nothing here moves.
        _lu = FaultNetworkSystem.FromTriplets(busCount, triplets);
    }

    public int Machines => _terminal.Length;
    public int Converters => _convBus.Length;

    public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents)
    {
        var rhs = new Complex[_n];
        for (int i = 0; i < _terminal.Length; i++) rhs[_terminal[i]] += emf[i] * _yGen[i];
        for (int c = 0; c < _convBus.Length; c++) rhs[_convBus[c]] += converterCurrents[c];
        Complex[] v = _lu.Solve(rhs);
        var im = new Complex[_terminal.Length];
        for (int i = 0; i < _terminal.Length; i++) im[i] = (emf[i] - v[_terminal[i]]) * _yGen[i];
        var vc = new Complex[_convBus.Length];
        for (int c = 0; c < _convBus.Length; c++) vc[c] = v[_convBus[c]];
        return (im, vc);
    }

    /// <inheritdoc />
    /// <remarks>Exactly <see cref="Solve"/>'s arithmetic: a zeroed right-hand side (a cleared spare reads +0 as a
    /// new array does), the same accumulation order, and the same back-substitution, written into a reused solution
    /// vector by <c>FaultNetworkSystem.SolveInto</c> (bit-identical to its <c>Solve</c>; N060). On the sparse path
    /// nothing is allocated per solve.</remarks>
    public void SolveInto(Complex[] emf, Complex[] converterCurrents, Complex[] machineCurrents, Complex[] converterVoltages)
    {
        ArgumentNullException.ThrowIfNull(emf);
        ArgumentNullException.ThrowIfNull(converterCurrents);
        ArgumentNullException.ThrowIfNull(machineCurrents);
        ArgumentNullException.ThrowIfNull(converterVoltages);
        RmsNetworkBuffers.Check(_terminal.Length, _convBus.Length, machineCurrents, converterVoltages);
        if (ReferenceEquals(emf, machineCurrents))
            throw new ArgumentException("machineCurrents must not be the emf array.", nameof(machineCurrents));

        Scratch scratch = Interlocked.Exchange(ref _spare, null) ?? new Scratch(_n);
        try
        {
            Complex[] rhs = scratch.Rhs;
            Array.Clear(rhs);
            for (int i = 0; i < _terminal.Length; i++) rhs[_terminal[i]] += emf[i] * _yGen[i];
            for (int c = 0; c < _convBus.Length; c++) rhs[_convBus[c]] += converterCurrents[c];
            Complex[] v = scratch.V;
            _lu.SolveInto(rhs, v);
            for (int i = 0; i < _terminal.Length; i++) machineCurrents[i] = (emf[i] - v[_terminal[i]]) * _yGen[i];
            for (int c = 0; c < _convBus.Length; c++) converterVoltages[c] = v[_convBus[c]];
        }
        finally
        {
            Volatile.Write(ref _spare, scratch);
        }
    }

    /// <summary>Number of buses of the network (the length <see cref="SolveBusVoltagesInto"/> writes).</summary>
    public int Buses => _n;

    /// <summary>
    /// Every bus voltage of the solve <see cref="SolveInto"/> makes for the same <paramref name="emf"/> and
    /// <paramref name="converterCurrents"/>: the same right-hand side, accumulated in the same order, and the same
    /// factorisation, written into <paramref name="busVoltages"/> (one entry per bus, in the network's bus order). For
    /// measurement only (inertia placement study, October 2026): it uses its own right-hand side, never the spare
    /// buffers, so it changes nothing a later <see cref="SolveInto"/> computes.
    /// </summary>
    public void SolveBusVoltagesInto(Complex[] emf, Complex[] converterCurrents, Complex[] busVoltages)
    {
        ArgumentNullException.ThrowIfNull(emf);
        ArgumentNullException.ThrowIfNull(converterCurrents);
        ArgumentNullException.ThrowIfNull(busVoltages);
        if (emf.Length != _terminal.Length)
            throw new ArgumentException($"emf holds {emf.Length} entries; the network has {_terminal.Length} machines.", nameof(emf));
        if (converterCurrents.Length != _convBus.Length)
            throw new ArgumentException($"converterCurrents holds {converterCurrents.Length} entries; the network has {_convBus.Length} converters.", nameof(converterCurrents));
        if (busVoltages.Length != _n)
            throw new ArgumentException($"busVoltages holds {busVoltages.Length} entries; the network has {_n} buses.", nameof(busVoltages));
        var rhs = new Complex[_n];
        for (int i = 0; i < _terminal.Length; i++) rhs[_terminal[i]] += emf[i] * _yGen[i];
        for (int c = 0; c < _convBus.Length; c++) rhs[_convBus[c]] += converterCurrents[c];
        _lu.SolveInto(rhs, busVoltages);
    }
}
