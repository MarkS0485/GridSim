using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Tests;

/// <summary>
/// N186 (October 2026): the fixtures and the digest that pin every eigenvalue, eigenvector and participation factor
/// of <see cref="GeneralEigen.Decompose"/> and <see cref="ModalAnalysis.Analyze"/> across the speed-up of the
/// participation path (vectorised complex LU trailing update, inverse iterations run in parallel). The digests in
/// <see cref="N186ParticipationBitIdentityTests"/> were produced by THIS code linked against the GridSim.Core of
/// 1.95.133 (before the change), in normal mode, with DOTNET_EnableAVX2=0 and with DOTNET_EnableHWIntrinsic=0 (all
/// three gave the same digests). A digest is SHA-256 over the raw 64-bit patterns of every number in order, so one
/// flipped bit anywhere changes it.
///
/// The MATPOWER cases carry no inertia (H = 0, so modal refuses them; N179). The fixture gives every in-service
/// generator a deterministic H = 2 + (i mod 7) * 0.75 MWs/MVA on its own base so the classical small-signal
/// matrix can be assembled; that is a test input, not a modelling claim.
/// </summary>
internal static class N186ParticipationFixtures
{
    /// <summary>WSCC-9 X'd (Anderson-Fouad), as the modal tests and the CLI use.</summary>
    internal static readonly double[] Wscc9Xdp = { 0.0608, 0.1198, 0.1813 };

    /// <summary>The named fixtures, in a fixed order.</summary>
    internal static readonly string[] Names =
    {
        "case9-undamped", "case9-damped",
        "case14-undamped", "case14-damped",
        "case118-undamped", "case118-damped",
        "case300-undamped", "case300-damped",
        "random-6", "random-23", "random-64", "random-130",
        "repeated-blocks-12",
    };

    /// <summary>Digest of one fixture (hex SHA-256).</summary>
    internal static string Digest(string name)
    {
        var bytes = new List<byte>();
        void D(double x) => bytes.AddRange(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(x)));
        void C(Complex z) { D(z.Real); D(z.Imaginary); }

        if (name.StartsWith("random-", StringComparison.Ordinal) || name.StartsWith("repeated-", StringComparison.Ordinal))
        {
            double[,] a = name.StartsWith("random-", StringComparison.Ordinal)
                ? RandomMatrix(int.Parse(name["random-".Length..], System.Globalization.CultureInfo.InvariantCulture))
                : RepeatedBlocks();
            foreach (GeneralEigen.Eigenpair p in GeneralEigen.Decompose(a))
            {
                C(p.Value);
                foreach (Complex z in p.Right) C(z);
                foreach (Complex z in p.Left) C(z);
            }
        }
        else
        {
            ModalResult r = ModalAnalysis.Analyze(CaseModel(name));
            D(r.RhpEigenvalueCount);
            D(r.MinDampingRatio);
            D(r.ParticipationUnavailable ? 1 : 0);
            foreach (Mode m in r.Modes)
            {
                C(m.Eigenvalue);
                D(m.DampingRatio);
                D(m.FrequencyHz);
                D(m.IsOscillatory ? 1 : 0);
                foreach (double x in m.ParticipationByState) D(x);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    /// <summary>The classical small-signal model of a named case fixture.</summary>
    internal static SmallSignalModel CaseModel(string name)
    {
        string caseName = name[..name.IndexOf('-')];
        bool damped = name.EndsWith("-damped", StringComparison.Ordinal);
        GridModel model = caseName switch
        {
            // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
            // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
            // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
            // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
            // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
            "case9" => IeeeCases.Case9().CopyWith(frequencyHz: 50.0),
            "case14" => WithInertia(IeeeCases.Case14()),
            "case118" => WithInertia(IeeeCases.Case118()),
            "case300" => WithInertia(IeeeCases.Case300()),
            _ => throw new ArgumentException(name),
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        if (!pf.Converged) throw new InvalidOperationException($"{name}: power flow did not converge.");
        double[]? xdp = caseName == "case9" ? Wscc9Xdp : null;
        double[]? d = null;
        if (damped)
        {
            d = new double[model.Generators.Count];
            for (int i = 0; i < d.Length; i++) d[i] = 2.0;
        }
        return SmallSignalAssembler.FromSolvedCase(model, pf, xdp, d);
    }

    private static GridModel WithInertia(GridModel m) =>
        m.CopyWith(generators: m.Generators.Select((g, i) => g with { H = 2.0 + (i % 7) * 0.75 }).ToList());

    /// <summary>A seeded dense matrix with entries in [-1, 1) (the generator FactorOnceInverseIterationTests uses).</summary>
    internal static double[,] RandomMatrix(int n)
    {
        var a = new double[n, n];
        ulong s = (ulong)(n * 2654435761u + 7);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                s = s * 6364136223846793005UL + 1442695040888963407UL;
                a[i, j] = ((s >> 11) * (1.0 / (1UL << 53))) * 2.0 - 1.0;
            }
        return a;
    }

    /// <summary>Four copies of one 3x3 block on the diagonal (repeated, non-defective eigenvalues): the R033 cluster
    /// deflation path.</summary>
    internal static double[,] RepeatedBlocks()
    {
        double[,] b = { { -1.0, 2.0, 0.5 }, { -2.0, -0.5, 0.25 }, { 0.1, 0.3, -3.0 } };
        var a = new double[12, 12];
        for (int k = 0; k < 4; k++)
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) a[3 * k + i, 3 * k + j] = b[i, j];
        return a;
    }
}
