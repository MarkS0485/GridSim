using GridSim.Core.Model;

namespace GridSim.Core.Synthetic;

/// <summary>
/// Procedurally builds a synthetic GB-shaped network at distribution depth  -  a couple
/// of thousand electrical buses down the whole voltage ladder (400 -> 275 -> 132 -> 33 ->
/// 11 -> 6.6 kV)  -  so the UI and solvers can be exercised at GDA scale without any
/// licensed or disclosure-bound data. Everything here is invented: the geography is
/// jittered off the reduced model's ten zone anchors, demand follows the same zonal
/// split, and the transmission fleet (including the named interconnectors) matches
/// the reduced case so the Generation tab and asset pages stay meaningful.
///
/// Structure per zone: a small 400 kV ring (meshed to neighbouring zones along the
/// familiar corridors; a zone with only two 400 kV nodes gets a single "400 tie" instead,
/// N171), a 275 kV ring in the urban zones, Grid Supply Points at
/// 132 kV under the transmission nodes, 33 kV primaries under each GSP, 11 kV
/// secondaries under each primary, and 6.6 kV legacy pockets under a fraction of
/// those. Demand attaches mostly at 11 kV; capacitor banks at the GSPs and primaries
/// keep the radial voltage profile in band, the way a DNO actually runs it.
/// </summary>
public static class SyntheticGb
{
    public sealed record Options
    {
        public int Seed { get; init; } = 20260605;
        /// <summary>Multiplies the number of 11 kV secondaries under each 33 kV primary (at least 2 per primary)  -  1.0
        /// ~= 2,000+ buses. Must be finite and above zero.
        /// <para>Below 1.0 the network is a smaller GB (N108, Ruling R1, October 2026): each zone's demand, embedded
        /// plant and transmission fleet are multiplied by <see cref="ZoneScaleFactor"/>, the zone's secondaries built
        /// over those it gets at 1.0, so the average load per 30 MVA 33/11 kV transformer stays at its scale-1.0 level.
        /// Before N108 every zone kept its GB demand at every scale (N051): every scale up to 0.36 built the same
        /// 1,082-bus network with a median secondary transformer at 175 % of its rating and no power-flow solution.
        /// At 1.0 and above nothing is scaled (those networks are bit-identical to before): a larger scale spreads the
        /// same GB demand over more secondaries, because the 132 kV and 33 kV layers do not grow with it.</para></summary>
        public double Scale { get; init; } = 1.0;
    }

    private sealed record Zone(string Name, string Tag, double X, double Y, double DemandMw, bool Urban);

    /// <summary>The ten GB zones of the reduced model: anchor geography + demand split.</summary>
    private static readonly Zone[] Zones =
    {
        new("North Scotland",       "NSC", 0.45, 0.97,  600, false),
        new("South Scotland",       "SSC", 0.42, 0.85, 2200, true),
        new("North England",        "NEN", 0.45, 0.72, 5500, true),
        new("Yorkshire & Humber",   "YRK", 0.57, 0.66, 4500, true),
        new("North Wales & Mersey", "MER", 0.36, 0.60, 3500, true),
        new("Midlands",             "MID", 0.48, 0.52, 6500, true),
        new("East Anglia",          "EAN", 0.64, 0.45, 3000, false),
        new("London & South East",  "LDN", 0.58, 0.34, 8500, true),
        new("South",                "STH", 0.50, 0.27, 3500, false),
        new("South West & S Wales", "SWW", 0.30, 0.32, 1700, false),
    };

    /// <summary>The familiar inter-zone 400 kV corridors (zone index pairs).</summary>
    private static readonly (int a, int b)[] Corridors =
    {
        (0, 1), (1, 2), (2, 3), (2, 4), (3, 5), (4, 5), (3, 6),
        (5, 6), (5, 7), (6, 7), (7, 8), (5, 8), (8, 9), (4, 9),
    };

    /// <summary>A transmission-connected unit of the fleet. <paramref name="LineCommutated"/> marks a line-commutated
    /// (LCC, thyristor) HVDC interconnector: such a converter supplies no fault current, so its unit is written with
    /// <see cref="Generator.ConverterFaultCurrentPu"/> = 0 (the model's LCC encoding, which also selects the
    /// line-commutated harmonic profile). Every other converter unit keeps the default (null: 1.2 x rated, a
    /// self-commutated VSC link).</summary>
    private sealed record Plant(string Name, int Zone, string Type, double CapMva, double Cf, double H, bool Slack = false,
        bool LineCommutated = false);

