using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Ucte;
using GridSim.Ui.Geography;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 RC-4-Fixes: the whole-file size cap on case and geography inputs. The parsers read a file completely before
/// parsing it, so the length is checked first; a longer file is refused naming the file and the limit, and a file of
/// exactly the limit is read. The tests scope a tiny limit to the current async context (no 256 MiB files are made and
/// no other test sees the change).
/// </summary>
public sealed class InputFileLimitsTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-inputlimit-" + Guid.NewGuid().ToString("N"));

    public InputFileLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string File12(string name, int length = 12)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, new string(' ', length));
        return p;
    }

    [Fact]
    public void The_default_limit_is_256_MiB_and_a_scope_restores_it()
    {
        Assert.Equal(256L * 1024 * 1024, InputFileLimits.MaxBytes);
        using (InputFileLimits.Scope(7)) Assert.Equal(7, InputFileLimits.MaxBytes);
        Assert.Equal(InputFileLimits.DefaultMaxBytes, InputFileLimits.MaxBytes);
    }

    [Fact]
    public void A_file_over_the_limit_is_refused_naming_it_and_a_file_of_exactly_the_limit_is_read()
    {
        string path = File12("a.txt");
        using (InputFileLimits.Scope(12))
            Assert.Equal(12, InputFileLimits.ReadAllText(path).Length);
        using (InputFileLimits.Scope(11))
        {
            var ex = Assert.Throws<InputFileTooLargeException>(() => InputFileLimits.ReadAllText(path));
            Assert.Contains("a.txt", ex.Message, StringComparison.Ordinal);
            Assert.Contains("11-byte limit", ex.Message, StringComparison.Ordinal);
            Assert.IsAssignableFrom<IOException>(ex);
            Assert.Throws<InputFileTooLargeException>(() => InputFileLimits.ReadAllBytes(path));
        }
    }

    [Fact]
    public void A_missing_file_is_left_to_the_reader()
    {
        using (InputFileLimits.Scope(1))
            Assert.Throws<FileNotFoundException>(() => InputFileLimits.ReadAllText(Path.Combine(_dir, "none.txt")));
    }

    [Fact]
    public void Every_wired_case_parser_refuses_an_oversize_file_before_parsing_it()
    {
        string p = File12("case.dat");
        using (InputFileLimits.Scope(11))
        {
            Assert.Throws<InputFileTooLargeException>(() => MatpowerParser.ParseFile(p));
            Assert.Throws<InputFileTooLargeException>(() => PsseRawParser.ParseFile(p));
            Assert.Throws<InputFileTooLargeException>(() => PsseSeqReader.ReadFile(p, null!));
            Assert.Throws<InputFileTooLargeException>(() => UcteParser.ParseFile(p));
            Assert.Throws<InputFileTooLargeException>(() => SolveImport.ReadFile(p));
            Assert.Throws<InputFileTooLargeException>(() => MeasurementSet.Load(p));
            Assert.Throws<InputFileTooLargeException>(() => JsonSourceText.Load(p, StringComparer.Ordinal));
            Assert.Throws<InputFileTooLargeException>(() => GridSim.Core.Distribution.MultiPhaseJsonLoader.Load(p));
        }
    }

    [Fact]
    public void A_geography_file_over_the_limit_is_refused_naming_the_file_and_one_at_the_limit_is_read()
    {
        string caseDir = Path.Combine(_dir, "case");
        Directory.CreateDirectory(caseDir);
        File.WriteAllText(Path.Combine(caseDir, CaseGeography.FileName), "{\"format\":\"nope\"}");
        long length = new FileInfo(Path.Combine(caseDir, CaseGeography.FileName)).Length;

        using (InputFileLimits.Scope(length - 1))
        {
            Assert.False(CaseGeography.TryLoad(caseDir, out CaseGeography? g, out string? problem));
            Assert.Null(g);
            Assert.Contains("geography.json", problem, StringComparison.Ordinal);
            Assert.Contains("limit", problem, StringComparison.Ordinal);
        }
        using (InputFileLimits.Scope(length))
        {
            Assert.False(CaseGeography.TryLoad(caseDir, out _, out string? problem));
            Assert.DoesNotContain("limit", problem, StringComparison.Ordinal);   // read, then refused as the wrong format
            Assert.Contains("format", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_gb_geojson_layers_report_an_oversize_file_in_the_problem_list()
    {
        string geo = Path.Combine(_dir, "gda-out", "geo");
        Directory.CreateDirectory(geo);
        File.WriteAllText(Path.Combine(geo, "dno_areas.geojson"), new string(' ', 40));
        var problems = new List<string>();
        using (InputFileLimits.Scope(39))
            Assert.Null(GeographyData.Load(_dir, problems));
        Assert.Contains(problems, s => s.StartsWith("dno_areas.geojson:", StringComparison.Ordinal) && s.Contains("limit", StringComparison.Ordinal));
    }
}
