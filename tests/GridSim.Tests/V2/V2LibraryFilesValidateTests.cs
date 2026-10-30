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
    [Fact]
    public void Every_data_library_file_validates()
    {
        if (!Directory.Exists(LibraryDir)) return;   // no library shipped: nothing to validate
        List<string> files = Directory.EnumerateFiles(LibraryDir, "*.json", SearchOption.AllDirectories)
                                      .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) return;

        JsonSchemaSubset schemas = RealSchemas();
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(KindsDir, schemas);
        V2LibraryChecker.ValidateFilesOrThrow(files, new SchemaValidator(schemas), kinds);   // throws listing every error
    }
}
