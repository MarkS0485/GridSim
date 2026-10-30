using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N049 (October 2026, best-judgement ruling): <see cref="Iec60909.PartialThreePhase"/>'s fleet breaking current uses
/// the same IEC 60909-0:2016 meshed-network (multiple-fed) formula as <see cref="Iec60909.ThreePhase"/> (which gained
/// it in 1.95.094, R095). The two now agree bit for bit; before, the partial total was the single-fed sum of the
/// per-machine mu_i * Ik''_i.
/// </summary>
public class Iec60909PartialAgreementTests
{
    // WSCC-9, objective max, minimum time delay 0.10 s: the partial total before N049 (the single-fed sum) and after
    // (ThreePhase's Ib, unchanged by N049).
    //
    // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the fault network is now the sparse engine at every size
    // (it was the dense complex LU up to 1,500 buses), so case9's Zbus entries moved in their last bits and with them
    // these values. Before -> after, exactly as measured (normal mode, 1.95.207 base vs the DOC-16 branch):
    //   bus 2 single-fed sum 2.042454708094612 -> 2.0424547080946116 (Ib 2.0413041587855694 unchanged);
    //   bus 3 single-fed sum 1.6389680410617715 -> 1.6389680410617713 (Ib 1.6375154270367709 unchanged);
    //   bus 4 Ib 2.0600168548926723 -> 2.0600168548926727; bus 5 Ib 1.4683989584292976 -> 1.4683989584292985;
    //   bus 6 Ib 1.7482142689896478 -> 1.748214268989648;  bus 7 single-fed sum 1.6083616402122436 -> 1.6083616402122431
    //   (Ib 1.630308759669193 unchanged); bus 9 Ib 1.5444853448525935 -> 1.5444853448525933; buses 1 and 8 unchanged.
    // The single-fed sums of buses 4, 5, 6 and 9 were not reached by the run that measured these (the Ib assertion came
    // first and failed); they are left as they were and are to be confirmed at the next batch run (DOC-16 notes).
    [Theory]
    // Moved 2026-10 (A5, final batch; DOC-16 N233, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): with one sparse
    // fault-network engine the single-fed sum (before) moves by one ulp at buses 5, 6 and 9 (bus 4 does not move), as
    // DOC-16 left to confirm. Before -> after: bus 5 1.4690796589338537 -> 1.4690796589338535; bus 6 1.708607252464341
    // -> 1.7086072524643408; bus 9 1.5455388627348547 -> 1.5455388627348545. The after values are unchanged.
    [InlineData(1, 2.35285127770572, 2.3509284626333975)]
    [InlineData(2, 2.0424547080946116, 2.0413041587855694)]
    [InlineData(3, 1.6389680410617713, 1.6375154270367709)]
    [InlineData(4, 1.9953536674616223, 2.0600168548926727)]
    [InlineData(5, 1.4690796589338535, 1.4683989584292985)]
    [InlineData(6, 1.7086072524643408, 1.748214268989648)]
    [InlineData(7, 1.6083616402122431, 1.630308759669193)]
    [InlineData(8, 1.9128790957363628, 1.9706884701977745)]
    [InlineData(9, 1.5455388627348545, 1.5444853448525933)]
    public void Case9_partial_total_breaking_current_equals_ThreePhase(int bus, double before, double after)
    {
        GridModel m = IeeeCases.Case9();
        Iec60909.FaultContribution fc = Iec60909.PartialThreePhase(m, bus, minTimeDelaySeconds: 0.10);
        Iec60909.Duty duty = Iec60909.ThreePhase(m, bus, breakTimeSeconds: 0.10);
        Assert.Equal(after, fc.TotalBreakingIbKa);
        Assert.Equal(BitConverter.DoubleToInt64Bits(duty.BreakingIbKa), BitConverter.DoubleToInt64Bits(fc.TotalBreakingIbKa));
        Assert.NotEqual(before, fc.TotalBreakingIbKa);
        // The per-machine single-fed values are unchanged: their sum is still the old total.
        double sum = 0.0;
        foreach (Iec60909.MachinePartialCurrent g in fc.Machines) sum += g.BreakingIbKa;
        Assert.Equal(before, sum);
        // Never above Ik'' (the old single-fed sum was, at buses 5 and 9, where every mu is 1 in a lossy network).
        Assert.True(fc.TotalBreakingIbKa <= duty.IkppKa);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.05)]
    [InlineData(0.25)]
    public void The_two_agree_at_every_break_time(double tmin)
    {
        GridModel m = IeeeCases.Case9();
        for (int bus = 1; bus <= 9; bus++)
            Assert.Equal(Iec60909.ThreePhase(m, bus, breakTimeSeconds: tmin).BreakingIbKa,
                Iec60909.PartialThreePhase(m, bus, minTimeDelaySeconds: tmin).TotalBreakingIbKa);
    }
}
