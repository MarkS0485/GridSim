using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N284 (CF2, October 2026): v2 kind specs cite a code default by SYMBOL wherever the value lives in a named member the
/// resolver can reach (GridSim.Core / GridSim.Gda), instead of quoting the exact source line, so re-formatting or
/// re-aligning a line no longer breaks the reference. Twenty references moved, no value changed: the seven
/// <c>ChemistryParameters</c> property initialisers (now <c>instance:</c>) and the thirteen synthetic GB fleet inertias
/// (now <c>member:</c> with a <c>[Name=...]</c> selector on <c>SyntheticGb.Fleet</c>). What stays <c>source:</c> is a
/// literal inside a method body (no symbol holds it) or a GridSim.Cli value (the resolver cannot reflect the CLI).
/// </summary>
public sealed partial class Cf2OpenCodeFindingsTests
{
    /// <summary>(kind, param, exact ref, value) for every converted entry.</summary>
    public static TheoryData<string, string, string, double> N284Converted()
    {
        var rows = new TheoryData<string, string, string, double>
        {
            { "storage", "cellSocMin", "instance:GridSim.Core:GridSim.Core.Storage.ChemistryParameters.SocMin", 0.05 },
            { "storage", "cellSocMax", "instance:GridSim.Core:GridSim.Core.Storage.ChemistryParameters.SocMax", 0.95 },
            { "storage", "coulombicEfficiency", "instance:GridSim.Core:GridSim.Core.Storage.ChemistryParameters.CoulombicEfficiency", 0.998 },
            { "storage", "peukertExponent", "instance:GridSim.Core:GridSim.Core.Storage.ChemistryParameters.PeukertExponent", 1.0 },
            { "storage", "deratingStartC", "instance:GridSim.Core:GridSim.Core.Storage.ChemistryParameters.TDerateC", 45.0 },
            { "storage", "deratingEndC", "instance:GridSim.Core:GridSim.Core.Storage.ChemistryParameters.TMaxC", 55.0 },
            { "storage", "minChargeTemperatureC", "instance:GridSim.Core:GridSim.Core.Storage.ChemistryParameters.TMinChargeC", 0.0 },
        };
        foreach ((string plant, double h) in new[]
        {
            ("North Scotland Hydro", 3.0), ("Torness Nuclear", 4.0), ("Heysham Nuclear", 4.0), ("North England CCGT", 5.0),
            ("Drax Biomass", 4.0), ("Yorkshire CCGT", 5.0), ("North Wales CCGT", 5.0), ("Midlands CCGT (marginal)", 5.0),
            ("Sizewell B Nuclear", 4.0), ("London CCGT", 5.0), ("South CCGT", 5.0), ("Hinkley Point Nuclear", 4.0),
            ("Pembroke CCGT", 5.0),
        })
            rows.Add("synchronousMachine", "inertiaH", $"member:GridSim.Core:GridSim.Core.Synthetic.SyntheticGb.Fleet[Name={plant}].H", h);
        return rows;
    }

    [Theory]
    [MemberData(nameof(N284Converted))]
    public void N284_each_converted_reference_is_present_once_and_resolves_to_its_unchanged_value(string kind, string param, string reference, double value)
    {
        var hits = KindFileSet.Load(kind).GetProperty("params").GetProperty(param).GetProperty("codeDefaults").EnumerateArray()
            .Where(c => c.GetProperty("ref").GetString() == reference)
            .ToList();
        JsonElement entry = Assert.Single(hits);
        Assert.Equal(value, entry.GetProperty("value").GetDouble());
        Assert.Null(CodeDefaultRefResolver.Mismatch(reference, entry.GetProperty("value")));
    }

    [Fact]
    public void N284_no_source_reference_quotes_a_named_member_the_resolver_could_reach()
    {
        // A source: snippet that is a property initialiser, a const or static readonly declaration, or a row of the
        // SyntheticGb fleet, in a reflectable assembly, must be written as a symbol reference instead.
        var namedMember = new Regex(@"(\{ get; (init|set); \} =|\bconst\s+\w+\s+\w+\s*=|\bstatic readonly\s+\w+\s+\w+\s*=)", RegexOptions.CultureInvariant);
        var offenders = new List<string>();
        int sourceRefs = 0;
        foreach ((string kind, JsonElement spec) in KindFileSet.LoadAll())
            foreach (JsonProperty p in spec.GetProperty("params").EnumerateObject())
                foreach (JsonElement c in p.Value.GetProperty("codeDefaults").EnumerateArray())
                {
                    string r = c.GetProperty("ref").GetString()!;
                    if (!r.StartsWith("source:", StringComparison.Ordinal)) continue;
                    sourceRefs++;
                    if (r.StartsWith("source:src/GridSim.Cli/", StringComparison.Ordinal)) continue;   // not reflectable
                    if (namedMember.IsMatch(r) || r.StartsWith("source:src/GridSim.Core/Synthetic/SyntheticGb.cs:new(\"", StringComparison.Ordinal))
                        offenders.Add($"{kind}.{p.Name}: {r}");
                }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        // 206 source: references before N284, 20 converted.
        // Moved 2026-10 (END-PASS CF3 row 3, end pass; N345, every kind-spec code default cites a named symbol, Ruling R1
        // and Mark's go on the end-pass moves 2026-10-11): N345 named the remaining 186 literals (147 Core / Gda members, 39
        // CLI members read from the built CLI; entry total 591 unchanged), so no source: reference is left. Before ->
        // after: 186 -> 0 (observed: Expected 186, Actual 0, 1.95.273-275 runs).
        Assert.Equal(0, sourceRefs);
    }
}
