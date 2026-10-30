using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The honesty discipline: any parameter tagged <see cref="ProvenanceTag.CuratedPlaceholder"/> MUST
/// carry the literal <c>[VERIFY]</c> token, enforced at load - mirrors <c>EvidenceReportTests</c>.
/// </summary>
public class AssetProvenanceTests
{
    private static AssetClass ClassWith(ProvenanceTag tag, string note)
        => new("c", AssetKind.GridFollowingInverter, "grid-following",
            new Dictionary<string, double> { [AssetParams.RocofTripHzPerS] = 1.0 },
            new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(tag, note) });

    [Fact]
    public void Curated_placeholder_without_verify_is_a_violation()
    {
        AssetClass bad = ClassWith(ProvenanceTag.CuratedPlaceholder, "approximated from a datasheet");
        Assert.NotEmpty(bad.ValidateProvenance());
        Assert.Throws<System.IO.InvalidDataException>(() => new AssetClassLibrary(new[] { bad }).ValidateOrThrow());
    }

    [Fact]
    public void Curated_placeholder_with_verify_is_clean()
    {
        AssetClass ok = ClassWith(ProvenanceTag.CuratedPlaceholder, "approximated from literature [VERIFY]");
        Assert.Empty(ok.ValidateProvenance());
        new AssetClassLibrary(new[] { ok }).ValidateOrThrow();   // must not throw
    }

    [Fact]
    public void Recorded_and_derived_tags_do_not_require_verify()
    {
        Assert.Empty(ClassWith(ProvenanceTag.Recorded, "NESO published setting").ValidateProvenance());
        Assert.Empty(ClassWith(ProvenanceTag.DerivedFromRecorded, "from GDA ramp bands").ValidateProvenance());
    }

    [Fact]
    public void Provenance_for_a_parameter_falls_back_to_default_then_synthetic()
    {
        AssetClass c = ClassWith(ProvenanceTag.CuratedPlaceholder, "default note [VERIFY]");
        Assert.Equal("default note [VERIFY]", c.ProvenanceFor("anyParam").Note);   // via _default
    }
}