    /// <summary>The transmission fleet of the reduced case  -  same plants, same named links.</summary>
    private static readonly Plant[] Fleet =
    {
        new("North Scotland Hydro",        0, "hydro",          1300, 0.50, 3.0),
        new("North Scotland Onshore Wind", 0, "onshore",        3000, 0.50, 0.0),
        new("Torness Nuclear",             1, "nuclear",        1250, 0.90, 4.0),
        new("South Scotland Onshore Wind", 1, "onshore",        4000, 0.50, 0.0),
        // N203 (October 2026, Ruling R1 follow-up): Moyle, IFA and BritNed are line-commutated (LCC) HVDC links, as
        // data/gb-synthetic always marked them; every other interconnector here is a voltage-source (VSC) link.
        new("Moyle",                       1, "interconnector",  500, -0.30, 0.0, LineCommutated: true),
        new("Heysham Nuclear",             2, "nuclear",        2400, 0.90, 4.0),
        new("North England CCGT",          2, "ccgt",           2000, 0.60, 5.0),
        new("North Sea Link",              2, "interconnector", 1400, 0.55, 0.0),
        new("Drax Biomass",                3, "biomass",        2600, 0.70, 4.0),
        new("Yorkshire CCGT",              3, "ccgt",           4000, 0.60, 5.0),
        new("Mersey Offshore Wind",        4, "offshore",       2000, 0.50, 0.0),
        new("North Wales CCGT",            4, "ccgt",           3000, 0.60, 5.0),
        new("EWIC",                        4, "interconnector",  500, 0.30, 0.0),
        new("Midlands CCGT (marginal)",    5, "ccgt",           9000, 0.60, 5.0, Slack: true),
        new("East Anglia Offshore Wind",   6, "offshore",       8000, 0.50, 0.0),
        new("Sizewell B Nuclear",          6, "nuclear",        1250, 0.90, 4.0),
        new("Viking Link",                 6, "interconnector", 1400, 0.65, 0.0),
        new("IFA",                         7, "interconnector", 2000, 0.70, 0.0, LineCommutated: true),
        new("ElecLink",                    7, "interconnector", 1000, 0.60, 0.0),
        new("NemoLink",                    7, "interconnector", 1000, 0.50, 0.0),
        new("BritNed",                     7, "interconnector", 1000, 0.40, 0.0, LineCommutated: true),
        new("London CCGT",                 7, "ccgt",           2000, 0.60, 5.0),
        new("South CCGT",                  8, "ccgt",           3000, 0.60, 5.0),
        new("South Solar",                 8, "solar",          4000, 0.25, 0.0),
        new("IFA2",                        8, "interconnector", 1000, 0.55, 0.0),
        new("Hinkley Point Nuclear",       9, "nuclear",        3200, 0.90, 4.0),
        new("Pembroke CCGT",               9, "ccgt",           2000, 0.60, 5.0),
        new("South Wales Onshore Wind",    9, "onshore",        1000, 0.50, 0.0),
        new("Greenlink",                   9, "interconnector",  500, 0.35, 0.0),
    };

    // ---- electrical parameters, per-unit on the 100 MVA system base ----
    // (synthetic bulk equivalents: aggregated feeders are far stiffer than a single
    // real circuit, sized so the radial profile lands in band with the tap boosts)
    private const double BaseMva = 100.0;

