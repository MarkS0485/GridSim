// Hand-copied test oracles (not generated at build time).
// Stream P (October 2026) test oracles: the 1.95.225 code of ClassicalInit.FromSolvedCase and of DomainCoupling's
// Synchronising / ImpactShares (with their private helpers), copied VERBATIM from the source as it stood before the
// stream P change, with only the names qualified so they compile here (ClassicalInit.X, DomainCoupling.X) and the two
// public entry points renamed Old*. They run the dense YBus, the dense augmented matrix and the dense KronReduction, the
// path the sparse code replaced, so the tests can compare the two field for field, bit for bit.
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using DetMath = GridSim.Core.Numerics.DetMath;

namespace GridSim.Tests;

internal static class PPerformanceOracles
{
    public static ClassicalInit.Prepared OldFromSolvedCase(GridModel model, PowerFlowResult result, double[]? xdpMachineBase = null,
        int? faultBusId = null, bool holdNonMachines = false)
    {
        if (!result.Converged) throw new InvalidOperationException("Classical init needs a converged power flow.");
        int g = model.Generators.Count;
        if (g == 0) throw new InvalidOperationException("No generators to initialise.");
        if (xdpMachineBase is not null && xdpMachineBase.Length != g)
            throw new ArgumentException("xdpMachineBase must align to model.Generators.", nameof(xdpMachineBase));

        // R018 / N021 / N153 (October 2026): the two "no rotor to integrate" failures below (no machine at all when
        // holding non-machines, or a machine with no inertia constant) used to surface only AFTER the dense YBus and the
        // dense (n + g)^2 augmented admittance had been built and filled (1,456,645,640 B per call on gb-full), only to
        // be thrown away. Ruling R1 (Mark delegated 2026-10-04, N153): fail BEFORE the dense build. ClassicalInit.CertainNoRotorFailure
        // returns a message only when every step the main path takes before those throws provably succeeds, and the
        // message is exactly the one the main path would throw, so the outcome of every call is unchanged (same exception
        // type, same text); only the wasted allocation goes. This also spares the direct callers (the CLI `experimental`
        // verb, small-signal, the CCT search), which paid the ~1.46 GB build on gb-full before failing.
        if (ClassicalInit.CertainNoRotorFailure(model, result, holdNonMachines) is string noRotor)
            throw new InvalidOperationException(noRotor);

        double baseMva = model.BaseMva;
        YBus ybus = YBus.Build(model);
        int n = model.BusCount;

        // Bus voltage (pu, complex) and SOLVED net injection (generation - demand, MW/MVAr) by bus id.
        var vByBus = new Dictionary<int, Complex>(result.BusIds.Count);
        var pInjByBus = new Dictionary<int, double>(result.BusIds.Count);
        var qInjByBus = new Dictionary<int, double>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count; i++)
        {
            vByBus[result.BusIds[i]] = DetMath.FromPolar(result.Vm[i], result.VaDeg[i] * Math.PI / 180.0);
            pInjByBus[result.BusIds[i]] = result.PInjMw[i];
            qInjByBus[result.BusIds[i]] = result.QInjMvar[i];
        }

        // Scheduled generation per bus, to split the SOLVED bus output across co-located machines by their
        // scheduled share (equal split when the bus's scheduled generation is ~0). Canonical validation
        // cases are one-machine-per-bus, so the share is 1.0 and each machine gets its exact solved output.
        var schedPByBus = new Dictionary<int, double>();
        var genCountByBus = new Dictionary<int, int>();
        var loadByBus = new Dictionary<int, (double Pd, double Qd)>();
        foreach (Bus bb in model.Buses) loadByBus[bb.Id] = (bb.Pd, bb.Qd);
        foreach (Generator gg in model.Generators)
        {
            schedPByBus[gg.Bus] = schedPByBus.GetValueOrDefault(gg.Bus) + gg.Pg;
            genCountByBus[gg.Bus] = genCountByBus.GetValueOrDefault(gg.Bus) + 1;
        }

        // Which generators are machines; the rest (when holding) become constant admittances below.
        var machineIdx = new List<int>(g);
        var heldIdx = new List<int>();
        for (int k = 0; k < g; k++)
        {
            Generator x = model.Generators[k];
            if (holdNonMachines && ClassicalInit.IsNonMachine(x)) heldIdx.Add(k); else machineIdx.Add(k);
        }
        if (machineIdx.Count == 0)
            throw new InvalidOperationException(ClassicalInit.NoMachinesMessage);
        int gm = machineIdx.Count;

