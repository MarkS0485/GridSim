namespace GridSim.Core.IO.PyPsa;

/// <summary>
/// The published GB reference networks bundled with GridSim (S2, October 2026): their built-in names and where their
/// files sit under the shipped <c>data/</c> folder. Provenance, licences and checksums are in
/// <c>data/matpower/gb/README.md</c>.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><see cref="Gb29Id"/></term><description>Strathclyde / Bukhsh GBreducednetwork.m: 29 buses, 99 branches,
/// 66 generators (MATPOWER; the PyPSA-GB copy, MIT, identical in data to the Edinburgh archive original).</description></item>
/// <item><term><see cref="PypsaReducedId"/></term><description>PyPSA-GB reduced network (CSV): the same 29 substations with
/// OSGB coordinates and PyPSA-GB's own 99 lines, demand and generation taken from GBreducednetwork.m.</description></item>
/// <item><term><see cref="Gb2224Id"/></term><description>Bukhsh / McKinnon GBnetwork.m: 2,224 buses, 3,207 branches, 394
/// generators (MATPOWER, the Edinburgh archive original).</description></item>
/// <item><term><see cref="PypsaZonalId"/></term><description>PyPSA-GB zonal model: 20 zones, 37 links; a transport model,
/// not AC solvable.</description></item>
/// </list>
/// Every path is relative to the data folder (the CLI's <c>&lt;install&gt;/data</c>), so the MATPOWER files also resolve
/// by bare name ("GBnetwork.m") through the CLI's data/matpower search, and they sit inside the bundled
/// <c>data/matpower</c> tree (Bundled Networks, never counted against the licence tier).
/// </remarks>
public static class GbReferenceModels
{
    /// <summary>Built-in name of the 29-bus Strathclyde reduced GB network (MATPOWER).</summary>
    public const string Gb29Id = "gb-reduced-29";
    /// <summary>Built-in name of the PyPSA-GB reduced network (CSV, AC solvable).</summary>
    public const string PypsaReducedId = "pypsa-gb-reduced";
    /// <summary>Built-in name of the 2,224-bus GB network (MATPOWER).</summary>
    public const string Gb2224Id = "gb-network-2224";
    /// <summary>Built-in name of the PyPSA-GB zonal transport model (not AC solvable).</summary>
    public const string PypsaZonalId = "pypsa-gb-zonal";

    /// <summary>GBreducednetwork.m, relative to the data folder.</summary>
    public static readonly string Gb29RelativePath = Path.Combine("matpower", "gb", "GBreducednetwork.m");
    /// <summary>GBnetwork.m, relative to the data folder.</summary>
    public static readonly string Gb2224RelativePath = Path.Combine("matpower", "gb", "GBnetwork.m");
    /// <summary>The PyPSA-GB reduced network folder, relative to the data folder.</summary>
    public static readonly string PypsaReducedRelativeDir = Path.Combine("matpower", "gb", "pypsa-gb", "reduced");
    /// <summary>The PyPSA-GB zonal model folder, relative to the data folder.</summary>
    public static readonly string PypsaZonalRelativeDir = Path.Combine("matpower", "gb", "pypsa-gb", "zonal");
}
