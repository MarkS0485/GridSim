### simdcheck

`gridsim simdcheck [--expect <tier>]`

| Flag | Default | Meaning |
|---|---|---|
| `--expect` | - | Gate mode: exit 3 when the active tier is narrower than the given one (`Avx512`, `Avx256`, `Sse128`, `Scalar`; case-insensitive). Any other value exits 2. Use it in CI on a box that should run 8-wide. |
