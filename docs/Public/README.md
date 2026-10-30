# Public reference documents (optional download)

`docs/Public/GB` is a git submodule: a link to a separate public repository,
<https://github.com/MarkS0485/GridSim-GB-Documents>, that holds the GB grid documents (NESO, Grid Code, SQSS, FRCR, ETYS, DNO and
standards material, with its source catalogue and fetch tools) from which GridSim's cited values and numbers were taken.

It is not part of GridSim and is not downloaded by a normal clone: the folder stays empty until you ask for it. To
fetch it (about 265 MB), from the repository root:

```
git submodule update --init docs/Public/GB
```

Or clone GridSim with it in one go:

```
git clone --recurse-submodules https://github.com/MarkS0485/GridSim.git
```

To bring it up to the latest version of that repository later:

```
git submodule update --remote docs/Public/GB
```

The repository and branch the link follows are recorded in `.gitmodules` at the repository root.

The rules every submodule follows (optional, never needed, cannot change the core maths) are in
[`SUBMODULES.md`](../../SUBMODULES.md) at the repository root.
