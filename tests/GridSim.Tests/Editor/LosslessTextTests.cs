using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.IO.Json.Lossless;
using Xunit;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// Slice E1 of the case editor (SCHEMA-EDITOR-PLAN.md section 10): the lossless JSON text layer. Proofs P7 (number
/// literals round-trip bit for bit), P8 (string literals), P9 (structural edits in every bundled layout style), byte
/// identity on every bundled JSON file, and the characterisation of why JsonNode is not used.
/// </summary>
public class LosslessTextTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>The v1 reader leniency (comments, trailing commas), for building JsonNode expectations.</summary>
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // =================================================================================================================
    // P7: number literals
    // =================================================================================================================

    /// <summary>The awkward doubles: zeros, the extremes, subnormals, epsilon, the classic 0.1 + 0.2.</summary>
    private static IEnumerable<double> SpecialDoubles()
    {
        yield return 0.0;
        yield return -0.0;
        yield return double.MaxValue;
        yield return double.MinValue;
        yield return double.Epsilon;
        yield return -double.Epsilon;
        yield return BitConverter.Int64BitsToDouble(0x000FFFFFFFFFFFFFL);   // largest subnormal
        yield return BitConverter.Int64BitsToDouble(0x0010000000000000L);   // smallest normal
        yield return 0.1 + 0.2;
        yield return 0.1;
        yield return 1.0 / 3.0;
        yield return 1300;
        yield return 266424.9878610732;
        yield return 1e21;
        yield return 1e-7;
        yield return 9007199254740993.0;
        yield return Math.PI;
    }

    [Fact]
    public void P7_every_formatted_number_reads_back_with_identical_bits()
    {
        // About 10k doubles from a fixed seed (random bit patterns cover every exponent, unlike NextDouble), plus the
        // special values. Each literal must read back to the same 64 bits through BOTH System.Text.Json paths the code
        // uses: DTO deserialisation (the loader) and JsonElement.GetDouble (the schema validator and the editor).
        var rng = new Random(20261003);
        var values = new List<double>(SpecialDoubles());
        var buffer = new byte[8];
        while (values.Count < 10_000)
        {
            rng.NextBytes(buffer);
            double d = BitConverter.ToDouble(buffer);
            if (double.IsFinite(d)) values.Add(d);
        }
        foreach (double d in values)
        {
            string s = JsonLiteral.FormatNumber(d);
            long bits = BitConverter.DoubleToInt64Bits(d);
            Assert.Equal(bits, BitConverter.DoubleToInt64Bits(JsonSerializer.Deserialize<double>(s)));
            using JsonDocument doc = JsonDocument.Parse(s);
            Assert.Equal(bits, BitConverter.DoubleToInt64Bits(doc.RootElement.GetDouble()));
            // The literal is also acceptable typed input, and typing it gives the same bits back.
            Assert.True(JsonLiteral.TryParseNumber(s, out double typed, out string why), why);
            Assert.Equal(bits, BitConverter.DoubleToInt64Bits(typed));
        }
    }

    [Fact]
    public void P7_shortest_form_is_written_whatever_was_typed()
    {
        // Q2 resolved to the plan's recommendation: the shortest round-trip literal, not the typed characters.
        Assert.True(JsonLiteral.TryParseNumber("1300.0", out double a, out _));
        Assert.True(JsonLiteral.TryParseNumber("1.3e3", out double b, out _));
        Assert.Equal("1300", JsonLiteral.FormatNumber(a));
        Assert.Equal("1300", JsonLiteral.FormatNumber(b));
        Assert.Equal("0.30000000000000004", JsonLiteral.FormatNumber(0.1 + 0.2));
        Assert.Equal("-0", JsonLiteral.FormatNumber(-0.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void P7_non_finite_numbers_are_refused(double d)
        => Assert.Throws<ArgumentOutOfRangeException>(() => JsonLiteral.FormatNumber(d));

    [Theory]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1,5")]
    [InlineData("1 000")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0x10")]
    [InlineData("1e400")]
    [InlineData("")]
    public void P7_typed_numbers_outside_the_JSON_grammar_or_range_are_refused(string typed)
    {
        Assert.False(JsonLiteral.TryParseNumber(typed, out _, out string why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e3")]
    [InlineData("007")]
    public void P7_typed_integers_must_be_plain_digits_in_the_int32_range(string typed)
        => Assert.False(JsonLiteral.TryParseInt32(typed, out _, out _));

    [Fact]
    public void P7_integers_are_written_as_digits()
    {
        Assert.Equal("-2147483648", JsonLiteral.FormatInteger(int.MinValue));
        Assert.True(JsonLiteral.TryParseInt32(" 42 ", out int v, out _));
        Assert.Equal(42, v);
    }

    // =================================================================================================================
    // P8: string literals
    // =================================================================================================================

    [Theory]
    [InlineData("SW Peninsula & S Wales (export)")]
    [InlineData("quote \" inside")]
    [InlineData("back\\slash")]
    [InlineData("tab\tnew\nline\rcr\bbs\fff\u0001\u001f")]
    [InlineData("Straße")]
    [InlineData("Pezilla-la-Rivière")]
    [InlineData("emoji \U0001F600 pair")]
    [InlineData("")]
    public void P8_every_formatted_string_decodes_back_equal(string s)
    {
        string literal = JsonLiteral.FormatString(s);
        Assert.Equal(s, JsonSerializer.Deserialize<string>(literal));
        using JsonDocument doc = JsonDocument.Parse(literal);
        Assert.Equal(s, doc.RootElement.GetString());
    }

    [Fact]
    public void P8_ampersand_and_non_ASCII_are_written_raw_not_escaped()
    {
        // The uk files hold a raw '&' and the intl files raw UTF-8 letters; a new value must look like its neighbours.
        Assert.Equal("\"A & B\"", JsonLiteral.FormatString("A & B"));
        Assert.Equal("\"Straße\"", JsonLiteral.FormatString("Straße"));
        Assert.Equal("\"a\\u0001b\"", JsonLiteral.FormatString("a\u0001b"));
    }

    // The strings are built in code: a lone surrogate written into an attribute argument does not survive compilation
    // (the compiler stores attribute strings as UTF-8, which replaces it with U+FFFD).
    [Theory]
    [InlineData(0xD800, " high")]
    [InlineData(0xDC00, " low")]
    [InlineData(0xD83D, "")]           // a high surrogate at the very end
    public void P8_unpaired_surrogates_are_refused(int surrogate, string after)
    {
        string s = "bad " + (char)surrogate + after;
        Assert.Throws<ArgumentException>(() => JsonLiteral.FormatString(s));
        Assert.True(JsonLiteral.HasUnpairedSurrogate(s));
    }

    // =================================================================================================================
    // Parsing: spans, BOM, newline, duplicates
    // =================================================================================================================

    [Fact]
    public void Spans_cover_exactly_each_token()
    {
        string json = "// lead\r\n{ \"a\" : 1.50 , \"b\":[true,null, \"x\\u0026y\"], /* mid */ \"c\": {} }\r\n";
        JsonSourceText t = JsonSourceText.Parse(Utf8(json), "t.json", Ci);
        Assert.Equal("\r\n", t.Newline);
        Assert.True(t.TrailingNewline);
        Assert.False(t.HasBom);
        JsonSourceNode root = t.Root;
        Assert.Equal(JsonSourceKind.Object, root.Kind);
        Assert.Equal(json.IndexOf('{', StringComparison.Ordinal), root.Start);
        Assert.Equal(json.LastIndexOf('}') + 1, root.End);
        Assert.Equal("1.50", t.RawText(root.Members[0].Value));
        Assert.Equal(1.5, root.Members[0].Value.NumberValue);
        Assert.Equal("\"a\"", t.Slice(root.Members[0].NameStart, root.Members[0].NameEnd));
        Assert.Equal("[true,null, \"x\\u0026y\"]", t.RawText(root.Members[1].Value));
        Assert.Equal("\"x\\u0026y\"", t.RawText(root.Members[1].Value.Elements[2]));
        Assert.Equal("x&y", root.Members[1].Value.Elements[2].StringValue);
        Assert.Equal("{}", t.RawText(root.Members[2].Value));
        Assert.Same(root.Members[1].Value.Elements[0], t.Resolve("/B/0"));   // v1 lookup ignores case
        Assert.Null(t.Resolve("/b/3"));
        Assert.Null(t.Resolve("/b/01"));
    }

    [Fact]
    public void A_BOM_is_kept_out_of_every_span()
    {
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("[1, 2]")).ToArray();
        JsonSourceText t = JsonSourceText.Parse(bytes, "bom.json", Ci);
        Assert.True(t.HasBom);
        Assert.Equal(3, t.Root.Start);
        Assert.Equal("2", t.RawText(t.Root.Elements[1]));
        Assert.Equal(JsonValueKind.Array, t.ToElement().ValueKind);
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}", true, true)]
    [InlineData("{\"a\":1,\"a\":2}", false, true)]
    [InlineData("{\"bus\":1,\"Bus\":2}", true, true)]     // the same member to the case-insensitive v1 loader
    [InlineData("{\"bus\":1,\"Bus\":2}", false, false)]   // two different members in v2
    public void Duplicate_member_names_are_refused_under_the_files_comparer(string json, bool v1, bool refused)
    {
        StringComparer comparer = v1 ? Ci : StringComparer.Ordinal;
        if (refused)
        {
            var ex = Assert.Throws<InvalidDataException>(() => JsonSourceText.Parse(Utf8(json), "dup.json", comparer));
            Assert.Contains("dup.json", ex.Message, StringComparison.Ordinal);
            Assert.Contains("duplicate member", ex.Message, StringComparison.Ordinal);
        }
        else Assert.Equal(2, JsonSourceText.Parse(Utf8(json), "dup.json", comparer).Root.Members.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("// only a comment")]
    [InlineData("[1, 2")]
    [InlineData("[1] [2]")]
    [InlineData("{\"a\" 1}")]
    public void Malformed_files_are_refused_naming_the_file(string json)
    {
        var ex = Assert.Throws<InvalidDataException>(() => JsonSourceText.Parse(Utf8(json), "bad.json", Ci));
        Assert.StartsWith("bad.json:", ex.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // Edits: the patch primitive
    // =================================================================================================================

    [Fact]
    public void Apply_returns_an_inverse_that_restores_the_original_exactly()
    {
        byte[] original = Utf8("abcdefghij");
        var edits = new[]
        {
            JsonTextEdit.InsertText(0, "XX"),
            JsonTextEdit.ReplaceText(2, 3, "Y"),
            JsonTextEdit.Remove(7, 2),
            JsonTextEdit.InsertText(10, "!"),
        };
        (byte[] next, IReadOnlyList<JsonTextEdit> inverse) = JsonTextEdit.Apply(original, edits);
        Assert.Equal("XXabYfgj!", Encoding.UTF8.GetString(next));
        (byte[] back, _) = JsonTextEdit.Apply(next, inverse);
        Assert.Equal(original, back);
    }

    [Fact]
    public void Overlapping_or_unsorted_edits_are_refused()
    {
        byte[] t = Utf8("abcdef");
        Assert.Throws<ArgumentException>(() => JsonTextEdit.Apply(t, new[] { JsonTextEdit.Remove(2, 2), JsonTextEdit.Remove(3, 1) }));
        Assert.Throws<ArgumentException>(() => JsonTextEdit.Apply(t, new[] { JsonTextEdit.Remove(4, 1), JsonTextEdit.Remove(1, 1) }));
        Assert.Throws<ArgumentException>(() => JsonTextEdit.Apply(t, new[] { JsonTextEdit.Remove(5, 2) }));
    }

    // =================================================================================================================
    // P9: structural edits in every style
    // =================================================================================================================

    /// <summary>
    /// Synthetic arrays of records in each layout the bundled data uses, plus the awkward cases: comments, CRLF, a BOM,
    /// a trailing comma. Every one holds three records with members name/bus/capacityMva so the same operations apply.
    /// </summary>
    public static TheoryData<string, string> Styles() => new()
    {
        { "uk aligned CRLF", "[\r\n  { \"name\": \"A & B\",   \"bus\": 1,  \"capacityMva\": 1300 },\r\n  { \"name\": \"C\",       \"bus\": 2,  \"capacityMva\": 50.5 },\r\n\r\n  { \"name\": \"D\",       \"bus\": 3,  \"capacityMva\": 7 }\r\n]\r\n" },
        { "gb-full indent 2 LF no trailing newline", "[\n  {\n    \"name\": \"A\",\n    \"bus\": 1,\n    \"capacityMva\": 0.0\n  },\n  {\n    \"name\": \"B\",\n    \"bus\": 2,\n    \"capacityMva\": 1.5\n  },\n  {\n    \"name\": \"D\",\n    \"bus\": 3,\n    \"capacityMva\": 7.0\n  }\n]" },
        { "STJ escaped", "[\r\n  {\r\n    \"name\": \"A \\u0026 B\",\r\n    \"bus\": 1,\r\n    \"capacityMva\": 1300\r\n  },\r\n  {\r\n    \"name\": \"C\",\r\n    \"bus\": 2,\r\n    \"capacityMva\": 50\r\n  },\r\n  {\r\n    \"name\": \"D\",\r\n    \"bus\": 3,\r\n    \"capacityMva\": 7\r\n  }\r\n]" },
        { "comments", "// generators\n[\n  /* first */ {\"name\": \"A\", \"bus\": 1, \"capacityMva\": 1}, // one\n  {\"name\": \"B\", /* inner */ \"bus\": 2, \"capacityMva\": 2},\n  {\"name\": \"D\", \"bus\": 3, \"capacityMva\": 3} // last\n]\n" },
        { "BOM CRLF", "﻿[\r\n  {\"name\":\"A\",\"bus\":1,\"capacityMva\":1},\r\n  {\"name\":\"B\",\"bus\":2,\"capacityMva\":2},\r\n  {\"name\":\"D\",\"bus\":3,\"capacityMva\":3}\r\n]\r\n" },
        { "trailing commas", "[{\"name\":\"A\",\"bus\":1,\"capacityMva\":1,},{\"name\":\"B\",\"bus\":2,\"capacityMva\":2},{\"name\":\"D\",\"bus\":3,\"capacityMva\":3},]" },
        { "intl raw UTF-8", "[\r\n  {\r\n    \"name\": \"Pezilla-la-Rivière\",\r\n    \"bus\": 1,\r\n    \"capacityMva\": 12.0\r\n  },\r\n  {\r\n    \"name\": \"Straße\",\r\n    \"bus\": 2,\r\n    \"capacityMva\": 3.0\r\n  },\r\n  {\r\n    \"name\": \"D\",\r\n    \"bus\": 3,\r\n    \"capacityMva\": 1.0\r\n  }\r\n]" },
    };

    /// <summary>One structural operation, done both on the lossless text and (as the expectation) on a JsonNode tree.</summary>
    private sealed record Op(string Name, Func<JsonSourceText, IReadOnlyList<JsonTextEdit>> Text, Action<JsonArray> Expect);

    private static readonly JsonFresh FreshRecord = JsonFresh.Object(new[]
    {
        new KeyValuePair<string, JsonFresh>("name", JsonFresh.Scalar(JsonLiteral.FormatString("New & ß"))),
        new KeyValuePair<string, JsonFresh>("bus", JsonFresh.Scalar("9")),
        new KeyValuePair<string, JsonFresh>("capacityMva", JsonFresh.Scalar(JsonLiteral.FormatNumber(0.1 + 0.2))),
        new KeyValuePair<string, JsonFresh>("zip", JsonFresh.Object(new[]
        {
            new KeyValuePair<string, JsonFresh>("ap", JsonFresh.Scalar("1")),
            new KeyValuePair<string, JsonFresh>("bp", JsonFresh.Scalar("0")),
        })),
    });

    private static JsonNode FreshNode() => JsonNode.Parse("{\"name\": \"New & ß\", \"bus\": 9, \"capacityMva\": 0.30000000000000004, \"zip\": {\"ap\": 1, \"bp\": 0}}")!;

    private static IEnumerable<Op> Ops()
    {
        JsonSourceNode Rec(JsonSourceText t, int i) => t.Root.Elements[i];
        JsonContainerStyle Style0(JsonSourceText t) => JsonLayout.Detect(t, Rec(t, 0));

        yield return new Op("replace scalar", t => JsonTextOps.ReplaceValue(t, Rec(t, 1).Members[2].Value, "1400"),
            a => a[1]!["capacityMva"] = 1400);
        yield return new Op("replace string", t => JsonTextOps.ReplaceValue(t, Rec(t, 2).Members[0].Value, JsonLiteral.FormatString("Z\"q\"")),
            a => a[2]!["name"] = "Z\"q\"");
        yield return new Op("insert member middle", t => JsonTextOps.InsertMember(t, Rec(t, 0), 2, "type", JsonFresh.Scalar("\"hydro\"")),
            a => InsertAt(a[0]!.AsObject(), 2, "type", "hydro"));
        yield return new Op("insert member first", t => JsonTextOps.InsertMember(t, Rec(t, 1), 0, "role", JsonFresh.Scalar("\"slack\"")),
            a => InsertAt(a[1]!.AsObject(), 0, "role", "slack"));
        yield return new Op("insert member last (object)", t => JsonTextOps.InsertMember(t, Rec(t, 2), 3, "zip",
                JsonFresh.Object(new[] { new KeyValuePair<string, JsonFresh>("ap", JsonFresh.Scalar("1")) })),
            a => a[2]!.AsObject().Add("zip", JsonNode.Parse("{\"ap\":1}")));
        yield return new Op("remove member first", t => JsonTextOps.RemoveMember(t, Rec(t, 0), 0), a => a[0]!.AsObject().Remove("name"));
        yield return new Op("remove member last", t => JsonTextOps.RemoveMember(t, Rec(t, 1), 2), a => a[1]!.AsObject().Remove("capacityMva"));
        yield return new Op("remove member middle", t => JsonTextOps.RemoveMember(t, Rec(t, 2), 1), a => a[2]!.AsObject().Remove("bus"));
        yield return new Op("insert element first", t => JsonTextOps.InsertElement(t, t.Root, 0, FreshRecord, Style0(t)), a => a.Insert(0, FreshNode()));
        yield return new Op("insert element middle", t => JsonTextOps.InsertElement(t, t.Root, 2, FreshRecord, Style0(t)), a => a.Insert(2, FreshNode()));
        yield return new Op("insert element last", t => JsonTextOps.InsertElement(t, t.Root, 3, FreshRecord, Style0(t)), a => a.Add(FreshNode()));
        yield return new Op("insert element no style", t => JsonTextOps.InsertElement(t, t.Root, 1, FreshRecord, null), a => a.Insert(1, FreshNode()));
        yield return new Op("copy first", t => JsonTextOps.CopyElement(t, t.Root, 0), a => a.Insert(1, a[0]!.DeepClone()));
        yield return new Op("copy middle", t => JsonTextOps.CopyElement(t, t.Root, 1), a => a.Insert(2, a[1]!.DeepClone()));
        yield return new Op("copy last", t => JsonTextOps.CopyElement(t, t.Root, 2), a => a.Add(a[2]!.DeepClone()));
        yield return new Op("remove element first", t => JsonTextOps.RemoveElement(t, t.Root, 0), a => a.RemoveAt(0));
        yield return new Op("remove element middle", t => JsonTextOps.RemoveElement(t, t.Root, 1), a => a.RemoveAt(1));
        yield return new Op("remove element last", t => JsonTextOps.RemoveElement(t, t.Root, 2), a => a.RemoveAt(2));
    }

    private static void InsertAt(JsonObject o, int index, string name, string value)
    {
        var pairs = o.ToList();
        o.Clear();
        for (int i = 0; i <= pairs.Count; i++)
        {
            if (i == index) o.Add(name, value);
            if (i < pairs.Count) o.Add(pairs[i].Key, pairs[i].Value);
        }
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void P9_every_structural_edit_reparses_to_the_expected_value_and_undoes_exactly(string style, string json)
    {
        byte[] original = Utf8(json);
        JsonSourceText text = JsonSourceText.Parse(original, style, Ci);
        foreach (Op op in Ops())
        {
            (JsonSourceText after, IReadOnlyList<JsonTextEdit> inverse) = text.Apply(op.Text(text));

            // The semantic expectation: the same operation on a JsonNode tree of the original.
            JsonArray expected = JsonNode.Parse(text.ToElement().GetRawText(), documentOptions: Lenient)!.AsArray();
            op.Expect(expected);
            JsonElement want = Element(expected.ToJsonString());
            Assert.True(JsonElement.DeepEquals(want, after.ToElement()),
                $"{style} / {op.Name}: got{Environment.NewLine}{Encoding.UTF8.GetString(after.ToArray())}");

            // The file-level facts never change.
            Assert.Equal(text.HasBom, after.HasBom);
            Assert.Equal(text.Newline, after.Newline);
            Assert.Equal(text.TrailingNewline, after.TrailingNewline);

            // A new line uses only the file's own newline (no bare "\n" sneaks into a CRLF file).
            if (text.Newline == "\r\n")
            {
                string s = Encoding.UTF8.GetString(after.ToArray());
                for (int i = 0; i < s.Length; i++)
                    if (s[i] == '\n') Assert.True(i > 0 && s[i - 1] == '\r', $"{style} / {op.Name}: bare LF at char {i}");
            }

            // Undo restores the original bytes exactly.
            (JsonSourceText back, _) = after.Apply(inverse);
            Assert.Equal(original, back.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void P9_removing_the_only_element_and_adding_to_an_empty_array_work(string style, string json)
    {
        JsonSourceText t = JsonSourceText.Parse(Utf8(json), style, Ci);
        // Remove down to nothing: last, last, only.
        for (int n = 3; n > 0; n--) t = t.Apply(JsonTextOps.RemoveElement(t, t.Root, n - 1)).Text;
        Assert.Empty(t.Root.Elements);
        Assert.Equal(JsonValueKind.Array, t.ToElement().ValueKind);
        // Add one record to the empty array; it lands on its own line.
        t = t.Apply(JsonTextOps.InsertElement(t, t.Root, 0, FreshRecord, null)).Text;
        Assert.True(JsonElement.DeepEquals(Element("[" + FreshNode().ToJsonString() + "]"), t.ToElement()), style);
    }

    [Fact]
    public void P9_a_member_can_be_removed_from_and_added_to_a_one_member_object()
    {
        JsonSourceText t = JsonSourceText.Parse(Utf8("[ { \"bus\": 1 } ]"), "x.json", Ci);
        t = t.Apply(JsonTextOps.RemoveMember(t, t.Root.Elements[0], 0)).Text;
        Assert.Equal("[ {} ]", Encoding.UTF8.GetString(t.ToArray()));
        t = t.Apply(JsonTextOps.InsertMember(t, t.Root.Elements[0], 0, "bus", JsonFresh.Scalar("2"))).Text;
        Assert.Equal("[ { \"bus\": 2 } ]", Encoding.UTF8.GetString(t.ToArray()));
    }

    // Exact layouts: what a reviewer sees in a diff.

    [Fact]
    public void A_scalar_edit_changes_exactly_one_token()
    {
        string json = "[\r\n  { \"name\": \"A & B\",   \"bus\": 1,  \"capacityMva\": 1300, \"capacityFactor\": 0.50 }\r\n]\r\n";
        JsonSourceText t = JsonSourceText.Parse(Utf8(json), "g.json", Ci);
        t = t.Apply(JsonTextOps.ReplaceValue(t, t.Root.Elements[0].Members[2].Value, "1400")).Text;
        Assert.Equal(json.Replace("1300", "1400", StringComparison.Ordinal), Encoding.UTF8.GetString(t.ToArray()));
    }

    [Fact]
    public void A_member_inserted_into_a_one_line_record_takes_the_one_line_style()
    {
        string json = "[\r\n  { \"name\": \"A\",   \"bus\": 1 },\r\n  { \"name\": \"B\",   \"bus\": 2 }\r\n]\r\n";
        JsonSourceText t = JsonSourceText.Parse(Utf8(json), "g.json", Ci);
        t = t.Apply(JsonTextOps.InsertMember(t, t.Root.Elements[1], 1, "type", JsonFresh.Scalar("\"hydro\""))).Text;
        Assert.Equal("[\r\n  { \"name\": \"A\",   \"bus\": 1 },\r\n  { \"name\": \"B\", \"type\": \"hydro\",   \"bus\": 2 }\r\n]\r\n",
            Encoding.UTF8.GetString(t.ToArray()));
    }

    [Fact]
    public void A_member_inserted_into_an_indented_record_takes_its_own_line()
    {
        string json = "[\n  {\n    \"name\": \"A\",\n    \"bus\": 1\n  }\n]";
        JsonSourceText t = JsonSourceText.Parse(Utf8(json), "g.json", Ci);
        t = t.Apply(JsonTextOps.InsertMember(t, t.Root.Elements[0], 2, "zip", JsonFresh.Object(new[]
        {
            new KeyValuePair<string, JsonFresh>("ap", JsonFresh.Scalar("1")),
        }))).Text;
        Assert.Equal("[\n  {\n    \"name\": \"A\",\n    \"bus\": 1,\n    \"zip\": {\n      \"ap\": 1\n    }\n  }\n]", Encoding.UTF8.GetString(t.ToArray()));
    }

    [Fact]
    public void A_fresh_record_in_an_indented_file_matches_its_neighbours()
    {
        string json = "[\n  {\n    \"name\": \"A\",\n    \"bus\": 1\n  }\n]";
        JsonSourceText t = JsonSourceText.Parse(Utf8(json), "g.json", Ci);
        JsonFresh rec = JsonFresh.Object(new[]
        {
            new KeyValuePair<string, JsonFresh>("name", JsonFresh.Scalar("\"B\"")),
            new KeyValuePair<string, JsonFresh>("bus", JsonFresh.Scalar("2")),
        });
        t = t.Apply(JsonTextOps.InsertElement(t, t.Root, 1, rec, JsonLayout.Detect(t, t.Root.Elements[0]))).Text;
        Assert.Equal("[\n  {\n    \"name\": \"A\",\n    \"bus\": 1\n  },\n  {\n    \"name\": \"B\",\n    \"bus\": 2\n  }\n]", Encoding.UTF8.GetString(t.ToArray()));
    }

    [Fact]
    public void A_duplicate_copies_the_source_bytes_including_alignment_without_blank_lines()
    {
        string json = "[\r\n  { \"name\": \"A\",   \"bus\": 1 },\r\n\r\n  { \"name\": \"B\",   \"bus\": 2 }\r\n]\r\n";
        JsonSourceText t = JsonSourceText.Parse(Utf8(json), "g.json", Ci);
        t = t.Apply(JsonTextOps.CopyElement(t, t.Root, 0)).Text;
        Assert.Equal("[\r\n  { \"name\": \"A\",   \"bus\": 1 },\r\n  { \"name\": \"A\",   \"bus\": 1 },\r\n\r\n  { \"name\": \"B\",   \"bus\": 2 }\r\n]\r\n",
            Encoding.UTF8.GetString(t.ToArray()));
    }

    // =================================================================================================================
    // Byte identity on every bundled JSON file
    // =================================================================================================================

    /// <summary>Every JSON file under data/ (case folders, libraries, events, devices, dist3ph), relative paths.</summary>
    public static TheoryData<string> DataJsonFiles()
    {
        var data = new TheoryData<string>();
        foreach (string f in Directory.EnumerateFiles(DataDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(DataDir, f).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(DataJsonFiles))]
    public void Every_bundled_JSON_file_round_trips_byte_for_byte_with_exact_spans(string relative)
    {
        string path = Path.Combine(DataDir, relative);
        byte[] bytes = File.ReadAllBytes(path);
        JsonSourceText t = JsonSourceText.Parse(bytes, relative, StringComparer.Ordinal);

        // 1. No edit, no change.
        Assert.Equal(bytes, t.Apply(Array.Empty<JsonTextEdit>()).Text.ToArray());

        // 2. Every node's span is exactly the token System.Text.Json sees for the same value (walked in parallel).
        using JsonDocument doc = JsonDocument.Parse(bytes.AsMemory(t.HasBom ? 3 : 0), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 256 });
        var scalars = new List<JsonSourceNode>();
        Walk(t, t.Root, doc.RootElement, "", scalars);

        // 3. Replacing every scalar with its own text, in one patch, gives the same bytes back.
        var identity = scalars.Select(n => JsonTextEdit.ReplaceText(n.Start, n.Length, t.RawText(n))).ToList();
        Assert.Equal(bytes, t.Apply(identity).Text.ToArray());

        // 4. Every number, re-written by the editor's number writer, reads back with the same bits (P7 on real data).
        foreach (JsonSourceNode n in scalars.Where(n => n.Kind == JsonSourceKind.Number))
        {
            Assert.True(n.NumberValue.HasValue, $"{relative}: {n.NumberText} has no double value");
            double d = n.NumberValue!.Value;
            Assert.Equal(BitConverter.DoubleToInt64Bits(d), BitConverter.DoubleToInt64Bits(JsonSerializer.Deserialize<double>(JsonLiteral.FormatNumber(d))));
        }
    }

    private static void Walk(JsonSourceText t, JsonSourceNode n, JsonElement e, string pointer, List<JsonSourceNode> scalars)
    {
        Assert.True(e.GetRawText() == t.RawText(n), $"{t.DocumentName} at '{pointer}': span text differs from the token");
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.Equal(JsonSourceKind.Object, n.Kind);
                var props = e.EnumerateObject().ToList();
                Assert.Equal(props.Count, n.Members.Count);
                for (int i = 0; i < props.Count; i++)
                {
                    Assert.Equal(props[i].Name, n.Members[i].Name);
                    Walk(t, n.Members[i].Value, props[i].Value, pointer + "/" + props[i].Name, scalars);
                }
                break;
            case JsonValueKind.Array:
                Assert.Equal(JsonSourceKind.Array, n.Kind);
                Assert.Equal(e.GetArrayLength(), n.Elements.Count);
                for (int i = 0; i < n.Elements.Count; i++) Walk(t, n.Elements[i], e[i], pointer + "/" + i, scalars);
                break;
            case JsonValueKind.String:
                Assert.Equal(e.GetString(), n.StringValue);
                scalars.Add(n);
                break;
            default:
                scalars.Add(n);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(BundledCases), MemberType = typeof(EditorTestSupport))]
    public void Every_bundled_case_file_parses_under_the_v1_case_insensitive_comparer(string relative)
    {
        // No bundled case file holds two members that the case-insensitive loader would treat as one.
        foreach (string f in Directory.EnumerateFiles(CaseDir(relative), "*.json"))
            JsonSourceText.Load(f, Ci);
    }

    // =================================================================================================================
    // Why JsonNode is not used (characterisation)
    // =================================================================================================================

    [Fact]
    public void JsonNode_is_not_lossless_on_the_bundled_files()
    {
        // uk boundaries.json holds a raw '&' in "SW Peninsula & S Wales (export)". JsonNode keeps the value but
        // writes it back as & and drops the hand alignment, so a one-field edit through JsonNode would rewrite
        // the whole file. This is the reason the editor patches bytes (SCHEMA-EDITOR-PLAN.md section 2, fact 3).
        string uk = File.ReadAllText(Path.Combine(DataDir, "uk", "boundaries.json"));
        Assert.Contains(" & ", uk, StringComparison.Ordinal);
        string viaNode = JsonNode.Parse(uk)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\\u0026", viaNode, StringComparison.Ordinal);
        Assert.NotEqual(uk, viaNode);

        // intl generation.json holds raw UTF-8 letters; JsonNode escapes them.
        string fr = File.ReadAllText(Path.Combine(DataDir, "intl", "france", "generation.json"));
        Assert.Contains(fr, c => c > 127);
        Assert.DoesNotContain(JsonNode.Parse(fr)!.ToJsonString(), c => c > 127);

        // The lossless layer leaves both byte-identical.
        foreach (string f in new[] { Path.Combine(DataDir, "uk", "boundaries.json"), Path.Combine(DataDir, "intl", "france", "generation.json") })
        {
            byte[] bytes = File.ReadAllBytes(f);
            Assert.Equal(bytes, JsonSourceText.Parse(bytes, Path.GetFileName(f), Ci).ToArray());
        }
    }
}