    // ---- N345 (CF3, October 2026): the network literals, named ----
    // Every literal the build below used to write inline and the v2 kind specs (lineType, transformerType, oltc, load,
    // storage, gridFollowingInverter, synchronousMachine) cite: now a named constant the specs cite by symbol. Each
    // constant is the literal it replaces, character for character (an int rating such as 5500 is the same double),
    // so the built network is bit-identical. Per-unit values are on the 100 MVA system base (BaseMva).
    private const double Ring400RPu = 0.0003;   // 400 kV ring circuit resistance (pu on 100 MVA)
    private const double Ring400XPu = 0.003;   // 400 kV ring circuit reactance (pu)
    private const double Ring400BPu = 0.35;   // 400 kV ring circuit charging susceptance (pu)
    private const double Ring400RateMva = 5500;   // 400 kV ring circuit rating (MVA)
    private const double Ring275RPu = 0.001;   // 275 kV ring circuit resistance (pu)
    private const double Ring275XPu = 0.0085;   // 275 kV ring circuit reactance (pu)
    private const double Ring275BPu = 0.2;   // 275 kV ring circuit charging susceptance (pu)
    private const double Ring275RateMva = 2400;   // 275 kV ring circuit rating (MVA)
    private const double Sgt400To275RPu = 0.0004;   // SGT 400/275 kV resistance (pu)
    private const double Sgt400To275XPu = 0.012;   // SGT 400/275 kV reactance (pu)
    private const double Sgt400To275RateMva = 1000;   // SGT 400/275 kV rating (MVA)
    private const double Corridor400RPu = 0.0007;   // inter-zone 400 kV corridor resistance (pu)
    private const double Corridor400XPu = 0.007;   // inter-zone 400 kV corridor reactance (pu)
    private const double Corridor400BPu = 0.8;   // inter-zone 400 kV corridor charging susceptance (pu)
    private const double Corridor400RateMva = 5500;   // inter-zone 400 kV corridor rating (MVA)
    private const double EmbeddedSolarCf = 0.25;   // embedded solar capacity factor
    private const double EmbeddedOnshoreCf = 0.35;   // embedded onshore wind capacity factor
    private const double EmbeddedBiomassCf = 0.70;   // embedded biomass capacity factor
    private const double EmbeddedStorageCf = 0.10;   // embedded storage capacity factor
    private const double GspSgtRPu = 0.001;   // GSP supergrid transformer resistance (pu)
    private const double GspSgtXPu = 0.02;   // GSP supergrid transformer reactance (pu)
    private const double GspSgtTapRatio = 0.99;   // GSP supergrid transformer starting tap
    private const double GspSgtRateMva = 750;   // GSP supergrid transformer rating (MVA)
    private const double GspSgtOltcTargetVpu = 1.0;   // GSP SGT OLTC voltage target (pu)
    private const double GspSgtOltcMinTap = 0.9;   // GSP SGT OLTC lowest tap
    private const double GspSgtOltcMaxTap = 1.1;   // GSP SGT OLTC highest tap
    private const double GspSgtOltcStepPu = 0.00625;   // GSP SGT OLTC tap step (pu)
    private const double GspSgtOltcDeadbandPu = 0.01;   // GSP SGT OLTC deadband (pu)
    private const double IndustrialSpurRPu = 0.002;   // 132 kV industrial spur resistance (pu)
    private const double IndustrialSpurXPu = 0.01;   // 132 kV industrial spur reactance (pu)
    private const double IndustrialSpurBPu = 0.01;   // 132 kV industrial spur charging susceptance (pu)
    private const double IndustrialSpurRateMva = 240;   // 132 kV industrial spur rating (MVA)
    private const double IndustrialReactiveToActive = 0.25;   // industrial demand Q / P
    private const double PrimaryTxRPu = 0.004;   // 132/33 kV primary transformer resistance (pu)
    private const double PrimaryTxXPu = 0.06;   // 132/33 kV primary transformer reactance (pu)
    private const double PrimaryTxTapRatio = 0.97;   // 132/33 kV primary transformer starting tap
    private const double PrimaryTxRateMva = 180;   // 132/33 kV primary transformer rating (MVA)
    private const double PrimaryOltcTargetVpu = 1.0;   // 132/33 kV OLTC voltage target (pu)
    private const double PrimaryOltcMinTap = 0.9;   // 132/33 kV OLTC lowest tap
    private const double PrimaryOltcMaxTap = 1.1;   // 132/33 kV OLTC highest tap
    private const double PrimaryOltcStepPu = 0.0125;   // 132/33 kV OLTC tap step (pu)
    private const double PrimaryOltcDeadbandPu = 0.015;   // 132/33 kV OLTC deadband (pu)
    private const double SecondaryTxRPu = 0.01;   // 33/11 kV secondary transformer resistance (pu)
    private const double SecondaryTxXPu = 0.22;   // 33/11 kV secondary transformer reactance (pu)
    private const double SecondaryTxTapRatio = 0.97;   // 33/11 kV secondary transformer fixed tap
    private const double SecondaryTxRateMva = 30;   // 33/11 kV secondary transformer rating (MVA)
    private const double LegacyTxRPu = 0.04;   // 11/6.6 kV legacy transformer resistance (pu)
    private const double LegacyTxXPu = 0.5;   // 11/6.6 kV legacy transformer reactance (pu)
    private const double LegacyTxTapRatio = 0.99;   // 11/6.6 kV legacy transformer fixed tap
    private const double LegacyTxRateMva = 12;   // 11/6.6 kV legacy transformer rating (MVA)
    private const double LadderReactiveToActive = 0.25;   // 33 / 11 / 6.6 kV ladder demand Q / P (pf ~= 0.97)

    public static GridModel Build(Options? options = null)
    {
        Options opt = options ?? new Options();
        // N051: fail loud on a scale that is not a positive finite number. NaN used to cast to int.MinValue and a zero or
        // negative scale to a non-positive count, and the floor of 2 secondaries per primary quietly built the smallest
        // network for all of them.
        if (!double.IsFinite(opt.Scale) || opt.Scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), opt.Scale,
                "SyntheticGb: Scale must be a finite number above 0 (it multiplies the 11 kV secondaries per primary; 1.0 ~= 2,000+ buses).");
        var rng = new Random(opt.Seed);

        var buses = new List<Bus>();
        var gens = new List<Generator>();
        var branches = new List<Branch>();
        var oltcs = new List<Oltc>();
        int nextId = 1;
        double totalDemand = Zones.Sum(z => z.DemandMw);

        int AddBus(string name, double kv, double x, double y, BusType type = BusType.Pq)
        {
            buses.Add(new Bus(nextId, type, 0, 0, 0, 0, kv,
                Name: name, X: Math.Clamp(x, 0.01, 0.99), Y: Math.Clamp(y, 0.01, 0.99)));
            return nextId++;
        }

