using System.Globalization;
using GridSim.Core.Estimation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N177 (October 2026): the CLI's HTI report labelled every estimated error "pu" (console line and the
/// `estimated_error_pu` CSV column), but HtiIdentified.EstimatedError is in the estimator's internal units: radians for
/// the angle rows, per-unit of the system base for the power rows, per-unit for the magnitudes. The report now names the
/// unit per row and also gives the error in the measurement's own unit. No computed number changes: the
/// `estimated_error` column carries exactly the number the old column carried.
/// </summary>
public sealed class N177HtiErrorUnitLabelTests
{
    // ---- The unit names, one rule in Core -------------------------------------------------------------------------

    /// <summary>Every measurement kind has an internal-unit name and a measurement-unit name; none throws.</summary>
    [Fact]
    public void Every_kind_has_both_unit_names()
    {
        foreach (MeasurementKind kind in Enum.GetValues<MeasurementKind>())
        {
            Assert.False(string.IsNullOrEmpty(HtiBadDataEstimation.InternalUnitName(kind)), kind.ToString());
            Assert.False(string.IsNullOrEmpty(HtiBadDataEstimation.MeasurementUnitName(kind)), kind.ToString());
        }
    }

    /// <summary>The exact names per kind: the angle rows are radians inside and degrees outside (the defect).</summary>
    [Theory]
    [InlineData(MeasurementKind.VmPu, "pu", "pu")]
    [InlineData(MeasurementKind.IMagPu, "pu", "pu")]
    [InlineData(MeasurementKind.VaDeg, "rad", "deg")]
    [InlineData(MeasurementKind.IAngDeg, "rad", "deg")]
    [InlineData(MeasurementKind.PInjMw, "pu_base_mva", "MW")]
    [InlineData(MeasurementKind.PFlowMw, "pu_base_mva", "MW")]
    [InlineData(MeasurementKind.QInjMvar, "pu_base_mva", "Mvar")]
    [InlineData(MeasurementKind.QFlowMvar, "pu_base_mva", "Mvar")]
    public void Unit_names_per_kind(MeasurementKind kind, string inside, string outside)
    {
        Assert.Equal(inside, HtiBadDataEstimation.InternalUnitName(kind));
        Assert.Equal(outside, HtiBadDataEstimation.MeasurementUnitName(kind));
    }

    /// <summary>An undefined kind fails loud instead of borrowing a label.</summary>
    [Fact]
    public void Unknown_kind_is_refused()
    {
        var bogus = (MeasurementKind)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => HtiBadDataEstimation.InternalUnitName(bogus));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtiBadDataEstimation.MeasurementUnitName(bogus));
    }

    /// <summary>The conversion (now public) is unchanged: exact values for each family on a 100 MVA base.</summary>
    [Fact]
    public void Conversion_is_unchanged()
    {
        Assert.Equal(-0.0764948, HtiBadDataEstimation.ToMeasurementUnits(MeasurementKind.VmPu, -0.0764948, 100.0));
        Assert.Equal(0.25 * 100.0, HtiBadDataEstimation.ToMeasurementUnits(MeasurementKind.PFlowMw, 0.25, 100.0));
        Assert.Equal(0.01 * 180.0 / Math.PI, HtiBadDataEstimation.ToMeasurementUnits(MeasurementKind.VaDeg, 0.01, 100.0));
    }

    // ---- The CLI source ---------------------------------------------------------------------------------------------

    /// <summary>The CLI no longer writes the misleading column name or the blanket " pu" suffix, and takes its unit
    /// names from Core.</summary>
    [Fact]
    public void Cli_source_labels_from_core()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs"));
        // The old column name may appear in the explanatory comment; it must not appear in a CSV header any more.
        Assert.DoesNotContain(",estimated_error_pu,", src, StringComparison.Ordinal);
        Assert.DoesNotContain("EstimatedError:G6} pu", src, StringComparison.Ordinal);
        Assert.Contains("HtiBadDataEstimation.InternalUnitName(kind)", src, StringComparison.Ordinal);
        Assert.Contains("HtiBadDataEstimation.MeasurementUnitName(kind)", src, StringComparison.Ordinal);
    }

    // ---- End to end on the built CLI --------------------------------------------------------------------------------

    /// <summary>
    /// The documented case14 planted set (docs/_captures/estimation/case14-planted.json): the three HTI removals print
    /// their unit per row, and hti-removals.csv carries the unit columns. The error numbers are the ones the capture
    /// shows (-0.0764948, 0.23808, 0.272927), i.e. unchanged.
    /// </summary>
    [Fact]
    public void Planted_case14_hti_labels_each_row()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n177-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string set = Path.Combine(CliProcess.Root(), "docs", "_captures", "estimation", "case14-planted.json");
            string outDir = Path.Combine(dir, "out");
            CliProcess.Result r = CliProcess.RunBuilt("estimate", "case14", "--measurements", set, "--hti", "--out", outDir);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("VmPu @ bus 9  t = 17.9  estimated error = -0.0764948 pu" + Environment.NewLine, r.StdOut, StringComparison.Ordinal);
            Assert.Contains("PFlowMw @ branch 2->3 (from)  t = 13.6  estimated error = 0.23808 pu_base_mva (23.808 MW)", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("PInjMw @ bus 4  t = 7.2  estimated error = 0.272927 pu_base_mva (27.2927 MW)", r.StdOut, StringComparison.Ordinal);

            string[] lines = File.ReadAllLines(Path.Combine(outDir, "hti-removals.csv"));
            string header = lines.Single(l => l.StartsWith("pass,", StringComparison.Ordinal));
            Assert.Equal("pass,kind,location,value,sigma,estimated_error,estimated_error_unit,estimated_error_measured,measured_unit,test_statistic", header);
            string[][] rows = lines.Where(l => l.Length > 0 && char.IsDigit(l[0])).Select(l => l.Split(',')).ToArray();
            Assert.Equal(3, rows.Length);
            foreach (string[] row in rows)
            {
                Assert.Equal(10, row.Length);
                var kind = Enum.Parse<MeasurementKind>(row[1]);
                Assert.Equal(HtiBadDataEstimation.InternalUnitName(kind), row[6]);
                Assert.Equal(HtiBadDataEstimation.MeasurementUnitName(kind), row[8]);
                double inside = double.Parse(row[5], CultureInfo.InvariantCulture);
                double outside = double.Parse(row[7], CultureInfo.InvariantCulture);
                // G17 round-trips exactly, so the measured column is exactly Core's conversion of the internal one.
                Assert.Equal(HtiBadDataEstimation.ToMeasurementUnits(kind, inside, 100.0), outside);
            }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
