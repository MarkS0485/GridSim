using System.Globalization;
using System.Numerics;
using System.Text;
using GridSim.Core.Dynamics.Models;

namespace GridSim.Core.IO;

/// <summary>
/// The fault-point trace of an RMS event study with sequence faults (<see cref="RmsTransientStability.StudyEventsWithFaults"/>),
/// written beside the RMS event export (<see cref="RmsEventsExport"/>) as <see cref="FileName"/>: one row per (fault event,
/// integration step while it is in place) with the sequence voltages and currents (real and imaginary parts, pu on the
/// system base, phase-a reference), the phase voltage and current magnitudes, the stage's Z2 and Z0, and whether the point
/// reaches an earthed neutral. Each double is in its shortest round-trip form ("R", invariant culture). The file starts
/// with the caller's header lines, each prefixed "# ", and a "# provenance:" line (a model output). Pure formatting.
/// </summary>
public static class RmsFaultPointsExport
{
    /// <summary>The file name written into the export folder.</summary>
    public const string FileName = "rms-fault-points.csv";

    /// <summary>The column header.</summary>
    public const string Header = "event,t_s,energised,v1_re,v1_im,v2_re,v2_im,v0_re,v0_im,i1_re,i1_im,i2_re,i2_im,i0_re,i0_im,"
        + "va_pu,vb_pu,vc_pu,ia_pu,ib_pu,ic_pu,z2_re,z2_im,z0_re,z0_im,grounded";

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The CSV text for every fault trace of <paramref name="study"/>, in event order.</summary>
    public static string CsvText(RmsFaultStudyResult study, IReadOnlyList<string> headerLines)
    {
        ArgumentNullException.ThrowIfNull(study);
        ArgumentNullException.ThrowIfNull(headerLines);
        var sb = new StringBuilder();
        foreach (string h in headerLines) sb.Append("# ").Append(h).Append('\n');
        sb.Append("# provenance: ").Append(RmsEventsExport.Provenance.ToString())
          .Append(" (a model trajectory of the RMS event study, positive-sequence representation of the faults, not a record)\n");
        sb.Append(Header).Append('\n');
        void C(Complex z) => sb.Append(',').Append(R(z.Real)).Append(',').Append(R(z.Imaginary));
        foreach (RmsFaultTrace f in study.Faults)
            foreach (RmsFaultSample s in f.Samples)
            {
                sb.Append(f.EventIndex.ToString(CultureInfo.InvariantCulture)).Append(',').Append(R(s.TimeSeconds))
                  .Append(',').Append(s.Energised ? "true" : "false");
                C(s.V1); C(s.V2); C(s.V0); C(s.I1); C(s.I2); C(s.I0);
                (double va, double vb, double vc) = s.PhaseVoltagesPu;
                (double ia, double ib, double ic) = s.PhaseCurrentsPu;
                sb.Append(',').Append(R(va)).Append(',').Append(R(vb)).Append(',').Append(R(vc))
                  .Append(',').Append(R(ia)).Append(',').Append(R(ib)).Append(',').Append(R(ic));
                C(s.Z2Pu); C(s.Z0Pu);
                sb.Append(',').Append(s.Grounded ? "true" : "false").Append('\n');
            }
        return sb.ToString();
    }
}