        void SetDemand(int id, double pMw, double qMvar)
        {
            int i = buses.FindIndex(b => b.Id == id);
            buses[i] = buses[i] with { Pd = pMw, Qd = qMvar };
        }

        void AddShunt(int id, double bsPu)
        {
            int i = buses.FindIndex(b => b.Id == id);
            buses[i] = buses[i] with { Bs = buses[i].Bs + bsPu };
        }

        (double x, double y) Jitter(double x, double y, double r) =>
            (x + (rng.NextDouble() - 0.5) * 2 * r, y + (rng.NextDouble() - 0.5) * 2 * r);

        // ---------- transmission: 400 kV rings per zone, corridors between ----------
        var z400 = new List<int>[Zones.Length];
        var z275 = new List<int>[Zones.Length];
        var zGsp132 = new List<int>[Zones.Length];

        for (int z = 0; z < Zones.Length; z++)
        {
            Zone zone = Zones[z];
            double share = zone.DemandMw / totalDemand;
            int n400 = Math.Max(2, (int)Math.Round(share * 30));
            z400[z] = new List<int>();
            for (int k = 0; k < n400; k++)
            {
                double ang = 2 * Math.PI * k / n400;
                int id = AddBus($"{zone.Name} {(char)('A' + k)} 400", 400,
                    zone.X + 0.028 * DetMath.Cos(ang), zone.Y + 0.028 * DetMath.Sin(ang));
                z400[z].Add(id);
            }
            // ring (or single tie for a pair)
            for (int k = 0; k < n400 && n400 > 1; k++)
            {
                if (n400 == 2 && k == 1) break;
                int a = z400[z][k], b = z400[z][(k + 1) % n400];
                // N171 (October 2026): a zone with only two 400 kV nodes (NSC, SSC, EAN and SWW at the default options)
                // gets ONE circuit between them, not a ring, so it is labelled a tie. It used to be called "<tag> 400 ring
                // 1", which suggested a second path that does not exist: in NSC, EAN and SWW the second node hangs on this
                // circuit alone (its outage splits the network; SSC's second node also reaches the 275 kV ring). Only the
                // label changed: same buses, impedance, rating and branch order, so every number is unchanged. (The branch
                // statement is unchanged, and the label is replaced just after it; its literals are named constants since
                // N345, which schemas/v2/kinds/lineType.kind.json cites by symbol.)
                branches.Add(new Branch(a, b, Ring400RPu, Ring400XPu, Ring400BPu,
                    Name: $"{zone.Tag} 400 ring {k + 1}", RateAMva: Ring400RateMva));
                if (n400 == 2) branches[^1] = branches[^1] with { Name = $"{zone.Tag} 400 tie" };
            }

            // 275 kV ring in the urban zones
            z275[z] = new List<int>();
            if (zone.Urban)
            {
                int n275 = 4 + rng.Next(3);
                for (int k = 0; k < n275; k++)
                {
                    double ang = 2 * Math.PI * (k + 0.5) / n275;
                    int id = AddBus($"{zone.Name} {(char)('A' + k)} 275", 275,
                        zone.X + 0.042 * DetMath.Cos(ang), zone.Y + 0.042 * DetMath.Sin(ang));
                    z275[z].Add(id);
                }
                for (int k = 0; k < n275; k++)
                    branches.Add(new Branch(z275[z][k], z275[z][(k + 1) % n275], Ring275RPu, Ring275XPu, Ring275BPu,
                        Name: $"{zone.Tag} 275 ring {k + 1}", RateAMva: Ring275RateMva));
                // every 275 node gets supergrid infeed  -  an urban ring carries GWs of
                // GSP load, and two SGTs would collapse it (ask London)
                for (int t = 0; t < n275; t++)
                    branches.Add(new Branch(z400[z][t % z400[z].Count], z275[z][t], Sgt400To275RPu, Sgt400To275XPu, 0,
                        Name: $"{zone.Tag} SGT 400/275 {t + 1}", IsTransformer: true, RateAMva: Sgt400To275RateMva));
            }
        }

        foreach ((int a, int b) in Corridors)
            branches.Add(new Branch(z400[a][0], z400[b][0], Corridor400RPu, Corridor400XPu, Corridor400BPu,
                Name: $"{Zones[a].Tag}-{Zones[b].Tag} 400", RateAMva: Corridor400RateMva));