        // Augmented admittance: n network buses then one internal node per machine.
        int m = n + gm;
        var ya = new Complex[m, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                ya[i, j] = ybus.Y[i, j];

        // Loads -> constant shunt admittance on the bus diagonal.
        for (int i = 0; i < n; i++)
        {
            Bus bus = model.Buses[i];
            if (bus.Pd == 0 && bus.Qd == 0) continue;
            Complex v = vByBus.TryGetValue(bus.Id, out Complex vv) ? vv : Complex.One;
            double v2 = Math.Max(ClassicalInit.MinVoltageSquaredPu, v.Magnitude * v.Magnitude);
            Complex sLoadPu = new Complex(bus.Pd, bus.Qd) / baseMva;      // consumed
            ya[i, i] += Complex.Conjugate(sLoadPu) / v2;                  // y = conj(S)/|V|^2
        }

        var h = new double[gm];
        var eq = new double[gm];
        var delta0 = new double[gm];
        var genBuses = new int[gm];
        var keep = new int[gm];

        // A unit's share of its bus's SOLVED generation (split across co-located units by scheduled share).
        Complex SolvedShare(Generator gen)
        {
            (double pdBus, double qdBus) = loadByBus.GetValueOrDefault(gen.Bus);
            double busGenP = pInjByBus.GetValueOrDefault(gen.Bus) + pdBus;
            double busGenQ = qInjByBus.GetValueOrDefault(gen.Bus) + qdBus;
            double schedP = schedPByBus.GetValueOrDefault(gen.Bus);
            double share = Math.Abs(schedP) > ClassicalInit.MinScheduledShareMw ? gen.Pg / schedP : 1.0 / Math.Max(1, genCountByBus.GetValueOrDefault(gen.Bus, 1));
            return new Complex(busGenP * share, busGenQ * share) / baseMva;
        }

        // Held units: their solved injection as a constant (negative-load) admittance at their bus.
        foreach (int kh in heldIdx)
        {
            Generator gen = model.Generators[kh];
            int bi = ybus.IndexOf(gen.Bus);
            Complex vt = vByBus.TryGetValue(gen.Bus, out Complex vh) ? vh : Complex.One;
            double v2 = Math.Max(ClassicalInit.MinVoltageSquaredPu, vt.Magnitude * vt.Magnitude);
            ya[bi, bi] -= Complex.Conjugate(SolvedShare(gen)) / v2;
        }

        for (int k = 0; k < gm; k++)
        {
            Generator gen = model.Generators[machineIdx[k]];

            // The classical swing divides by 2*H; a zero/negative/non-finite inertia constant makes every
            // step 0/0 -> NaN, which the synchronism test can no longer distinguish from stability (see the
            // ClassicalMultiMachine guard). Reject it here - fail loud rather than emit confidently-wrong
            // clearing times. MATPOWER .m cases carry no H (the loader defaults H=0), so they legitimately
            // cannot be screened without dynamic data.
            if (!(gen.H > 0.0))
                throw new InvalidOperationException(ClassicalInit.NoInertiaMessage(gen));

            genBuses[k] = gen.Bus;
            int ti = ybus.IndexOf(gen.Bus);
            int intNode = n + k;
            keep[k] = intNode;

            // D-DOC-055 (docs-session finding, Ruling R1: Mark delegated 2026-10-04): the machine base comes from the
            // one shared rule, MachineBase.EffectiveMva - a positive MBase is the base, a zero or negative MBase means
            // "not given" and the system base is used (as SubtransientInit and the MATPOWER parser already did), a NaN
            // or infinite MBase is refused. Before, this line floored the divisor at 1e-6 MVA (a zero MBase made X'd on
            // the system base 1e8 times the machine value) and the H line below scaled by the raw MBase (a zero MBase
            // gave H = 0 on the system base, which the swing divides by). For every positive MBase of at least 1e-6 MVA
            // (every machine any bundled case reaches here) both values are bit for bit what they were.
            double mBase = GridSim.Core.Dynamics.Models.MachineBase.EffectiveMva(gen, baseMva);
            double xdpMachine = xdpMachineBase?[machineIdx[k]] ?? ClassicalInit.DefaultTransientReactancePu;
            double xdpSys = xdpMachine * baseMva / mBase;
            Complex yGen = Complex.One / new Complex(0.0, xdpSys);        // 1/(jX'd) = -j/X'd

            ya[ti, ti] += yGen;
            ya[intNode, intNode] += yGen;
            ya[ti, intNode] -= yGen;
            ya[intNode, ti] -= yGen;

            // Internal EMF from the terminal condition: E' = V_t + jX'd*I_t, I_t = conj(S_gen/V_t).
            // S_gen is the SOLVED generator output - the slack picks up losses and PV-bus Qg is set by the
            // solve, so the scheduled Pg/Qg would build the EMF at a shifted dispatch. Solved bus generation
            // = net injection + local load; split across co-located machines by scheduled share.
            Complex vt = vByBus.TryGetValue(gen.Bus, out Complex vg) ? vg : Complex.One;
            Complex sGenPu = SolvedShare(gen);
            Complex it = Complex.Conjugate(sGenPu / vt);
            Complex ePrime = vt + new Complex(0.0, xdpSys) * it;
            eq[k] = ePrime.Magnitude;
            delta0[k] = DetMath.Phase(ePrime);

            h[k] = gen.H * mBase / baseMva;                              // H on the system base (same base rule as X'd)
        }

        Complex[,] yRed = KronReduction.Reduce(ya, keep);

        // Optional bolted three-phase fault: clamp the faulted bus with a large shunt admittance before
        // reducing, so the reduced network during the fault has a collapsed transfer to that bus (Pe ~ 0
        // for machines close to it). Cleared by reverting to yRed.
        Complex[,]? yRedFault = null;
        if (faultBusId is int fb)
        {
            int fi = ybus.IndexOf(fb);
            var yaF = (Complex[,])ya.Clone();
            yaF[fi, fi] += new Complex(ClassicalInit.BoltedFaultAdmittancePu, 0.0);   // near short-circuit to ground
            yRedFault = KronReduction.Reduce(yaF, keep);
        }

        // Mechanical power = electrical output at delta0, so an undisturbed case is a fixed point.
        var pm = new double[gm];
        for (int i = 0; i < gm; i++)
        {
            double pe = eq[i] * eq[i] * yRed[i, i].Real;
            for (int j = 0; j < gm; j++)
            {
                if (j == i) continue;
                double dij = delta0[i] - delta0[j];
                (double sinD, double cosD) = DetMath.SinCos(dij);
                pe += eq[i] * eq[j] * (yRed[i, j].Real * cosD + yRed[i, j].Imaginary * sinD);
            }
            pm[i] = pe;
        }

        return new ClassicalInit.Prepared(h, pm, eq, delta0, yRed, genBuses, yRedFault, holdNonMachines ? machineIdx : null);
    }

