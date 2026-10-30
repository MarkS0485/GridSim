using System.Buffers.Binary;

namespace GridSim.Core.Interpose;

/// <summary>
/// Decoders for the wire time-tags each protocol carries, all landing on a plain UTC <see cref="DateTime"/>
/// (Kind = Utc) so <see cref="Diagnostics.ForwardInferenceGuard"/> reads them correctly. Devices commonly
/// timestamp in local wall-clock; because the interposer cannot know the RTU's offset, decoded fields are
/// treated as UTC and the operator is expected to run the source clock in UTC (as substation practice
/// increasingly does). A caller with a known fixed offset can subtract it before use.
/// </summary>
public static class TimeDecode
{
    /// <summary>
    /// Decode an IEC 60870-5 CP56Time2a 7-octet time tag to UTC. Layout: ms (u16 LE, 0..59999),
    /// minute (b0-5)+IV(b7), hour (b0-4)+SU(b7), day-of-month (b0-4)+day-of-week(b5-7), month (b0-3),
    /// year (b0-6, +2000). Returns null if the invalid (IV) bit is set or the field is out of range.
    /// <para>Returns null too when the summer-time (SU) bit is set: the station is then stamping LOCAL summer time,
    /// one hour ahead of UTC in GB, and GridSim reads every tag as UTC. Reading it anyway put the window an hour late
    /// and misaligned it against PMU and SCADA data from other sources, silently; it is now an invalid time, counted
    /// like an IV-flagged one, so the station must be set to UTC.</para>
    /// </summary>
    public static DateTime? Cp56Time2a(ReadOnlySpan<byte> t)
    {
        if (t.Length < 7) return null;
        int ms = BinaryPrimitives.ReadUInt16LittleEndian(t);
        int minute = t[2] & 0x3F;
        bool iv = (t[2] & 0x80) != 0;
        int hour = t[3] & 0x1F;
        bool su = (t[3] & 0x80) != 0;
        int day = t[4] & 0x1F;
        int month = t[5] & 0x0F;
        int year = 2000 + (t[6] & 0x7F);
        if (iv || su) return null;
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || ms > 59999) return null;

        try
        {
            return new DateTime(year, month, day, hour, minute, ms / 1000, ms % 1000, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // e.g. day 31 in a 30-day month
        }
    }

    // The largest millisecond count DateTimeOffset.FromUnixTimeMilliseconds accepts (9999-12-31T23:59:59.999Z).
    private static readonly long MaxUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>Decode a DNP3 absolute time (6-octet unsigned little-endian milliseconds since the
    /// 1970-01-01 UTC epoch, as used by Group 50/51/58 timestamps). SCAFFOLDING (R089): no product caller.</summary>
    public static DateTime? Dnp3AbsoluteTime(ReadOnlySpan<byte> t)
    {
        if (t.Length < 6) return null;
        long msSinceEpoch = t[0] | ((long)t[1] << 8) | ((long)t[2] << 16)
            | ((long)t[3] << 24) | ((long)t[4] << 32) | ((long)t[5] << 40);
        // GR-792: a count past 9999-12-31T23:59:59.999Z is out of range: null, as the contract says, not a throw.
        if (msSinceEpoch > MaxUnixMilliseconds) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(msSinceEpoch).UtcDateTime;
    }

    /// <summary>Decode an IEC 61850 <c>t</c> UtcTime (8 octets: u32 BE seconds since the 1970 epoch,
    /// then u24 BE fraction-of-second numerator over 2^24, then a 1-octet time-quality). The fractional
    /// part is truncated to whole milliseconds. SCAFFOLDING (R089): no product caller.</summary>
    public static DateTime? IsoUtcTime(ReadOnlySpan<byte> t)
    {
        if (t.Length < 8) return null;
        uint seconds = BinaryPrimitives.ReadUInt32BigEndian(t);
        uint frac = ((uint)t[4] << 16) | ((uint)t[5] << 8) | t[6];
        int ms = (int)(frac / 16777.216); // frac/2^24 * 1000
        return DateTimeOffset.FromUnixTimeMilliseconds((long)seconds * 1000 + ms).UtcDateTime;
    }
}