        // ---------- the transmission fleet ----------
        var slot = new int[Zones.Length];
        foreach (Plant p in Fleet)
        {
            List<int> nodes = z400[p.Zone];
            int busId = nodes[slot[p.Zone]++ % nodes.Count];
            int i = buses.FindIndex(b => b.Id == busId);
            if (p.Slack) buses[i] = buses[i] with { Type = BusType.Slack };
            else if (buses[i].Type != BusType.Slack) buses[i] = buses[i] with { Type = BusType.Pv };
            gens.Add(new Generator(busId, Pg: p.CapMva * p.Cf, Qg: 0, Vg: 1.04,
                Qmin: -p.CapMva, Qmax: p.CapMva, Pmin: 0, Pmax: p.CapMva,
                MBase: p.CapMva, H: p.H, Name: p.Name, Type: p.Type,
                // N203: a line-commutated link carries no fault current (0); null keeps the VSC default.
                ConverterFaultCurrentPu: p.LineCommutated ? 0.0 : null));
        }

        // ---------- distribution: GSPs -> primaries -> secondaries -> 6.6 pockets ----------
        // zone demand split across the ladder
        const double f132 = 0.12, f33 = 0.15, f11 = 0.68, f66 = 0.05;
        var embeddedTypes = new (string type, double capLo, double capHi, double cf)[]
        {
            ("solar", 10, 60, EmbeddedSolarCf), ("onshore", 10, 40, EmbeddedOnshoreCf),
            ("biomass", 5, 25, EmbeddedBiomassCf), ("storage", 10, 50, EmbeddedStorageCf),
        };

        // N108: each zone's demand and plant factor (1.0 at scale 1.0 and above); filled per zone below and used for the
        // transmission fleet after the loop.
        var zoneFactors = new double[Zones.Length];

