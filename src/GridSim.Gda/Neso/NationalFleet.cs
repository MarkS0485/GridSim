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

        // F-038: the connected fleet counts only what is connected today (ConnectedCapacityMw = "MW Connected"); the
        // future-inclusive cumulative figure is used only in whole-register mode. A connected-looking row with 0 MW
        // connected used to enter at its cumulative MW. Same rule for the embedded loop below.
        IReadOnlyList<TecEntry> tecRows = NesoReader.Tec(paths.TecRegister);
        if (connectedOnly)
        {
            foreach (TecEntry t in tecRows)
            {
                if (!t.IsConnected) continue;
                double cap = t.ConnectedCapacityMw;
                if (cap <= 0) continue;
                FuelMap.Kind k = FuelMap.Classify(t.PlantType);
                units.Add(new FleetUnit(t.ProjectName, t.ConnectionSite, k.Type, cap, k.H));
            }
        }
        else
        {
            // Bug #209: whole-register mode counts each PROJECT once. A multi-stage project has one row per stage, each
            // carrying the running cumulative total, so adding every row counted it once per stage (Springwell's
            // 400 + 800 + 1600 MW stages gave 2800 MW against its 1600 MW ultimate).
            foreach (NesoStages.Pick<TecEntry> pick in NesoStages.LatestPerProject(tecRows, "TEC register",
                         t => t.ProjectId, t => t.ProjectName, t => t.Stage, t => t.MwEffectiveFrom))
            {
                double cap = pick.MultiStage ? pick.Latest.CumulativeMw : pick.Latest.CapacityMw;
                if (cap <= 0) continue;
                FuelMap.Kind k = FuelMap.Classify(pick.Latest.PlantType);
                units.Add(new FleetUnit(pick.Latest.ProjectName, pick.Latest.ConnectionSite, k.Type, cap, k.H));
            }
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

        if (paths.Has(p => p.EmbeddedRegister) && !connectedOnly)
        {
            // Bug #209: one unit per project, as for the TEC register above.
            foreach (NesoStages.Pick<EmbeddedEntry> pick in NesoStages.LatestPerProject(
                         NesoReader.Embedded(paths.EmbeddedRegister), "Embedded Register",
                         e => e.ProjectId, e => e.ProjectName, e => e.Stage, e => e.MwEffectiveFrom))
            {
                double cap = pick.MultiStage ? pick.Latest.CumulativeMw : pick.Latest.CapacityMw;
                if (cap <= 0) continue;
                FuelMap.Kind k = FuelMap.Classify(pick.Latest.PlantType);
                units.Add(new FleetUnit(pick.Latest.ProjectName, pick.Latest.ConnectionSite, k.Type, cap, k.H));
            }
        }
        else if (paths.Has(p => p.EmbeddedRegister))
            foreach (EmbeddedEntry e in NesoReader.Embedded(paths.EmbeddedRegister))
            {
                // Same connected-only rule as the TEC and interconnector loops above (R016,
                // October 2026 review). Without it every scoping and consenting embedded
                // project entered the "connected" fleet at its ultimate cumulative MW,
                // inflating total capacity, dispatch and (for synchronous types) inertia.
                if (connectedOnly && !e.IsConnected) continue;
                double cap = connectedOnly ? e.ConnectedCapacityMw : e.CapacityMw;   // F-038
                if (cap <= 0) continue;
                FuelMap.Kind k = FuelMap.Classify(e.PlantType);
                units.Add(new FleetUnit(e.ProjectName, e.ConnectionSite, k.Type, cap, k.H));
            }

        return new NationalFleet(units);
    }
}

/// <summary>
/// Bug #209: picks the LATEST stage of each project in a NESO register (TEC or Embedded), so whole-register mode counts a
/// multi-stage project once, at the running total its last stage reaches.
/// <para>A project is the set of rows sharing a "Project ID". A row with a blank Project ID cannot be grouped, so it is its
/// own project (the 28 September 2026 registers have none).</para>
/// <para>"Latest" is decided by "MW Effective From" first: a blank date means the row is already in effect, so it sorts
/// before every dated row; a dated row must be exactly yyyy-MM-dd. Then by "Stage" number (blank sorts first). Two rows
/// of one project with the same date and the same stage number leave the latest stage ambiguous, and that throws; so
/// does a malformed date or stage. Nothing is guessed.</para>
/// <para>Projects come out in the order of their FIRST row, so the fleet's unit order (and therefore every floating-point
/// sum over it) is fixed by the register's own row order.</para>
/// </summary>
internal static class NesoStages
{
    /// <summary>The latest row of one project, and whether the project has more than one row.</summary>
    internal readonly record struct Pick<T>(T Latest, bool MultiStage);

    internal static IReadOnlyList<Pick<T>> LatestPerProject<T>(IReadOnlyList<T> rows, string register,
        Func<T, string> projectId, Func<T, string> projectName, Func<T, string> stage, Func<T, string> effectiveFrom)
    {
        // Group by Project ID, remembering first-appearance order.
        var order = new List<List<T>>();
        var byId = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (T row in rows)
        {
            string id = projectId(row).Trim();
            if (id.Length == 0) { order.Add(new List<T> { row }); continue; }   // ungroupable: its own project
            if (!byId.TryGetValue(id, out List<T>? group))
            {
                group = new List<T>();
                byId.Add(id, group);
                order.Add(group);
            }
            group.Add(row);
        }

        var picks = new List<Pick<T>>(order.Count);
        foreach (List<T> group in order)
        {
            if (group.Count == 1) { picks.Add(new Pick<T>(group[0], MultiStage: false)); continue; }

            T best = group[0];
            (DateOnly Date, double Stage) bestKey = Key(best);
            for (int i = 1; i < group.Count; i++)
            {
                (DateOnly Date, double Stage) k = Key(group[i]);
                int c = k.Date.CompareTo(bestKey.Date);
                if (c == 0) c = k.Stage.CompareTo(bestKey.Stage);
                if (c > 0) { best = group[i]; bestKey = k; }
            }
            // Ambiguity: a second row with the very same key as the winner.
            int same = 0;
            foreach (T row in group)
                if (Key(row) == bestKey) same++;
            if (same > 1)
                throw new InvalidDataException(
                    $"{register}: project '{projectName(best)}' (Project ID {projectId(best).Trim()}) has {same} stage rows " +
                    $"with the same MW Effective From and Stage, so its latest stage is ambiguous.");
            picks.Add(new Pick<T>(best, MultiStage: true));
        }
        return picks;

        (DateOnly Date, double Stage) Key(T row)
        {
            string d = effectiveFrom(row).Trim();
            DateOnly date = DateOnly.MinValue;   // blank: already in effect, sorts first
            if (d.Length > 0 && !DateOnly.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out date))
                throw new InvalidDataException(
                    $"{register}: project '{projectName(row)}' has MW Effective From '{d}', which is not a yyyy-MM-dd date.");
            string s = stage(row).Trim();
            double st = double.NegativeInfinity;  // blank: sorts first
            if (s.Length > 0 && !(double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out st) && double.IsFinite(st)))
                throw new InvalidDataException(
                    $"{register}: project '{projectName(row)}' has Stage '{s}', which is not a number.");
            return (date, st);
        }
    }
}
