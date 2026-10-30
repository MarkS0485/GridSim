namespace GridSim.Core.Synchrophasor;

/// <summary>Whether one PMU sample (one block of one DATA frame) may be used, and if not, why.</summary>
public enum C37118SampleVerdict
{
    /// <summary>Good data, synchronised time: usable.</summary>
    Usable = 0,

    /// <summary>STAT bits 15-14 (data error) are not 00: the PMU itself says the values must not be used (PMU error,
    /// test mode, or absent data with tags inserted).</summary>
    InvalidData = 1,

    /// <summary>The sample's time is not synchronised well enough to trust its phase angle: STAT bit 13 (PMU sync) set,
    /// a PMU time quality (STAT bits 8-6) of 100 us or worse, or an unlocked time (STAT bits 5-4) of 10 s or more.</summary>
    Unsynchronised = 2,

    /// <summary>The whole frame's time stamp is unreliable: the FRACSEC message time quality code says the clock has
    /// failed (0xF), the time error is 100 us or more (0x6 to 0xB), or the code is undefined (0xC to 0xE). Every block of
    /// that frame is dropped.</summary>
    UnreliableFrameTime = 3,
}

/// <summary>
/// D-DOC-108 (October 2026): the documented rule that turns the C37.118 STAT word and the FRACSEC time quality into a
/// verdict on each PMU sample. Before, both were decoded and stored but never read, so a sample the PMU flagged as
/// invalid (test mode, PMU error) or unsynchronised (GPS lost) went into the aligned instants exactly like a good one.
/// <c>pmu-ingest</c> now drops every sample that is not <see cref="C37118SampleVerdict.Usable"/>, counts it per PMU and
/// per reason, and exits 3 when any was dropped (as it does for a frame with a bad CRC): never passed on silently.
///
/// <para><b>The rule</b> (IEEE C37.118.2-2011; the standard is not in docs/reference, so the table numbers are as the
/// decoder records them [VERIFY]):</para>
/// <list type="number">
/// <item><b>Frame time quality</b> (FRACSEC bits 31-24, the message time quality; Table 3): the low four bits are a
/// code. 0x0 = clock locked to a UTC-traceable source; 0x1 to 0xB = time within 10^(n-10) s of UTC (0x1 = 1 ns ... 0x5 =
/// 10 us, 0x6 = 100 us ... 0xB = 10 s); 0xF = clock failure, time not reliable; 0xC to 0xE are not defined. Usable: 0x0
/// to 0x5. Anything else drops the whole frame (<see cref="C37118SampleVerdict.UnreliableFrameTime"/>). The leap-second
/// bits (4-6) do not decide usability.</item>
/// <item><b>STAT data error</b> (bits 15-14; Table 7): 00 good; 01 PMU error, no information about the data; 10 PMU in
/// test mode or absent data tags inserted (do not use); 11 PMU error (do not use). Anything but 00 drops the sample
/// (<see cref="C37118SampleVerdict.InvalidData"/>).</item>
/// <item><b>STAT synchronisation</b>: bit 13 set (PMU not in sync with a UTC source), a PMU time quality (bits 8-6) of
/// 100 (100 us) or worse (101 1 ms, 110 10 ms, 111 more than 10 ms or unknown; 000 means "not used", as in 2005-format
/// PMUs, and passes), or an unlocked time (bits 5-4) other than 00 (unlocked 10 s or longer) drops the sample
/// (<see cref="C37118SampleVerdict.Unsynchronised"/>). The other STAT bits (sorting, trigger, configuration change, data
/// modified, trigger reason) describe the stream, not the trust of the value, and do not decide usability.</item>
/// </list>
///
/// <para><b>Why 10 us is the line.</b> A time error dt rotates a 50 Hz phasor by 360 x 50 x dt degrees: 10 us is 0.18
/// degrees, 100 us is 1.8 degrees. The 1 % total vector error budget of a synchrophasor is about 0.57 degrees of angle
/// on its own (arctan 0.01), so a sample whose time may be 100 us out can carry an angle error three times the whole
/// budget, while 10 us stays well inside it. A state estimator weights PMU angles heavily (small sigma), so an angle
/// that may be degrees wrong must not reach it.</para>
/// </summary>
public static class C37118SampleQuality
{
    /// <summary>The largest FRACSEC message time quality code that is usable (0x5: time within 10 us of UTC).</summary>
    public const int MaxUsableFrameTimeQualityCode = 0x5;

    /// <summary>The largest STAT PMU time quality code (bits 8-6) that is usable (011: within 10 us).</summary>
    public const int MaxUsableStatTimeQuality = 0b011;

    /// <summary>The verdict on a whole frame from its FRACSEC message time quality byte (the top byte of FRACSEC,
    /// <see cref="C37118CommonHeader.TimeQuality"/>): <see cref="C37118SampleVerdict.Usable"/> or
    /// <see cref="C37118SampleVerdict.UnreliableFrameTime"/>.</summary>
    public static C37118SampleVerdict OfFrameTimeQuality(byte timeQuality)
    {
        int code = timeQuality & 0x0F;
        return code <= MaxUsableFrameTimeQualityCode ? C37118SampleVerdict.Usable : C37118SampleVerdict.UnreliableFrameTime;
    }

    /// <summary>The verdict on one PMU block from its 16-bit STAT word: <see cref="C37118SampleVerdict.Usable"/>,
    /// <see cref="C37118SampleVerdict.InvalidData"/> or <see cref="C37118SampleVerdict.Unsynchronised"/>. Data error is
    /// judged first (a value the PMU disowns is invalid whatever its time).</summary>
    public static C37118SampleVerdict OfStat(ushort stat)
    {
        if (((stat >> 14) & 0b11) != 0) return C37118SampleVerdict.InvalidData;
        bool syncLost = ((stat >> 13) & 1) != 0;
        int timeQuality = (stat >> 6) & 0b111;
        int unlocked = (stat >> 4) & 0b11;
        if (syncLost || timeQuality > MaxUsableStatTimeQuality || unlocked != 0) return C37118SampleVerdict.Unsynchronised;
        return C37118SampleVerdict.Usable;
    }

    /// <summary>The verdict on one block of a frame: the frame's time quality first (it applies to every block), then
    /// the block's STAT word.</summary>
    public static C37118SampleVerdict Of(byte frameTimeQuality, ushort stat)
    {
        C37118SampleVerdict frame = OfFrameTimeQuality(frameTimeQuality);
        return frame != C37118SampleVerdict.Usable ? frame : OfStat(stat);
    }
}
