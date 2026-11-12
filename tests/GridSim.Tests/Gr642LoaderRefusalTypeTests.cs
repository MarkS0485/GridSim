using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-642: the case editor took InvalidOperationException, KeyNotFoundException, FormatException and NotSupportedException
/// from a loader run as a problem of the case, so a loader defect of those types was shown as a refusal of the case. The
/// owner's order: first the loaders turn System.Text.Json's own non-JsonException refusals of the input into the loader's
/// refusal type (InvalidDataException naming the file), then the editor catches only the loader's types. Pinned: a real
/// System.Text.Json InvalidOperationException / KeyNotFoundException / FormatException is recognised and the same types
/// raised by other code are not; the editor's catch takes InvalidDataException, JsonException, IOException and
/// ArgumentException and no longer the four others; both loaders wrap at their entry (source pins).
/// </summary>
public sealed class Gr642LoaderRefusalTypeTests
{
    private static Exception Caught(Action a)
    {
        try { a(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("expected an exception");
    }

    [Fact]
    public void Gr642_system_text_json_raw_refusals_are_recognised()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"a\": \"text\"}");
        JsonElement root = doc.RootElement;
        Exception wrongKind = Caught(() => root.GetProperty("a").GetDouble());
        Exception missing = Caught(() => root.GetProperty("absent"));
        Assert.IsType<InvalidOperationException>(wrongKind);
        Assert.IsType<KeyNotFoundException>(missing);
        Assert.True(JsonGridLoader.IsRawJsonFailure(wrongKind));
        Assert.True(JsonGridLoader.IsRawJsonFailure(missing));
    }

    [Fact]
    public void Gr642_the_same_types_from_other_code_are_not_recognised()
    {
        Exception own = Caught(() => throw new InvalidOperationException("a defect"));
        Exception lookup = Caught(() => _ = new Dictionary<string, int>()["absent"]);
        Assert.False(JsonGridLoader.IsRawJsonFailure(own));
        Assert.False(JsonGridLoader.IsRawJsonFailure(lookup));   // System.Private.CoreLib, not System.Text.Json
        Assert.False(JsonGridLoader.IsRawJsonFailure(new InvalidDataException("x")));
    }

    [Fact]
    public void Gr642_the_editor_catches_only_the_loaders_refusal_types()
    {
        Assert.True(CaseSession.IsLoaderFailure(new InvalidDataException("x")));
        Assert.True(CaseSession.IsLoaderFailure(new JsonException("x")));
        Assert.True(CaseSession.IsLoaderFailure(new FileNotFoundException("x")));
        Assert.True(CaseSession.IsLoaderFailure(new ArgumentOutOfRangeException("x")));
        Assert.False(CaseSession.IsLoaderFailure(new InvalidOperationException("x")));
        Assert.False(CaseSession.IsLoaderFailure(new KeyNotFoundException("x")));
        Assert.False(CaseSession.IsLoaderFailure(new FormatException("x")));
        Assert.False(CaseSession.IsLoaderFailure(new NotSupportedException("x")));
        Assert.False(CaseSession.IsLoaderFailure(new ArithmeticException("x")));   // any other type (CA2201 forbids constructing NullReferenceException)
    }

    [Fact]
    public void Gr642_both_loaders_wrap_raw_json_refusals_as_InvalidDataException()
    {
        string core = Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "IO", "Json");
        string v1 = File.ReadAllText(Path.Combine(core, "JsonGridLoader.cs")).Replace("\r\n", "\n");
        string v2 = File.ReadAllText(Path.Combine(core, "V2CaseLoader.cs")).Replace("\r\n", "\n");
        Assert.Contains("catch (Exception ex) when (IsRawJsonFailure(ex))\n        {", v1, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (JsonGridLoader.IsRawJsonFailure(ex))\n        {\n            throw new InvalidDataException(", v2, StringComparison.Ordinal);
    }
}