        for (int z = 0; z < Zones.Length; z++)
        {
            Zone zone = Zones[z];
            double share = zone.DemandMw / totalDemand;
            zGsp132[z] = new List<int>();

            int gsps = Math.Max(2, (int)Math.Round(share * 78));
            int industrials = (int)Math.Round(gsps * 0.35);

            // raw jittered weights per category, normalised back to the zone totals
            double indEach = industrials > 0 ? zone.DemandMw * f132 / industrials : 0;

            var all33 = new List<int>();
            var primQ = new Dictionary<int, double>();   // 33 kV bus -> downstream Q (pu), for cap banks

            int prims = 0, secs = 0;
            // N108: how many 11 kV secondaries this zone would get at scale 1.0 (the sum of the per-primary draws before
            // the scale is applied), and where this zone's distribution buses start, so a scale below 1 can shrink the
            // zone's demand and plant in the same proportion as its 11 kV layer (see ZoneScaleFactor below).
            int secsAtUnitScale = 0;
            int firstZoneBusId = nextId;
            // N204: where this zone's branches start; every branch from here to the end of the zone is part of its
            // radial distribution ladder (the transmission rings, corridors and SGTs 400/275 were all added earlier).
            int firstZoneBranch = branches.Count;
            List<int> parents = z400[z].Concat(z275[z]).ToList();

            for (int g = 0; g < gsps; g++)
            {
                int parent = parents[rng.Next(parents.Count)];
                Bus pb = buses.First(b => b.Id == parent);
                (double gx, double gy) = Jitter(pb.X, pb.Y, 0.040);
                int gsp = AddBus($"{zone.Tag} GSP {g + 1} 132", 132, gx, gy);
                zGsp132[z].Add(gsp);
                string sgt = $"{zone.Tag} GSP {g + 1} SGT";
                branches.Add(new Branch(parent, gsp, GspSgtRPu, GspSgtXPu, 0,
                    TapRatio: GspSgtTapRatio, Name: sgt, IsTransformer: true, RateAMva: GspSgtRateMva));
                oltcs.Add(new Oltc(sgt, gsp, GspSgtOltcTargetVpu, GspSgtOltcMinTap, GspSgtOltcMaxTap, GspSgtOltcStepPu, GspSgtOltcDeadbandPu));

                // a 132 kV industrial spur on some GSPs
                if (g < industrials)
                {
                    (double ix, double iy) = Jitter(gx, gy, 0.012);
                    int ind = AddBus($"{zone.Tag} GSP {g + 1} Industrial 132", 132, ix, iy);
                    branches.Add(new Branch(gsp, ind, IndustrialSpurRPu, IndustrialSpurXPu, IndustrialSpurBPu,
                        Name: $"{zone.Tag} GSP {g + 1} industrial spur", RateAMva: IndustrialSpurRateMva));
                    double pInd = indEach * (0.7 + rng.NextDouble() * 0.6);
                    SetDemand(ind, pInd, pInd * IndustrialReactiveToActive);
                }

                // 33 kV primaries
                int nPrim = 3 + rng.Next(2);
                for (int p = 0; p < nPrim; p++)
                {
                    prims++;
                    (double px, double py) = Jitter(gx, gy, 0.015);
                    int prim = AddBus($"{zone.Tag} G{g + 1}P{p + 1} 33", 33, px, py);
                    all33.Add(prim);
                    primQ[prim] = 0;
                    string tx = $"{zone.Tag} G{g + 1}P{p + 1} 132/33";
                    branches.Add(new Branch(gsp, prim, PrimaryTxRPu, PrimaryTxXPu, 0,
                        TapRatio: PrimaryTxTapRatio, Name: tx, IsTransformer: true, RateAMva: PrimaryTxRateMva));
                    oltcs.Add(new Oltc(tx, prim, PrimaryOltcTargetVpu, PrimaryOltcMinTap, PrimaryOltcMaxTap, PrimaryOltcStepPu, PrimaryOltcDeadbandPu));

                    // 11 kV secondaries (the bulk of the network)
                    // The draw is taken once, exactly as before (same random sequence, same arithmetic: the int draw
                    // times Scale), and also remembered for the scale-1.0 count (N108).
                    int secDraw = 4 + rng.Next(4);
                    secsAtUnitScale += secDraw;
                    int nSec = Math.Max(2, (int)Math.Round(secDraw * opt.Scale));
                    for (int s = 0; s < nSec; s++)
                    {
                        secs++;
                        (double sx, double sy) = Jitter(px, py, 0.007);
                        int sec = AddBus($"{zone.Tag} G{g + 1}P{p + 1}S{s + 1} 11", 11, sx, sy);
                        branches.Add(new Branch(prim, sec, SecondaryTxRPu, SecondaryTxXPu, 0,
                            TapRatio: SecondaryTxTapRatio, Name: $"{zone.Tag} G{g + 1}P{p + 1}S{s + 1} 33/11",
                            IsTransformer: true, RateAMva: SecondaryTxRateMva));

                        // a 6.6 kV legacy pocket under some secondaries
                        if (rng.NextDouble() < 0.15)
                        {
                            (double lx, double ly) = Jitter(sx, sy, 0.004);
                            int leg = AddBus($"{zone.Tag} G{g + 1}P{p + 1}S{s + 1} 6.6", 6.6, lx, ly);
                            branches.Add(new Branch(sec, leg, LegacyTxRPu, LegacyTxXPu, 0,
                                TapRatio: LegacyTxTapRatio, Name: $"{zone.Tag} G{g + 1}P{p + 1}S{s + 1} 11/6.6",
                                IsTransformer: true, RateAMva: LegacyTxRateMva));
                        }
                    }
                }
            }

            // demand on the ladder, jittered and scaled towards the zone total. Not exactly: the 132 kV industrial share
            // is jittered (0.7 to 1.3) and not renormalised, and the 6.6 kV share is dropped in a zone with no 6.6 kV bus,
            // so a zone's built demand can differ from its stated total by a few per cent.
            List<Bus> z33 = buses.Where(b => all33.Contains(b.Id)).ToList();
            List<Bus> z11 = buses.Where(b => b.BaseKv == 11 && b.Name!.StartsWith(zone.Tag)).ToList();
            List<Bus> z66 = buses.Where(b => b.BaseKv == 6.6 && b.Name!.StartsWith(zone.Tag)).ToList();
            void Spread(List<Bus> targets, double zoneCatMw)
            {
                if (targets.Count == 0) return;
                double[] w = targets.Select(_ => 0.7 + rng.NextDouble() * 0.6).ToArray();
                double norm = zoneCatMw / w.Sum();
                for (int i = 0; i < targets.Count; i++)
                {
                    double p = w[i] * norm;
                    SetDemand(targets[i].Id, p, p * LadderReactiveToActive);   // pf ~= 0.97
                }
            }
            Spread(z33, zone.DemandMw * f33);
            Spread(z11, zone.DemandMw * f11);
            Spread(z66, zone.DemandMw * (z66.Count > 0 ? f66 : 0));

            // N108 (Ruling R1, Mark delegated 2026-10-04): below scale 1.0 the zone's demand shrinks with its network.
            // The factor is the zone's 11 kV secondaries built at this scale over those it would get at 1.0, so the
            // average demand per secondary (and per 30 MVA 33/11 kV transformer) stays at its scale-1.0 level and a
            // small scale is a smaller GB rather than GB's whole load on fewer transformers (N051). Every demand bus of
            // the zone (132 kV industrial spurs, 33, 11 and 6.6 kV) is scaled by it, before the capacitor banks are
            // sized from the downstream Q, so the banks follow too. At scale 1.0 and above nothing here runs, so those
            // networks are bit-identical to before (no random draw is added or moved at any scale).
            double zoneFactor = ZoneScaleFactor(opt.Scale, secs, secsAtUnitScale);
            zoneFactors[z] = zoneFactor;
            if (opt.Scale < 1.0)
            {
                for (int i = 0; i < buses.Count; i++)
                    if (buses[i].Id >= firstZoneBusId && (buses[i].Pd != 0 || buses[i].Qd != 0))
                        buses[i] = buses[i] with { Pd = buses[i].Pd * zoneFactor, Qd = buses[i].Qd * zoneFactor };
            }

            // N204 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): size the radial ladder to its load.
            // Every branch this zone added (GSP SGTs, 132 kV industrial spurs, 132/33, 33/11 and 11/6.6 kV transformers)
            // feeds a radial subtree, and it used to get a fixed rating whatever that subtree drew. At the default options
            // 84 of them fed more peak demand (MVA) than their rating, 51 of those even in MW, which no dispatch could
            // relieve (N109: 36 of the 53 unrelievable lines were 33/11 kV secondaries). Now each gets as many parallel units of its standard size as its peak demand needs; see
            // SizeRadialBranchesToLoad for the rule and its basis. Done after the demand is final (spread and, below
            // scale 1.0, scaled) and before the capacitor banks (which read only the topology and the demand). No
            // random draw is added or moved, so every bus, demand and generator is exactly as before.
            SizeRadialBranchesToLoad(buses, branches, firstZoneBranch);

            // capacitor banks: primaries cover half their local Q, GSPs much of the rest  -
            // the way a DNO holds the radial profile up
            foreach (int prim in all33)
            {
                double q = DownstreamQ(buses, branches, prim);
                if (q > 0) AddShunt(prim, q * 0.5);          // Bs is MVAr at 1 pu
            }
            foreach (int gsp in zGsp132[z])
            {
                double q = DownstreamQ(buses, branches, gsp);
                if (q > 0) AddShunt(gsp, q * 0.4);
            }

            // Embedded generation at 33 kV  -  fixed PQ injections, the way distributed
            // plant actually runs (it follows the network's voltage; it doesn't set it).
            int nEmb = Math.Max(1, (int)Math.Round(share * 45));
            for (int e = 0; e < nEmb && all33.Count > 0; e++)
            {
                (string type, double lo, double hi, double cf) = embeddedTypes[rng.Next(embeddedTypes.Length)];
                double cap = lo + rng.NextDouble() * (hi - lo);
                // N108: below scale 1.0 the embedded plant shrinks with the zone's demand (same draw, then scaled).
                if (opt.Scale < 1.0) cap *= zoneFactor;
                int at = all33[rng.Next(all33.Count)];
                gens.Add(new Generator(at, Pg: cap * cf, Qg: 0, Vg: 1.0,
                    Qmin: 0, Qmax: 0, Pmin: 0, Pmax: cap,
                    MBase: cap, H: 0,
                    Name: $"{zone.Tag} embedded {type} {e + 1}", Type: type));
            }
        }

