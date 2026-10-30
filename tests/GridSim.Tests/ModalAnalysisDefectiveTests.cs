using System;
using System.Linq;
using GridSim.Core.Dynamics.SmallSignal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N011 (October 2026, R033 follow-up): <see cref="ModalAnalysis.Analyze"/> must never crash on, or silently drop
/// participation for, a DEFECTIVE state matrix. Before the fix the undamped swing path had no catch (the
/// <c>DefectiveEigenproblemException</c> escaped) and the damped path's catch returned all-zero participation with
/// no word. Both now return eigenvalues with <see cref="ModalResult.ParticipationUnavailable"/> set and a reason
/// naming the path; a non-defective model reports the flag false and no reason.
/// </summary>
public class ModalAnalysisDefectiveTests
{
    private static StateLabel[] Labels(params StateKind[] kinds) =>
        kinds.Select((k, i) => new StateLabel(i, k, i, k + " " + i)).ToArray();

    [Fact]
    public void Damped_path_with_a_jordan_block_reports_participation_unavailable()
    {
        // Inverter-augmented (so not the swing-only path), A = [[0, 1], [0, 0]]: a 2x2 Jordan block at s = 0,
        // defective, so biorthonormal eigenvectors do not exist.
        var model = new SmallSignalModel(
            new double[,] { { 0, 1 }, { 0, 0 } },
            Labels(StateKind.SwingAngle, StateKind.SwingSpeed),
            50.0, Array.Empty<int>(), new double[0, 0], InverterStatesIncluded: true);

        ModalResult res = ModalAnalysis.Analyze(model);

        Assert.True(res.ParticipationUnavailable);
        Assert.NotNull(res.ParticipationUnavailableReason);
        Assert.Contains("damped / inverter-augmented path", res.ParticipationUnavailableReason);
        Assert.Contains("defective", res.ParticipationUnavailableReason);
        Assert.Equal(2, res.Modes.Count);                       // the eigenvalues are still reported
        foreach (Mode m in res.Modes) Assert.All(m.ParticipationByState, p => Assert.Equal(0.0, p));
    }

    [Fact]
    public void Undamped_swing_path_with_a_defective_reduced_matrix_reports_instead_of_throwing()
    {
        // Two undamped machines with NO synchronising coupling: angle i -> speed i couplings only (A[i, g+i] = ws),
        // every speed row zero. The deflated 2x2 electromechanical matrix is [[0, ws], [0, 0]], a Jordan block, so
        // GeneralEigen.Decompose throws; before N011 that exception escaped Analyze.
        const double ws = 2.0 * Math.PI * 50.0;
        var a = new double[4, 4];
        a[0, 2] = ws;
        a[1, 3] = ws;
        var model = new SmallSignalModel(
            a, Labels(StateKind.SwingAngle, StateKind.SwingAngle, StateKind.SwingSpeed, StateKind.SwingSpeed),
            50.0, new[] { 1, 2 }, new double[0, 0], InverterStatesIncluded: false);

        ModalResult res = ModalAnalysis.Analyze(model);

        Assert.True(res.ParticipationUnavailable);
        Assert.NotNull(res.ParticipationUnavailableReason);
        Assert.Contains("undamped swing path", res.ParticipationUnavailableReason);
        Assert.Contains("defective", res.ParticipationUnavailableReason);
        Assert.Equal(4, res.Modes.Count);                       // 2 reduced eigenvalues + 2 appended COI zeros
        foreach (Mode m in res.Modes) Assert.All(m.ParticipationByState, p => Assert.Equal(0.0, p));
    }

    [Fact]
    public void Non_defective_model_reports_participation_available()
    {
        // A damped oscillator: s^2 + 0.1 s + 1 = 0, two distinct complex eigenvalues, non-defective.
        var model = new SmallSignalModel(
            new double[,] { { 0, 1 }, { -1, -0.1 } },
            Labels(StateKind.SwingAngle, StateKind.SwingSpeed),
            50.0, Array.Empty<int>(), new double[0, 0], InverterStatesIncluded: true);

        ModalResult res = ModalAnalysis.Analyze(model);

        Assert.False(res.ParticipationUnavailable);
        Assert.Null(res.ParticipationUnavailableReason);
        Assert.Equal(2, res.Modes.Count);
        foreach (Mode m in res.Modes) Assert.Equal(1.0, m.ParticipationByState.Sum(), 12);
    }
}