    public static (double[,] K, CouplingReport Report) OldSynchronising(
        GridModel model, IReadOnlyList<SpeedDomain> masses, DistributedConfig dist)
    {
        int n = masses.Count;
        var k = new double[n, n];
        if (n <= 1) return (k, new CouplingReport(CouplingSource.NotNeeded, "one swing-mass domain or none: nothing to couple", 0, 0));

        bool hasReactance = model.Branches.Any(b => Math.Abs(b.X) > DomainCoupling.MinBranchReactancePu);
        if (!hasReactance)
            return (Uniform(masses, model.FrequencyHz, dist), new CouplingReport(CouplingSource.UniformNoBranchReactance,
                $"the model has no branch reactance, so the uniform {dist.TargetInterAreaHz:0.###} Hz target-mode stiffness is used", 0, 0));

        // Map each generator bus id -> the swing-mass domain that holds the most inertia there.
        (Dictionary<int, int> busDomain, List<int> keepBusIds) = BusDomains(model, masses);

        YBus ybus = YBus.Build(model);
        Complex[,]? reduced = TryReduce(ybus, keepBusIds, out string? failure, out int dropped);
        if (reduced is null)
            return (Uniform(masses, model.FrequencyHz, dist), new CouplingReport(CouplingSource.UniformReductionFailed,
                $"the network reduction onto the machine buses failed ({failure}), so the uniform {dist.TargetInterAreaHz:0.###} Hz "
                + "target-mode stiffness is used", dropped, 0));

        int negative = 0;
        int m = keepBusIds.Count;
        for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
            {
                int da = busDomain[keepBusIds[a]];
                int db = busDomain[keepBusIds[b]];
                if (da == db) continue;
                double kij = model.BaseMva * reduced[a, b].Imaginary;   // synchronising coefficient (MW/rad), flat 1 p.u.
                if (kij <= 0) { negative++; continue; }                  // ignore anti-synchronising artefacts (counted, R025)
                k[da, db] += kij;
                k[db, da] += kij;
            }

        double total = 0;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) total += k[i, j];
        if (total > DomainCoupling.MinTotalCouplingMwPerRad)
            return (k, new CouplingReport(CouplingSource.Kron, string.Empty, dropped, negative));
        return (Uniform(masses, model.FrequencyHz, dist), new CouplingReport(CouplingSource.UniformNegligibleCoupling,
            $"the reduced network couples the speed domains by a negligible total ({total:E3} MW/rad), so the uniform "
            + $"{dist.TargetInterAreaHz:0.###} Hz target-mode stiffness is used", dropped, negative));
    }

    // <summary>
    // R060 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): how a power step at
    // <paramref name="busId"/> is shared among the swing-mass domains at the instant it happens, as fractions that add to
    // one, in <paramref name="masses"/> order, with a plain-English statement of the rule that placed it.
    //
    // <para>The physics (Anderson and Fouad, Power System Control and Stability, 2nd ed., 2003, ch. 3, the distribution
    // of a power impact [VERIFY section number]; Kundur, Power System Stability and Control, 1994, section 11.1): at the
    // instant of a sudden power impact at a bus the rotor angles cannot move, so the impact is picked up by the machines
    // in proportion to their synchronising power coefficients with respect to the point of impact, P_sik, and only later
    // redistributed by inertia and the governors. This model works on the same flat-voltage, network-only reduction as
    // <see cref="SynchronisingMatrix"/>: the generator buses are the domains' internal nodes, P_sik is the base MVA times
    // the imaginary part of the Kron-reduced transfer admittance between the impact bus and each generator bus, and a
    // domain's share is the sum over its buses. Consequences, each the limit of that rule: a step at a bus that hosts a
    // domain's machines lands wholly on that domain (the bus IS the domain's node; when machines of several domains
    // share the bus, the one holding the most inertia there owns it, the rule SynchronisingMatrix uses); a step at a
    // load or transmission bus is shared by electrical proximity.</para>
    //
    // <para>Fails loud: a bus the model does not have (<see cref="ArgumentException"/>), and a bus that cannot be placed
    // physically, because the model has no branch reactance, the network reduction fails, or the bus has no positive
    // synchronising coupling to any machine (<see cref="InvalidOperationException"/>, naming the bus and the reason).</para>
    // </summary>
    public static (double[] Shares, string Rule) OldImpactShares(GridModel model, IReadOnlyList<SpeedDomain> masses, int busId)
    {
        int n = masses.Count;
        if (n == 0) throw new ArgumentException("There is no swing-mass domain to place a power step on.", nameof(masses));
        if (!model.Buses.Any(b => b.Id == busId))
            throw new ArgumentException($"The frequency event is placed at bus {busId}, which the model '{model.Name}' does not have.", nameof(busId));

        (Dictionary<int, int> busDomain, List<int> genBusIds) = BusDomains(model, masses);
        var shares = new double[n];
        if (busDomain.TryGetValue(busId, out int home))
        {
            shares[home] = 1.0;
            return (shares, $"bus {busId} hosts the machines of speed domain {masses[home].Id}, which takes the whole step");
        }

        if (!model.Branches.Any(b => Math.Abs(b.X) > DomainCoupling.MinBranchReactancePu))
            throw new InvalidOperationException(
                $"Cannot place the frequency event at bus {busId}: it hosts no synchronous machine and the model has no branch "
                + "reactance to share the step by electrical proximity.");

        var keep = new List<int>(genBusIds) { busId };
        YBus ybus = YBus.Build(model);
        Complex[,]? reduced = TryReduce(ybus, keep, out string? failure, out _);
        if (reduced is null)
            throw new InvalidOperationException(
                $"Cannot place the frequency event at bus {busId}: the network reduction onto the machine buses failed ({failure}).");

        int e = keep.Count - 1;   // the impact bus is the last kept node
        var coupling = new double[n];
        for (int a = 0; a < genBusIds.Count; a++)
        {
            double pSik = model.BaseMva * reduced[e, a].Imaginary;   // synchronising coefficient to the impact bus (MW/rad)
            if (pSik > 0) coupling[busDomain[genBusIds[a]]] += pSik;
        }
        double total = 0;
        for (int d = 0; d < n; d++) total += coupling[d];
        if (!(total > DomainCoupling.MinTotalCouplingMwPerRad))
            throw new InvalidOperationException(
                $"Cannot place the frequency event at bus {busId}: it has no synchronising coupling to any synchronous machine "
                + "(it is not connected to the machines through the network).");
        for (int d = 0; d < n; d++) shares[d] = coupling[d] / total;
        return (shares, $"bus {busId} hosts no synchronous machine; the step is shared by synchronising coupling to the impact bus "
            + "(Anderson and Fouad, distribution of a power impact)");
    }

    // <summary>
    // Each generator bus that hosts a swing-mass member, mapped to the domain holding the most inertia (H x MBase) there,
    // plus the bus ids in first-seen order (domain order, then member order). The list is the kept-bus order of the Kron
    // reduction; it used to be read from the dictionary's Keys, which enumerate in insertion order when nothing is
    // removed, so the order and every reduced value are the same, now pinned by construction instead of by a
    // Dictionary implementation detail.
    // </summary>
    private static (Dictionary<int, int> BusDomain, List<int> BusIds) BusDomains(GridModel model, IReadOnlyList<SpeedDomain> masses)
    {
        var busDomain = new Dictionary<int, int>();
        var busInertia = new Dictionary<int, double>();
        var order = new List<int>();
        for (int d = 0; d < masses.Count; d++)
            foreach (int gi in masses[d].MemberGenIndices)
            {
                Generator g = model.Generators[gi];
                double e = g.H * g.MBase;
                if (!busInertia.TryGetValue(g.Bus, out double cur) || e > cur)
                {
                    if (!busDomain.ContainsKey(g.Bus)) order.Add(g.Bus);
                    busInertia[g.Bus] = Math.Max(cur, e);
                    busDomain[g.Bus] = d;
                }
            }
        return (busDomain, order);
    }

    // <summary>
    // The Kron reduction of the network onto <paramref name="keepBusIds"/> (in that order), or null with the reason when it
    // cannot be formed. R025: only the buses connected (through non-zero admittance) to at least one kept bus take part;
    // the rest (<paramref name="droppedBusCount"/>) are left out, because they share no admittance with any kept bus and
    // so cannot change a reduced value, while an isolated one (a zero self-admittance) used to abort the whole reduction
    // on its near-singular pivot. The retained buses keep their relative order, so the elimination order and every
    // reduced value are those of the full reduction whenever that succeeded.
    // </summary>
    private static Complex[,]? TryReduce(YBus ybus, IReadOnlyList<int> keepBusIds, out string? failure, out int droppedBusCount)
    {
        Complex[,] y = ybus.Y;
        int nAll = y.GetLength(0);

        // Breadth-first search over the admittance graph from every kept bus.
        var reached = new bool[nAll];
        var queue = new Queue<int>();
        foreach (int id in keepBusIds)
        {
            int i = ybus.IndexOf(id);
            if (!reached[i]) { reached[i] = true; queue.Enqueue(i); }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            for (int j = 0; j < nAll; j++)
                if (!reached[j] && j != i && (y[i, j] != Complex.Zero || y[j, i] != Complex.Zero))
                {
                    reached[j] = true;
                    queue.Enqueue(j);
                }
        }

        // The sub-network of reached buses, in ascending original index (the order KronReduction eliminates in).
        var subOf = new int[nAll];
        var keptOrig = new List<int>(nAll);
        for (int i = 0; i < nAll; i++)
        {
            subOf[i] = -1;
            if (reached[i]) { subOf[i] = keptOrig.Count; keptOrig.Add(i); }
        }
        droppedBusCount = nAll - keptOrig.Count;

        Complex[,] ySub;
        if (droppedBusCount == 0) ySub = y;   // nothing to drop: the very matrix the reduction always used
        else
        {
            int m = keptOrig.Count;
            ySub = new Complex[m, m];
            for (int a = 0; a < m; a++)
                for (int b = 0; b < m; b++)
                    ySub[a, b] = y[keptOrig[a], keptOrig[b]];
        }
        List<int> keepIdx = keepBusIds.Select(id => subOf[ybus.IndexOf(id)]).ToList();
        try
        {
            failure = null;
            return KronReduction.Reduce(ySub, keepIdx);
        }
        catch (InvalidOperationException ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    // <summary>The M1 uniform stiffness targeting cfg.TargetInterAreaHz (fallback for branchless models).</summary>
    private static double[,] Uniform(IReadOnlyList<SpeedDomain> masses, double f0, DistributedConfig dist)
    {
        int n = masses.Count;
        var k = new double[n, n];
        double[] eMass = masses.Select(mm => mm.AggregateInertiaMwS).ToArray();
        double sumInvE = eMass.Sum(e => 1.0 / e);
        double omega = 2.0 * Math.PI * dist.TargetInterAreaHz;
        double kUniform = (omega * omega) / (Math.PI * f0 * sumInvE);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) if (i != j) k[i, j] = kUniform;
        return k;
    }
}