        // N108: below scale 1.0 the transmission fleet shrinks with its zone (capacity, dispatch, reactive limits and
        // machine base by the zone's factor; H is per unit on the machine base, so the zone's stored energy scales too),
        // so the smaller GB stays roughly balanced zone by zone and the slack is not left to absorb the whole fleet's
        // output. The fleet units are the first Fleet.Length generators, in Fleet order. Nothing runs at 1.0 and above.
        if (opt.Scale < 1.0)
        {
            for (int k = 0; k < Fleet.Length; k++)
            {
                double f = zoneFactors[Fleet[k].Zone];
                Generator g = gens[k];
                gens[k] = g with
                {
                    Pg = g.Pg * f, Qmin = g.Qmin * f, Qmax = g.Qmax * f, Pmax = g.Pmax * f, MBase = g.MBase * f,
                };
            }
        }

        return new GridModel
        {
            Name = $"GB synthetic",
            BaseMva = BaseMva,
            FrequencyHz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz,   // a GB model: the GB nominal (named, R196 / D2)
            Buses = buses,
            Generators = gens,
            Branches = branches,
            Oltcs = oltcs,
            Boundaries = new List<Boundary>(),
        };
    }

    /// <summary>
    /// N108: the factor a zone's demand, embedded plant and transmission fleet are multiplied by at a given scale: 1.0 at
    /// scale 1.0 and above (those networks are unchanged), else the zone's 11 kV secondaries built at this scale over the
    /// number it would get at scale 1.0. Below 1.0 each primary gets max(2, round(draw x scale)) secondaries for a draw
    /// of 4 to 7, which is never more than the draw, so the factor lies in (0, 1].
    /// </summary>
    /// <param name="scale">The build's <see cref="Options.Scale"/> (already checked finite and above zero).</param>
    /// <param name="secondariesBuilt">The zone's 11 kV secondaries at this scale.</param>
    /// <param name="secondariesAtUnitScale">The zone's 11 kV secondaries at scale 1.0 (the sum of the draws).</param>
    public static double ZoneScaleFactor(double scale, int secondariesBuilt, int secondariesAtUnitScale)
    {
        if (scale >= 1.0) return 1.0;
        if (secondariesAtUnitScale <= 0 || secondariesBuilt <= 0 || secondariesBuilt > secondariesAtUnitScale)
            throw new ArgumentOutOfRangeException(nameof(secondariesBuilt),
                $"SyntheticGb: a zone below scale 1.0 must build between 1 and {secondariesAtUnitScale} secondaries " +
                $"(its scale-1.0 count); it built {secondariesBuilt}.");
        return (double)secondariesBuilt / secondariesAtUnitScale;
    }

    /// <summary>
    /// N204 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): give every radial branch from
    /// <paramref name="firstBranch"/> to the end of <paramref name="branches"/> enough parallel units of its standard size
    /// to carry the peak demand of the subtree it feeds.
    ///
    /// <para><b>The rule.</b> For each branch, S = |P + jQ| of all the demand at and below its far-end bus (the bus
    /// demands of the subtree summed, then the magnitude taken). The branch's rating as built (750 MVA GSP SGT, 240 MVA
    /// industrial spur, 180 MVA 132/33 kV, 30 MVA 33/11 kV, 12 MVA 11/6.6 kV) is the size of ONE standard unit; the
    /// branch becomes n = ceil(S / unit) identical units in parallel, written as their single equivalent branch exactly
    /// as the JSON loader reads a line with <c>circuits: n</c>: R / n, X / n, B x n, rating x n. A branch whose demand
    /// fits one unit (n = 1) is not touched at all, so it stays bit-identical; at the default options that is every
    /// branch but 84 (counts per layer in data/gb-synthetic/README.md).</para>
    ///
    /// <para><b>The basis (best judgement, recorded).</b> A distribution transformer group is planned so its capacity
    /// covers the group demand it serves at peak: ENA EREC P2 Issue 8 (2023, docs/reference S142) Table 1 sets the
    /// demand to be met, by class of Group Demand, after first and second circuit outages, and clause 3.8 defines Group
    /// Demand as the maximum demand of the group INCLUDING the latent demand that distributed generation masks. This
    /// synthetic network is single-circuit by design (its radial ladder has no second path, N191), so only the
    /// intact-network part of that rule can be applied: installed capacity at least the peak group demand. The zone
    /// demands are GB peak-like figures, so the subtree demand stands for the winter peak. Embedded generation is
    /// therefore NOT netted off (clause 3.8), nor are the capacitor banks (they are sized after this from the same
    /// demand). The 400/275 kV supergrid transformers are meshed (their flow depends on the dispatch),
    /// so they are not part of this rule and keep their 1,000 MVA rating.</para>
    /// </summary>
    /// <param name="buses">Every bus built so far (the subtree demands are read from them).</param>
    /// <param name="branches">Every branch built so far; the radial ones from <paramref name="firstBranch"/> on are
    /// resized in place.</param>
    /// <param name="firstBranch">The index of the zone's first radial branch.</param>
    private static void SizeRadialBranchesToLoad(List<Bus> buses, List<Branch> branches, int firstBranch)
    {
        // The radial children of each bus, from this zone's ladder only (each ladder branch runs parent -> child).
        var children = new Dictionary<int, List<int>>();
        for (int k = firstBranch; k < branches.Count; k++)
        {
            Branch b = branches[k];
            if (!children.TryGetValue(b.From, out List<int>? l)) children[b.From] = l = new List<int>();
            l.Add(b.To);
        }
        var demandOf = new Dictionary<int, (double P, double Q)>(buses.Count);
        foreach (Bus bus in buses) demandOf[bus.Id] = (bus.Pd, bus.Qd);

        for (int k = firstBranch; k < branches.Count; k++)
        {
            Branch b = branches[k];

            // Sum the demand of the subtree under the branch's far end (depth-first; the ladder is a tree, the visited
            // set is only a guard).
            double p = 0, q = 0;
            var stack = new Stack<int>();
            var seen = new HashSet<int>();
            stack.Push(b.To);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (!seen.Add(id)) continue;
                (double pd, double qd) = demandOf[id];
                p += pd;
                q += qd;
                if (children.TryGetValue(id, out List<int>? kids))
                    foreach (int c in kids) stack.Push(c);
            }
            double sMva = Math.Sqrt(p * p + q * q);

            // How many standard units carry it. One unit: leave the branch exactly as built.
            double unitMva = b.RateAMva;
            int units = (int)Math.Ceiling(sMva / unitMva);
            if (units <= 1) continue;
            branches[k] = b with
            {
                R = b.R / units,
                X = b.X / units,
                B = b.B * units,
                RateAMva = unitMva * units,
            };
        }
    }

    /// <summary>Total reactive demand fed through a bus (its own plus everything below it in the tree).</summary>
    private static double DownstreamQ(List<Bus> buses, List<Branch> branches, int root)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (Branch b in branches)
        {
            if (!children.TryGetValue(b.From, out List<int>? l)) children[b.From] = l = new List<int>();
            l.Add(b.To);
        }
        double q = 0;
        var stack = new Stack<int>();
        var seen = new HashSet<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (!seen.Add(id)) continue;
            Bus? bus = buses.FirstOrDefault(b => b.Id == id);
            if (bus is not null) q += bus.Qd;
            if (children.TryGetValue(id, out List<int>? kids))
                foreach (int k in kids) stack.Push(k);
        }
        return q;
    }
}
