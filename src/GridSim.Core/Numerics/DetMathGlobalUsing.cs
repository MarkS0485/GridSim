// D1 (October 2026, Mark's determinism rule 2026-10-04): every file in GridSim.Core can name the deterministic maths
// module as plain "DetMath" (DetMath.Sin, DetMath.Phase, ...) without a using directive for GridSim.Core.Numerics,
// which would bring that namespace's other types into scope everywhere. See DetMath for why System.Math's
// transcendental functions are not used; DeterministicMathGuardTests enforces it.
global using DetMath = GridSim.Core.Numerics.DetMath;
