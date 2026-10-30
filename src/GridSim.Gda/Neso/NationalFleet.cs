namespace GridSim.Gda.Neso;

/// <summary>One generating unit in the GB transmission-connected fleet.</summary>
public sealed record FleetUnit(string Name, string Site, string Type, double CapacityMw, double H)
{
    /// <summary>Synchronous machine (contributes rotational inertia) vs converter-connected.</summary>
    public bool Synchronous => H > 0;
}

/// <summary>Per-technology rollup of the fleet.</summary>
public sealed record FleetCategory(string Type, int Count, double CapacityMw, double DispatchMw, double InertiaGvaS);

/// <summary>
/// The real GB transmission-connected generation fleet, assembled from the NESO TEC
/// register, the Interconnector Register and the Embedded Register. It gives the
/// generation mix, the synchronous-vs-converter split, and the headline that drives
/// frequency stability: total system inertia  HMVA  -  falling as the fleet
/// decarbonises, which is exactly what makes RoCoF worse after a big trip.
/// </summary>
public sealed class NationalFleet
{
    public IReadOnlyList<FleetUnit> Units { get; }
    public IReadOnlyList<FleetCategory> Categories { get; }

    public double TotalCapacityMw { get; }
    public double DispatchMw { get; }
    public double SynchronousCapacityMw { get; }
    public double ConverterCapacityMw { get; }
    /// <summary>System inertia with the whole synchronous fleet online ( HMVA), in GVAs  -  an upper bound.</summary>
    public double InertiaGvaS { get; }

    public FleetUnit Largest => Units.OrderByDescending(u => u.CapacityMw).First();

    public NationalFleet(IReadOnlyList<FleetUnit> units)
    {
        Units = units;
        TotalCapacityMw = units.Sum(u => u.CapacityMw);
        SynchronousCapacityMw = units.Where(u => u.Synchronous).Sum(u => u.CapacityMw);
        ConverterCapacityMw = TotalCapacityMw - SynchronousCapacityMw;
        // N325 (DOC-17): a sum over EVERY unit, which is the rotating-mass total because a unit's H comes from
        // FuelMap.InertiaH, exactly 0 for every converter technology (pinned by Doc17TextHygieneTests).
        InertiaGvaS = units.Sum(u => u.H * u.CapacityMw) / 1000.0;

        Categories = units.GroupBy(u => u.Type)
            .Select(g => new FleetCategory(
                g.Key, g.Count(), g.Sum(u => u.CapacityMw),
                g.Sum(u => u.CapacityMw * FuelMap.CapacityFactor(u.Type)),
                g.Sum(u => u.H * u.CapacityMw) / 1000.0))
            .OrderByDescending(c => c.CapacityMw).ToList();

        DispatchMw = Categories.Sum(c => c.DispatchMw);
    }
}

/// <summary>Builds the <see cref="NationalFleet"/> from the NESO registers.</summary>
public static class NationalFleetBuilder
{
    public static NationalFleet Build(GdaPaths paths, bool connectedOnly = true)
    {
        var units = new List<FleetUnit>();

        foreach (TecEntry t in NesoReader.Tec(paths.TecRegister))
        {
            if (t.CapacityMw <= 0) continue;
            if (connectedOnly && !t.IsConnected) continue;
            FuelMap.Kind k = FuelMap.Classify(t.PlantType);
            units.Add(new FleetUnit(t.ProjectName, t.ConnectionSite, k.Type, t.CapacityMw, k.H));
        }

        if (paths.Has(p => p.InterconnectorRegister))
            foreach (InterconnectorEntry ic in NesoReader.Interconnectors(paths.InterconnectorRegister))
            {
                // Without a status filter the whole register (Scoping/consenting links included)
                // floods the fleet with tens of GW of phantom interconnectors, valued at their
                // ultimate "Total" capability. For the connected fleet, keep only live links and
                // count their currently-commissioned capacity, not future-dated increases.
                if (connectedOnly && !ic.IsConnected) continue;
                double cap = connectedOnly ? ic.CurrentCapacityMw : ic.TotalCapacityMw;
                if (cap <= 0) continue;
                units.Add(new FleetUnit(ic.ProjectName, ic.ConnectionSite, "interconnector", cap, 0));
            }

        if (paths.Has(p => p.EmbeddedRegister))
            foreach (EmbeddedEntry e in NesoReader.Embedded(paths.EmbeddedRegister))
            {
                // Same connected-only rule as the TEC and interconnector loops above (R016,
                // October 2026 review). Without it every scoping and consenting embedded
                // project entered the "connected" fleet at its ultimate cumulative MW,
                // inflating total capacity, dispatch and (for synchronous types) inertia.
                if (connectedOnly && !e.IsConnected) continue;
                if (e.CapacityMw <= 0) continue;
                FuelMap.Kind k = FuelMap.Classify(e.PlantType);
                units.Add(new FleetUnit(e.ProjectName, e.ConnectionSite, k.Type, e.CapacityMw, k.H));
            }

        return new NationalFleet(units);
    }
}
