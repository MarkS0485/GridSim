using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Every shipped template library under data/library validates, as one set (so cross-file controller references
/// are checked too). Deliberately generic: it names no file and no template, and passes with any library file
/// present or absent, so a placeholder file such as placeholders/unmodelled-sites.json can be stripped by deleting
/// it, with no test to edit.
/// </summary>
public class V2LibraryFilesValidateTests
{
    // Test correction (2026-11-02, owner decision GR-404; recorded in the commit and CHANGELOG): this test returned early,
    // and so PASSED having checked nothing, when no library file was shipped. It is now SKIPPED with the reason in that
    // case (decided at discovery by the attribute), so a green run never reads as "the library was validated". Nothing
    // it checks when the library is present has changed.
    [RepositoryDataFact("the template library is optional, so with no library file there is nothing to validate",
        "data/library", "*.json")]
    public void Every_data_library_file_validates()
    {
        List<string> files = Directory.EnumerateFiles(LibraryDir, "*.json", SearchOption.AllDirectories)
                                      .OrderBy(f => f, StringComparer.Ordinal).ToList();
        // The attribute ran the test only because a library file exists; a mismatch here is a broken checkout.
        Assert.NotEmpty(files);

        JsonSchemaSubset schemas = RealSchemas();
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(KindsDir, schemas);
        V2LibraryChecker.ValidateFilesOrThrow(files, new SchemaValidator(schemas), kinds);   // throws listing every error
    }
}
