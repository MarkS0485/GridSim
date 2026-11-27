using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis.Gic;

/// <summary>
/// RC5 WP28: the northward and eastward distance between two substations, the path vector of a line for the uniform-field
/// source V = E_N L_N + E_E L_E (NERC, "Application Guide: Computing Geomagnetically-Induced Current in the Bulk-Power
/// System", December 2013, Appendix I). The vector points from the <c>fromLat</c> end to the other end, so a
/// positive V drives current from the from end to the to end through the line.
/// </summary>
public static class GicDistances
{
    /// <summary>Degrees to radians.</summary>
    private const double Rad = Math.PI / 180.0;

    /// <summary>The spherical model's km per degree (the guide's worked example).</summary>
    public const double SphericalKmPerDegree = 111.2;

    /// <summary>(L_N, L_E) in km from (fromLat, fromLon) to (toLat, toLon), degrees.</summary>
    public static (double NorthKm, double EastKm) Between(double fromLat, double fromLon, double toLat, double toLon, GicDistanceModel model)
    {
        double phi = (fromLat + toLat) / 2.0 * Rad;
        double dLat = toLat - fromLat, dLon = toLon - fromLon;
        double cosPhi = DetMath.Cos(phi);
        if (model == GicDistanceModel.Spherical)
            return (SphericalKmPerDegree * dLat, SphericalKmPerDegree * cosPhi * dLon);
        double cos2Phi = DetMath.Cos(2.0 * phi);
        // Guide eq. A.3 and eq. A.7 (WGS84, Table A.1).
        return ((111.133 - 0.56 * cos2Phi) * dLat, (111.5065 - 0.1872 * cos2Phi) * cosPhi * dLon);
    }
}
