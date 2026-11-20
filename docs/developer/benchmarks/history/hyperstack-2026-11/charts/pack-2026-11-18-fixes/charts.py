"""GridSim AC power-flow benchmark chart pack, regenerated for the regression-fix results (branch perf-rc3).

Reads (read-only) the fixes full-suite comparison.csv (headline + analysis), the old box 2 comparison.csv and the focused base/cand rounds
(before/after on the same box). Writes PNGs to headline/ and analysis/ next to this script, and INDEX.md.
Run:  python charts.py
"""
import os, json, math, statistics as st
import numpy as np, pandas as pd
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, Rectangle
from matplotlib.lines import Line2D
from matplotlib.ticker import FuncFormatter, NullFormatter
from matplotlib.colors import LinearSegmentedColormap, LogNorm

HERE = os.path.dirname(os.path.abspath(__file__))

# Benchmark run folders live outside the repository. Set GRIDSIM_BENCH_ROOT to the folder that holds the
# hyperstack-* run folders. Paths are written into INDEX.md relative to those variables.
def _root(var):
    v = os.environ.get(var)
    if not v:
        raise SystemExit(f"set {var}: see the comment above this function in charts.py")
    return v
BENCH = _root("GRIDSIM_BENCH_ROOT")
def shown(p):
    """A source path as written into INDEX.md: relative to its root variable, forward slashes."""
    for var, root in (("GRIDSIM_BENCH_ROOT", BENCH),):
        rel = os.path.relpath(p, root)
        if not rel.startswith(".."):
            return "$" + var + "/" + rel.replace(os.sep, "/")
    return p
HS3 = os.path.join(BENCH, "hyperstack-3-canada-2026-11-18", "bench-runs")
FULLCAND = os.path.join(HS3, "full-cand-20261118T0918Z", "comparison.csv")
FOCUS_OTH = os.path.join(HS3, "focused-others-20261118T1033Z", "comparison.csv")
FOC_BASE = [os.path.join(HS3, "focused-base-r1-20261118T0841Z", "comparison.csv"), os.path.join(HS3, "focused-base-r2-20261118T0914Z", "comparison.csv"), os.path.join(HS3, "focused-base-r3-20261118T0916Z", "comparison.csv")]
FOC_CAND = [os.path.join(HS3, "focused-cand-r1-20261118T0902Z", "comparison.csv"), os.path.join(HS3, "focused-cand-r2-20261118T0915Z", "comparison.csv"), os.path.join(HS3, "focused-cand-r3-20261118T0917Z", "comparison.csv")]
B2OLD = os.path.join(BENCH, "hyperstack-2-canada-2026-11-17", "suite-full-20261117T2356Z", "comparison.csv")
DPI = 150
GATE = 1e-8
TIE = 1.10

# ---- palette (skill reference palette, light mode) -----------------------------------
SURF = "#fcfcfb"; INK = "#0b0b0b"; INK2 = "#52514e"; MUTED = "#898781"
GRID = "#e1e0d9"; AXIS = "#c3c2b7"
BLUE = "#2a78d6"; ORANGE = "#eb6834"; LBLUE = "#86b6ef"; GRAYF = "#c3c2b7"
# fixed categorical order for per-solver charts; GridSim is always slot 1.
SOLVERS = ["sparse", "matpower", "pandapower", "powsybl", "powermodels", "veragrid", "pypsa", "andes"]
SLOT = {"sparse": "#2a78d6", "matpower": "#eb6834", "pandapower": "#1baf7a", "powsybl": "#eda100",
        "powermodels": "#e87ba4", "veragrid": "#008300", "pypsa": "#6250d6", "andes": "#e34948"}
MARK = {"sparse": "o", "dense": "o", "matpower": "s", "pandapower": "^", "powsybl": "D",
        "powermodels": "v", "veragrid": "P", "pypsa": "X", "andes": "<"}
NAME = {"sparse": "GridSim (sparse)", "dense": "GridSim (dense)", "matpower": "MATPOWER",
        "pandapower": "pandapower", "powsybl": "powsybl", "powermodels": "PowerModels",
        "veragrid": "VeraGrid", "pypsa": "PyPSA", "andes": "ANDES"}
ALLPATHS = ["sparse", "dense"] + SOLVERS[1:]
OTHERS = SOLVERS[1:]
RAMP = LinearSegmentedColormap.from_list("blue", ["#cde2fb", "#86b6ef", "#3987e5", "#256abf", "#184f95", "#0d366b"])

plt.rcParams.update({"font.family": ["Segoe UI", "DejaVu Sans"], "axes.unicode_minus": False,
                     "figure.facecolor": SURF, "axes.facecolor": SURF, "savefig.facecolor": SURF})

FOOT2 = ("Hardware: AMD EPYC 7763 (Zen 3) KVM VM, 28 vCPU; one solver path at a time, pinned to one core, "
         "Release build, tiered JIT off. Method: 67 MATPOWER 8.1 cases, flat start, mean of 10 timed solves, solve time only. "
         "Accuracy gate: 10/10 solves converged and max residual <= 1e-8 pu; a time counts only if it passes. "
         "GridSim = sparse path, branch perf-rc3 (minimum-degree LU ordering, Vm floor 0.01); 'fastest other' = fastest accurate non-GridSim solver (MATPOWER, pandapower, PyPSA, VeraGrid, PowSyBl, ANDES, PowerModels); 10% tie band. "
         "Data: GridSim paths from hyperstack-3 full-cand-20261118T0918Z; other solvers' times from the same-box hyperstack-3 focused-others round (16 cases) and, for the other cases, the hyperstack-2 suite-full-20261117T2356Z run (same CPU class), because the hyperstack-3 full runs measured GridSim paths only.")
FOOTBA = ("Hardware: AMD EPYC 7763 (Zen 3) KVM VM, 28 vCPU (hyperstack-3). Before = GridSim base, after = GridSim candidate (branch perf-rc3: minimum-degree LU ordering, Vm floor 0.01), "
          "both measured on the same box in the focused rounds (median of 3 rounds each, sparse path). One solver path at a time, pinned to one core, Release build, tiered JIT off; flat start, mean of 10 timed solves, solve time only. "
          "Accuracy gate: 10/10 converged and max residual <= 1e-8 pu; a time counts only if it passes. 'Fastest other' = fastest accurate non-GridSim solver from the same-box hyperstack-3 focused-others round; 10% tie band.")


# ---- data ----------------------------------------------------------------------------
def build_fixed():
    fc = pd.read_csv(FULLCAND); fo = pd.read_csv(FOCUS_OTH); old = pd.read_csv(B2OLD)
    for d in (fc, fo, old): d["case"] = d["case"].str.replace(r"\.m$", "", regex=True)
    gs = fc[fc.path.isin(["sparse", "dense"])]
    oth = old[~old.path.isin(["sparse", "dense"]) & ~old.case.isin(fo.case)]
    foc = fo[~fo.path.isin(["sparse", "dense"])]
    m = pd.concat([gs, foc, oth], ignore_index=True)
    return m


def load(p):
    d = pd.read_csv(p) if isinstance(p, str) else p.copy()
    d["case"] = d["case"].str.replace(r"\.m$", "", regex=True)
    d["ok"] = (d.status == "complete") & (d.converged.astype(str) == "10") & (d.max_original_residual_pu <= GATE)

    def cat(r):
        if r.ok: return "pass"
        if r.status == "complete" and str(r.converged) == "10": return "gate"      # ran, residual above gate
        if r.status == "case not materialised": return "unsup"
        return "none"                                                               # no result
    d["cat_"] = d.apply(cat, axis=1)
    return d


def table(d):
    rows = []
    for c, g in d.groupby("case", sort=False):
        b = int(g.buses.iloc[0])
        ms = {r.path: r.mean_ms for r in g.itertuples() if r.ok}
        sp = ms.get("sparse")
        ex = {k: v for k, v in ms.items() if k in OTHERS}
        be = min(ex, key=ex.get) if ex else None
        if sp is None and be is None: cls = "nobody"
        elif sp is None: cls = "loss_nr"
        elif be is None: cls = "win_only"
        else:
            r = ex[be] / sp
            cls = "win" if r > TIE else ("loss" if r < 1 / TIE else "tie")
        rows.append(dict(case=c, buses=b, spv=sp, dnv=ms.get("dense"), best=be,
                         best_ms=ex[be] if be else None, cls=cls,
                         ratio=(ex[be] / sp) if (sp and be) else None, ms=ms))
    return pd.DataFrame(rows).sort_values(["buses", "case"], kind="stable").reset_index(drop=True)


D2 = load(build_fixed())
T2 = table(D2)
CNT2 = T2.cls.value_counts().to_dict()
nwin = CNT2.get("win", 0) + CNT2.get("win_only", 0)
assert (nwin, CNT2.get("tie", 0), CNT2.get("loss", 0) + CNT2.get("loss_nr", 0), CNT2.get("nobody", 0)) == (62, 0, 0, 5), CNT2
LOSSES = T2[T2.cls.isin(["loss", "loss_nr"])]
SOLVABLE = 67 - CNT2["nobody"]
DOLD = load(B2OLD); OLD = table(DOLD)
OLDC = [int(((OLD.cls == "win") | (OLD.cls == "win_only")).sum()), int((OLD.cls == "tie").sum()), int(OLD.cls.isin(["loss", "loss_nr"]).sum()), int((OLD.cls == "nobody").sum())]

# ---- drawing framework -----------------------------------------------------------------
REG = []      # (file, claim, numbers)
_r = None


def wrap(fig, txt, size, weight, maxw):
    out = []
    for para in txt.split("\n"):
        cur = ""
        for wd in para.split():
            t = (cur + " " + wd) if cur else wd
            tx = fig.text(0, 0, t, fontsize=size, fontweight=weight)
            w = tx.get_window_extent(fig.canvas.get_renderer()).width; tx.remove()
            if w > maxw and cur: out.append(cur); cur = wd
            else: cur = t
        out.append(cur)
    return out


class Canvas:
    def __init__(s, w, h, title, sub, foot, tsz=30, ssz=14.5, fsz=8.2, pad=64, tag="GridSim AC power-flow benchmark", banner=None):
        s.w, s.h = w, h
        s.fig = plt.figure(figsize=(w / DPI, h / DPI), dpi=DPI, facecolor=SURF)
        s.fig.canvas.draw()
        y = 34
        if banner:
            bh = 58
            s.fig.add_artist(Rectangle((0, 1 - bh / h), 1, bh / h, color=BLUE, transform=s.fig.transFigure))
            bs = 19
            while True:
                t = s.text(pad, bh / 2, banner, bs, "white", "bold"); wd = t.get_window_extent(s.fig.canvas.get_renderer()).width; t.remove()
                if wd <= w - 2 * pad or bs <= 10: break
                bs -= 0.5
            s.text(pad, bh / 2, banner, bs, "white", "bold")
            y = bh + 26
        else:
            s.text(pad, y, tag.upper(), 8.5, MUTED, "bold", va="top"); y += 30
        for ln in wrap(s.fig, title, tsz, "bold", w - 2 * pad):
            s.text(pad, y, ln, tsz, INK, "bold", va="top"); y += tsz * 2.083 * 1.17
        y += 6
        for ln in wrap(s.fig, sub, ssz, "normal", w - 2 * pad):
            s.text(pad, y, ln, ssz, INK2, "normal", va="top"); y += ssz * 2.083 * 1.32
        s.top = y + 14
        fl = wrap(s.fig, foot, fsz, "normal", w - 2 * pad)
        lh = fsz * 2.083 * 1.3
        yb = h - 26 - lh * len(fl)
        for i, ln in enumerate(fl): s.text(pad, yb + i * lh, ln, fsz, MUTED, "normal", va="top")
        s.fig.add_artist(Line2D([pad / w, 1 - pad / w], [1 - (yb - 10) / h] * 2, color=GRID, lw=0.8))
        s.bot = yb - 20
        s.pad = pad

    def text(s, x, y, t, size, color=INK, weight="normal", ha="left", va="center", **kw):
        return s.fig.text(x / s.w, 1 - y / s.h, t, fontsize=size, color=color, fontweight=weight, ha=ha, va=va, **kw)

    def axes(s, l, t, r, b, **kw):
        ax = s.fig.add_axes([l / s.w, 1 - b / s.h, (r - l) / s.w, (b - t) / s.h], **kw)
        ax.set_facecolor(SURF)
        return ax

    def save(s, path):
        if os.path.exists(path): os.remove(path)
        s.fig.savefig(path, dpi=DPI, facecolor=SURF)
        plt.close(s.fig)


def style(ax, grid="y", ygrid=True):
    for k in ("top", "right"): ax.spines[k].set_visible(False)
    for k in ("left", "bottom"): ax.spines[k].set_color(AXIS); ax.spines[k].set_linewidth(1)
    ax.tick_params(colors=INK2, labelsize=9.5, length=0)
    ax.set_axisbelow(True)
    if grid == "y": ax.grid(axis="y", color=GRID, lw=0.8, ls="-")
    elif grid == "x": ax.grid(axis="x", color=GRID, lw=0.8, ls="-")
    elif grid == "both": ax.grid(color=GRID, lw=0.8, ls="-")
    ax.xaxis.label.set_color(INK2); ax.yaxis.label.set_color(INK2)
    ax.xaxis.label.set_size(10.5); ax.yaxis.label.set_size(10.5)


def num(v): return f"{v:,.0f}" if v >= 100 else (f"{v:.1f}" if v >= 10 else f"{v:.2f}")
def ms_s(v):
    return f"{v:,.0f} ms" if v >= 100 else (f"{v:.1f} ms" if v >= 10 else (f"{v:.2f} ms" if v >= 0.1 else f"{v:.3f} ms"))
def short(c): return c.replace("case_", "").replace("case", "case ") if False else c
def logfmt(ax, which="x"):
    f = FuncFormatter(lambda v, p: f"{v:,.0f}" if v >= 1 else f"{v:g}")
    (ax.xaxis if which == "x" else ax.yaxis).set_major_formatter(f)
    (ax.xaxis if which == "x" else ax.yaxis).set_minor_formatter(NullFormatter())


def out(sub, name): return os.path.join(HERE, sub, name)
def reg(file, claim, numbers): REG.append((file, claim, numbers))


def rr(ax, x, y, w, h, fc, r=3):
    ax.add_patch(FancyBboxPatch((x + r, y + r), w - 2 * r, h - 2 * r, boxstyle=f"round,pad={r},rounding_size={r}", fc=fc, ec="none"))


W2 = T2[T2.cls.isin(["win", "tie", "loss"]) & T2.ratio.notna()]
MAXR = W2.loc[W2.ratio.idxmax()]
ALLWIN = W2[W2.cls == "win"]
ALLMED = float(np.median(ALLWIN.ratio))


def T(case): return T2[T2.case == case].iloc[0]


# ======================================================================================
# HEADLINE (loud). Attitude in titles/banners; every number comes from the data; footers stay honest.
# ======================================================================================
WORD = {0: "ZERO", 1: "ONE", 2: "TWO", 3: "THREE", 4: "FOUR", 5: "FIVE"}
def W_(n): return WORD.get(n, str(n))
ONLY = T2[T2.cls == "win_only"]
NLOSS = len(LOSSES); NTIE = CNT2.get("tie", 0)
ALLR = T2[T2.ratio.notna()]
MIN_ALL = ALLR.loc[ALLR.ratio.idxmin()]; MED_ALL = float(np.median(ALLR.ratio))
assert (ALLR.ratio > TIE).all()


def h_speedup(w, h, fname):
    sq = w == h
    title = f"Up to {MAXR.ratio:.1f}x faster than the field"
    sub = (f"Per-case speedup over the fastest other solver that passed the accuracy gate, on all {len(ALLR)} cases where one passed. GridSim was faster on all {len(ALLR)} "
           f"(median {MED_ALL:.1f}x, lowest {MIN_ALL.ratio:.2f}x); {len(ONLY)} more cases were solved only by GridSim.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=40 if not sq else 28, ssz=14 if not sq else 13, banner="GRIDSIM IS COMING FOR YOUR MODELLING WORK")
    ax = c.axes(110, c.top + 10, w - 80, c.bot - (100 if not sq else 112))
    ax.scatter(ALLR.buses, ALLR.ratio, s=60, color=BLUE, edgecolor=SURF, linewidth=1.4, zorder=3)
    ax.axhline(1, color=INK2, lw=1.2, zorder=2)
    top = math.ceil(MAXR.ratio) + 1
    ax.set_xscale("log"); ax.set_xlim(3, 5000); ax.set_ylim(0, top)
    style(ax, "y"); ax.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); logfmt(ax)
    ax.set_yticks(range(0, top + 1)); ax.set_yticklabels([f"{i}x" for i in range(top + 1)])
    ax.set_xlabel("Grid size (buses, log scale)"); ax.set_ylabel("Speedup vs fastest other solver")
    ax.text(3.2, 1.12, "1x = same speed as the fastest other solver", color=INK2, fontsize=10, va="bottom")
    ax.annotate(f"{MAXR.case}  {MAXR.ratio:.1f}x\n({MAXR.buses:,} buses, vs {NAME[MAXR.best]})", (MAXR.buses, MAXR.ratio),
                (MAXR.buses * 1.5, top - 0.5), fontsize=10.5, color=INK, fontweight="bold",
                arrowprops=dict(arrowstyle="-", color=MUTED, lw=1), va="center")
    ax.annotate(f"{MIN_ALL.case}  {MIN_ALL.ratio:.2f}x (lowest)\n({MIN_ALL.buses:,} buses, vs {NAME[MIN_ALL.best]})", (MIN_ALL.buses, MIN_ALL.ratio),
                (MIN_ALL.buses * 0.55, 1.65), fontsize=10, color=INK2, ha="right",
                arrowprops=dict(arrowstyle="-", color=MUTED, lw=1), va="center")
    c.text(w - 64, c.bot - (40 if not sq else 16), "Catch me if you can.", 26 if not sq else 20, INK, "bold", ha="right")
    c.save(out("headline", fname))
    reg(f"headline/{fname}", title + f" (all {len(ALLR)} cases with an accurate other solver; no cherry-picked size range)",
        f"max {MAXR.ratio:.2f}x ({MAXR.case}, {MAXR.buses} buses; sparse {MAXR.spv:.4g} ms vs {MAXR.best} {MAXR.best_ms:.4g} ms); {len(ALLR)} cases, all wins; "
        f"median {MED_ALL:.2f}x; lowest {MIN_ALL.ratio:.2f}x ({MIN_ALL.case}, vs {MIN_ALL.best}); {len(ONLY)} cases only GridSim solved")


def h_scorecard(w, h, fname):
    sq = w == h
    nl = NLOSS
    title = f"Undefeated: {nwin} of {SOLVABLE}"
    banner = f"{W_(nl)} LOSSES. {W_(NTIE)} TIES. FASTEST ON EVERY SOLVABLE CASE."
    sub = (f"One tile per MATPOWER case, smallest grid first. Blue = GridSim sparse faster than every other accurate solver by more than 10% (or the only solver to pass the gate). "
           f"{CNT2['nobody']} of 67 cases were solved by no solver at flat start; they are not counted as GridSim failures.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=50 if not sq else 40, ssz=14 if not sq else 13, banner=banner)
    colmap = {"win": BLUE, "win_only": BLUE, "tie": LBLUE, "loss": ORANGE, "loss_nr": ORANGE, "nobody": GRAYF}
    cols = 17 if not sq else 12
    rows = math.ceil(67 / cols)
    x0, y0 = 64, c.top + 4
    gridw = w - 2 * 64
    th = 52 if not sq else 32
    ax = c.axes(x0, y0, x0 + gridw, y0 + rows * th)
    ax.set_xlim(0, cols); ax.set_ylim(rows, 0); ax.axis("off")
    for i, r in enumerate(T2.itertuples()):
        rr(ax, i % cols + 0.04, i // cols + 0.05, 0.92, 0.90, colmap[r.cls], r=0.05)
        if r.cls == "win_only":
            ax.text(i % cols + 0.5, i // cols + 0.55, "*", ha="center", va="center", color="white", fontsize=16, fontweight="bold")
    yb = y0 + rows * th + 4
    c.text(x0, yb + 8, f"smallest: {int(T2.buses.min())} buses", 9.5, INK2); c.text(x0 + gridw, yb + 8, f"largest: {int(T2.buses.max()):,} buses", 9.5, INK2, ha="right")
    only = ", ".join(ONLY.case)
    nob = ", ".join(T2[T2.cls == "nobody"].case)
    blocks = [(BLUE, f"{nwin}", f"GridSim faster by more than 10% (1.1x to {MAXR.ratio:.1f}x) or only GridSim passed the gate (*: {only})."),
              (LBLUE, f"{NTIE}", "Within 10% of the fastest other solver: none."),
              (ORANGE, f"{nl}", "GridSim behind: none."),
              (GRAYF, f"{CNT2['nobody']}", "No solver passed the gate (hard at flat start): " + nob + ".")]
    ncol = 2 if not sq else 1
    bw = (gridw - (ncol - 1) * 30) / ncol
    fs = 11.5 if not sq else 11
    ys = [yb + 52, yb + 52]
    for k, (col, n, txt) in enumerate(blocks):
        cc = k % ncol if ncol == 2 else 0
        x = x0 + cc * (bw + 30)
        lines = wrap(c.fig, txt, fs, "normal", bw - 96)
        y = ys[cc if ncol == 2 else 0]
        c.fig.add_artist(Rectangle((x / w, 1 - (y + 12) / h), 18 / w, 18 / h, color=col, transform=c.fig.transFigure))
        c.text(x + 28, y + 2, n, 20, INK, "bold")
        for j, ln in enumerate(lines): c.text(x + 92, y + 2 + (j - (len(lines) - 1) / 2) * fs * 2.083 * 1.2, ln, fs, INK2)
        ys[cc if ncol == 2 else 0] += max(52 if not sq else 50, len(lines) * fs * 2.083 * 1.2 + (26 if not sq else 14))
    assert max(ys) < c.bot + 12, (max(ys), c.bot)
    c.save(out("headline", fname))
    reg(f"headline/{fname}", title + " (zero losses, zero ties)",
        f"fixes: {nwin} wins (incl. {len(ONLY)} where only GridSim passed: {only}), {NTIE} ties, {nl} losses, {CNT2['nobody']} nobody solved, of 67; {SOLVABLE} solvable; "
        f"before the fixes (hyperstack-3 fixes): {OLDC[0]}/{OLDC[1]}/{OLDC[2]}/{OLDC[3]} win/tie/loss/nobody")


REP = ["case118", "case300", "case1197", "case1354pegase"]


def h_saved(w, h, fname):
    sq = w == h
    N = 30000
    rows = [T(c) for c in REP]
    mins = lambda ms: ms * N / 1000 / 60
    sav = [(mins(r.best_ms) - mins(r.spv), r) for r in rows]
    top = max(sav, key=lambda z: z[0])
    title = f"{top[0]:.0f} minutes back per 30,000 solves"
    sub = (f"Single-core time to run 30,000 AC power flows (for example a contingency or Monte Carlo study), GridSim vs the fastest other solver that passed the accuracy gate. "
           f"Four example cases from 118 to 1,354 buses where GridSim is {min(r.ratio for r in rows):.1f}x to {max(r.ratio for r in rows):.1f}x faster; "
           "other grid sizes give different numbers (see the scorecard and the analysis charts).")
    c = Canvas(w, h, title, sub, FOOT2, tsz=40 if not sq else 31, banner="FREE MATHS. FASTER SOLVES. DO THE ARITHMETIC.")
    ax = c.axes(250 if not sq else 220, c.top + 40, w - 150, c.bot - 50)
    mx = max(mins(r.best_ms) for r in rows)
    bh = 0.30
    for i, r in enumerate(rows):
        for j, (val, col) in enumerate(((mins(r.spv), BLUE), (mins(r.best_ms), ORANGE))):
            y = i + (j - 0.5) * (bh + 0.04)
            ax.barh(y, val, height=bh, color=col, zorder=3)
            lab = f"{val:.1f} min" if val >= 1 else f"{val * 60:.0f} s"
            ax.text(val + mx * 0.012, y, lab + ("" if j == 0 else f"  ({NAME[r.best]})"), va="center", fontsize=11, color=INK,
                    fontweight="bold" if j == 0 else "normal")
        ax.text(mx * 1.55, i - 0.40, f"{r.ratio:.1f}x faster, {mins(r.best_ms) - mins(r.spv):.1f} min saved", ha="right", va="center", fontsize=10, color=INK2, bbox=dict(fc=SURF, ec="none", pad=2))
    ax.set_ylim(len(rows) - 0.4, -0.55); ax.set_xlim(0, mx * 1.60)
    ax.set_yticks(range(len(rows))); ax.set_yticklabels([f"{r.case}\n{r.buses:,} buses" for r in rows], fontsize=11.5, color=INK)
    style(ax, "x"); ax.spines["left"].set_visible(True)
    ax.set_xlabel("Minutes for 30,000 solves, one core")
    ax.legend(handles=[Rectangle((0, 0), 1, 1, color=BLUE), Rectangle((0, 0), 1, 1, color=ORANGE)], labels=["GridSim (sparse)", "Fastest other solver"],
              loc="lower right", bbox_to_anchor=(1.0, 1.0), ncol=2, frameon=False, fontsize=10.5, labelcolor=INK2)
    c.save(out("headline", fname))
    reg(f"headline/{fname}", title + " (4 example cases, stated in subtitle)",
        "; ".join(f"{r.case} ({r.buses} buses): GridSim {r.spv:.4g} ms -> {mins(r.spv):.2f} min vs {r.best} {r.best_ms:.4g} ms -> {mins(r.best_ms):.2f} min, saved {mins(r.best_ms) - mins(r.spv):.2f} min ({r.ratio:.2f}x)" for r in rows))


def h_capacity(w, h, fname):
    rows = [T(c) for c in REP]
    per = lambda ms: 86.4e6 / ms / 1e6          # millions of solves per 24 h, one core
    top = max(rows, key=lambda r: per(r.spv) - per(r.best_ms))
    title = f"Up to {per(top.spv):.0f} million power flows per core per day"
    sub = (f"Solves per day on a single pinned core (86,400,000 ms / mean solve time), GridSim vs the fastest other accurate solver, for four example cases from 118 to 1,354 buses "
           f"({min(r.ratio for r in rows):.1f}x to {max(r.ratio for r in rows):.1f}x). Each panel has its own zero-based scale. Other grid sizes give different numbers.")
    c = Canvas(1600, 900, title, sub, FOOT2, tsz=31, banner="CATCH ME IF YOU CAN")
    gw = (1600 - 2 * 64 - 3 * 28) / 4
    for i, r in enumerate(rows):
        l = 64 + i * (gw + 28)
        ax = c.axes(l, c.top + 84, l + gw, c.bot - 56)
        vals = [per(r.spv), per(r.best_ms)]
        ax.bar([0, 1], vals, width=0.55, color=[BLUE, ORANGE], zorder=3)
        for x, v in enumerate(vals):
            ax.text(x, v + max(vals) * 0.02, f"{v:.1f}M" if v < 10 else f"{v:.0f}M", ha="center", va="bottom", fontsize=13, fontweight="bold", color=INK)
        ax.set_ylim(0, max(vals) * 1.18); ax.set_xlim(-0.6, 1.6)
        ax.set_xticks([0, 1]); ax.set_xticklabels(["GridSim", NAME[r.best]], fontsize=10, color=INK2)
        style(ax, "y"); ax.set_yticks([]); ax.spines["left"].set_visible(False)
        ax.yaxis.grid(False)
        c.text(l, c.top + 28, r.case, 13, INK, "bold"); c.text(l, c.top + 50, f"{r.buses:,} buses, {r.ratio:.1f}x", 11, INK2)
    c.text(64, c.bot - 8, "Millions of solves per 24 h on one core. Bars start at zero.", 10, INK2)
    c.save(out("headline", "h6-solves-per-day.png"))
    reg("headline/h6-solves-per-day.png", title + " (4 example cases)",
        "; ".join(f"{r.case}: GridSim {per(r.spv):.2f}M/day vs {r.best} {per(r.best_ms):.2f}M/day" for r in rows))


def h_only(w, h, fname):
    cases = list(ONLY.case)
    title = "Some cases only GridSim solves"
    sub = (f"{' and '.join(cases)}: other solvers either returned no result or returned a 'converged' answer whose power-flow residual is above the 1e-8 pu gate, "
           "so their times are not counted. GridSim sparse and dense both pass. Raw times are shown only for solvers that passed.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=40, banner="THE CASES THEY COULDN'T SOLVE")
    cols = ["sparse", "dense"] + OTHERS
    lw_ = 230
    cw = (w - 2 * 64 - lw_) / len(cols)
    ch = 150
    y0 = c.top + 64
    for j, p in enumerate(cols):
        c.text(64 + lw_ + j * cw + cw / 2, y0 - 26, NAME[p].replace(" (", chr(10) + "("), 10, INK, "bold", ha="center", linespacing=1.15)
    for i, cs in enumerate(cases):
        y = y0 + i * (ch + 14)
        b = int(T2[T2.case == cs].buses.iloc[0])
        c.text(64, y + ch / 2 - 12, cs, 14, INK, "bold"); c.text(64, y + ch / 2 + 14, f"{b:,} buses", 11, INK2)
        for j, p in enumerate(cols):
            r = D2[(D2.case == cs) & (D2.path == p)].iloc[0]
            x = 64 + lw_ + j * cw
            ax = c.axes(x + 3, y, x + cw - 3, y + ch); ax.axis("off"); ax.set_xlim(0, 1); ax.set_ylim(1, 0)
            col = BLUE if r.ok else (GRID if r.cat_ == "gate" else SURF)
            ax.add_patch(Rectangle((0, 0), 1, 1, fc=col, ec="none" if r.cat_ != "none" else AXIS, lw=1))
            if r.ok:
                ax.text(0.5, 0.40, ms_s(r.mean_ms), color="white", ha="center", va="center", fontsize=12, fontweight="bold")
                ax.text(0.5, 0.64, "passed", color="white", ha="center", va="center", fontsize=10)
            elif r.cat_ == "gate":
                ax.text(0.5, 0.36, "failed gate", color=INK, ha="center", va="center", fontsize=10, fontweight="bold")
                ax.text(0.5, 0.60, f"residual\n{r.max_original_residual_pu:.2g} pu", color=INK2, ha="center", va="center", fontsize=9.5, linespacing=1.3)
            else:
                ax.text(0.5, 0.5, "no result", color=MUTED, ha="center", va="center", fontsize=10.5)
    yb = y0 + len(cases) * (ch + 14)
    pp = D2[(D2.case == cases[0]) & (D2.path == "pandapower")].iloc[0]
    if pp.status == "complete" and pp.cat_ == "gate":
        c.text(64, yb + 4, f"Note: pandapower is faster on the clock on {cases[0]} ({pp.mean_ms:.0f} ms) but its residual is {pp.max_original_residual_pu:.1f} pu, which is not a solved power flow.", 11, INK2, va="top")
    c.save(out("headline", fname))
    g = lambda cs, p: D2[(D2.case == cs) & (D2.path == p)].iloc[0]
    reg(f"headline/{fname}", title,
        "; ".join(f"{cs} sparse {g(cs,'sparse').mean_ms:.4g} ms (residual {g(cs,'sparse').max_original_residual_pu:.2g}), dense {g(cs,'dense').mean_ms:.4g} ms" for cs in cases) + "; no other path passes these cases")


def h_maths(w, h, fname):
    title = "Built on 300-year-old maths. Free."
    sub = ("Newton's method (1600s-1700s), Gauss-Seidel (1800s) and Kirchhoff's laws (1845): free, open mathematics that nobody owns, and the basis of AC power flow. "
           "Expensive specialist software is built on the same public ideas. Here is what that maths plus careful engineering does today.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=44, banner="NEWTON. GAUSS. SEIDEL. KIRCHHOFF. ALL FREE.")
    ax = c.axes(150, c.top + 5, w - 150, c.top + 200)
    ax.set_xlim(1600, 2060); ax.set_ylim(0, 1); ax.axis("off")
    ax.plot([1600, 2060], [0.3, 0.3], color=INK2, lw=1.4, zorder=1)
    for yr in range(1600, 2060, 100):
        ax.plot([yr] * 2, [0.3, 0.26], color=INK2, lw=1); ax.text(yr, 0.15, str(yr), ha="center", va="center", fontsize=11, color=INK2)
    ax.add_patch(Rectangle((1600, 0.34), 200, 0.32, fc=LBLUE, ec="none")); ax.text(1700, 0.45, "Newton's method\n1600s-1700s", ha="center", va="center", fontsize=11, color=INK, fontweight="bold", linespacing=1.15)
    ax.add_patch(Rectangle((1800, 0.34), 100, 0.32, fc="#6da7ec", ec="none")); ax.text(1838, 0.45, "Gauss-Seidel\n1800s", ha="center", va="center", fontsize=11, color=INK, fontweight="bold", linespacing=1.15)
    ax.plot([1845, 1845], [0.66, 0.78], color=INK2, lw=1); ax.scatter([1845], [0.66], s=40, color=INK2, marker="D", zorder=3); ax.text(1845, 0.84, "Kirchhoff's laws, 1845", ha="center", va="center", fontsize=12, color=INK, fontweight="bold")
    ax.plot([2026, 2026], [0.30, 0.78], color=BLUE, lw=2); ax.text(2026, 0.84, "GridSim, 2026", ha="center", va="center", fontsize=12.5, color=BLUE, fontweight="bold")
    ax.scatter([2026], [0.3], s=90, color=BLUE, zorder=3)
    # stat tiles
    tiles = [(f"{nwin} of {SOLVABLE}", "cases GridSim is fastest on, of those any solver solved"),
             (f"{NLOSS}", "cases GridSim loses, after the regression fixes"),
             (f"{MAXR.ratio:.1f}x", f"best speedup over the fastest other solver ({MAXR.case})")]
    tw = (w - 128 - 2 * 30) / 3
    for i, (big, lab) in enumerate(tiles):
        x = 64 + i * (tw + 30); y = c.top + 215
        c.fig.add_artist(Rectangle((x / w, 1 - (y + 165) / h), tw / w, 165 / h, color="#f1f0eb", transform=c.fig.transFigure))
        c.text(x + 24, y + 55, big, 46, BLUE, "bold")
        for j, ln in enumerate(wrap(c.fig, lab, 13.5, "normal", tw - 48)): c.text(x + 24, y + 112 + j * 26, ln, 13.5, INK2)
    assert c.top + 390 < c.bot + 10, (c.top, c.bot)
    c.save(out("headline", fname))
    reg(f"headline/{fname}", title + " (history from the brief: Newton 1600s-1700s, Gauss-Seidel 1800s, Kirchhoff 1845)",
        f"{nwin} of {SOLVABLE} wins, {NLOSS} losses, max speedup {MAXR.ratio:.2f}x ({MAXR.case}); history dates are not data-derived")


def h_catch(w, h, fname):
    title = "Catch me if you can."
    sub = ("GridSim is coming for your modelling work. Here's where I'm at. If you start now, you might catch me by the end of next year.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=64, ssz=20, banner="THE INCUMBENTS: THIS IS YOUR NOTICE")
    former = [r for r in OLD.itertuples() if r.cls in ("loss", "loss_nr")]
    fixed = [r for r in former if T2.set_index("case").loc[r.case, "cls"] in ("win", "win_only", "tie")]
    tiles = [(f"{nwin}", f"wins of {SOLVABLE} solvable cases"),
             (f"{NLOSS}", "losses"),
             (f"{MAXR.ratio:.1f}x", f"best speedup vs the fastest other solver"),
             (f"{len(fixed)} of {len(former)}", "former losses turned into wins by the regression fixes")]
    tw = (w - 128 - 3 * 24) / 4
    y = c.top + 30
    for i, (big, lab) in enumerate(tiles):
        x = 64 + i * (tw + 24)
        c.fig.add_artist(Rectangle((x / w, 1 - (y + 250) / h), tw / w, 250 / h, color="#f1f0eb", transform=c.fig.transFigure))
        c.text(x + 22, y + 75, big, 54 if len(big) < 7 else 44, BLUE, "bold")
        for j, ln in enumerate(wrap(c.fig, lab, 13.5, "normal", tw - 44)): c.text(x + 22, y + 140 + j * 28, ln, 13.5, INK2)
    c.text(64, y + 295, "Free maths, 100+ years old. Measured, gated, reproducible.", 17, INK, "bold")
    assert y + 320 < c.bot, (y, c.bot)
    c.save(out("headline", fname))
    reg(f"headline/{fname}", title,
        f"{nwin} wins of {SOLVABLE}, {NLOSS} losses, best speedup {MAXR.ratio:.2f}x ({MAXR.case}); {len(fixed)} of {len(former)} former box-2 losses are now wins: " + ", ".join(r.case for r in fixed))


# ======================================================================================
# ANALYSIS
# ======================================================================================
def a_time_vs_buses():
    title = "Solve time vs grid size, all solvers"
    sub = "Mean solve time of each solver on every case it solved within the accuracy gate (hyperstack-3 fixes). Log-log. Cases a solver failed are simply absent for that solver; see the accuracy matrix."
    c = Canvas(1600, 1000, title, sub, FOOT2, tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1290, c.bot - 50)
    for p in ["dense"] + OTHERS + ["sparse"]:
        pts = [(r.buses, r.ms[p]) for r in T2.itertuples() if p in r.ms]
        if not pts: continue
        x, y = zip(*pts)
        if p == "sparse":
            ax.plot(x, y, color=BLUE, lw=2, marker="o", ms=5, mfc=BLUE, mec=SURF, mew=1.2, zorder=6, solid_capstyle="round")
        elif p == "dense":
            ax.plot(x, y, color=BLUE, lw=1, alpha=0.55, marker="o", ms=4.5, mfc=SURF, mec=BLUE, mew=1.1, zorder=5)
        else:
            ax.plot(x, y, color=SLOT[p], lw=0.9, alpha=0.35, zorder=3)
            ax.plot(x, y, ls="none", marker=MARK[p], ms=5, color=SLOT[p], mec=SURF, mew=0.8, zorder=4)
    ax.set_xscale("log"); ax.set_yscale("log"); style(ax, "both"); logfmt(ax, "x"); logfmt(ax, "y")
    ax.set_xlabel("Grid size (buses, log scale)"); ax.set_ylabel("Mean solve time (ms, log scale)")
    hs = [Line2D([], [], color=SLOT[p] if p != "dense" else BLUE, marker=MARK[p], ms=6, lw=1.2, mfc=SURF if p == "dense" else SLOT[p], mec=SLOT.get(p, BLUE)) for p in ALLPATHS]
    c.fig.legend(hs, [NAME[p] for p in ALLPATHS], loc="center left", bbox_to_anchor=(1310 / 1600, 0.5), frameon=False, fontsize=11, labelcolor=INK2, title="Solver", title_fontsize=11)
    c.save(out("analysis", "a1-time-vs-buses.png"))
    reg("analysis/a1-time-vs-buses.png", "GridSim sparse sits at or below the lowest curve over most of the size range; the gap narrows and flips above ~2,000 buses.", "all solver times in comparison.csv (hyperstack-3 fixes)")


def a_speedup_vs_buses():
    title = "Speedup over the fastest other solver, by grid size"
    sub = "Each dot is one case: ms of the fastest other accurate solver divided by GridSim sparse ms. Above the 1x line GridSim is faster. The four lowest-ratio cases are labelled; the strip below holds cases no solver solved. There are no cases below 1x."
    c = Canvas(1600, 1000, title, sub, FOOT2, tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1500, c.bot - 190)
    d = T2[T2.ratio.notna()]
    colm = {"win": BLUE, "tie": LBLUE, "loss": ORANGE}
    for k in ("win", "tie", "loss"):
        g = d[d.cls == k]
        if g.empty: continue
        ax.scatter(g.buses, g.ratio, s=62, color=colm[k], edgecolor=SURF, linewidth=1.5, zorder=4, label={"win": "GridSim faster (>10%)", "tie": "Within 10%", "loss": "GridSim slower (>10%)"}[k])
    ax.axhline(1, color=INK2, lw=1.2, zorder=2)
    ax.set_xscale("log"); ax.set_yscale("log"); ax.set_ylim(0.45, 12); ax.set_xlim(3, 4500)
    style(ax, "both"); ax.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); ax.set_xticklabels([])
    ax.set_yticks([0.5, 1, 2, 5, 10]); ax.set_yticklabels(["0.5x", "1x", "2x", "5x", "10x"])
    ax.set_ylabel("Speedup (log scale)")
    low = d.sort_values("ratio").head(4)
    ax.text(1750, 0.90, "Lowest four ratios:", fontsize=9.5, color=INK2, ha="right", va="center")
    for k, r in enumerate(low.itertuples()):
        ax.text(1750, 0.77 * (0.86 ** k), f"{r.case} ({r.buses:,} buses): {r.ratio:.2f}x vs {NAME[r.best]}", fontsize=9.5, color=INK, ha="right", va="center")
    ax.legend(loc="lower left", frameon=False, fontsize=10.5, labelcolor=INK2)
    ax2 = c.axes(110, c.bot - 170, 1500, c.bot - 50)
    ax2.set_xscale("log"); ax2.set_xlim(3, 4500); ax2.set_ylim(0, 2.2); style(ax2, "x"); ax2.set_yticks([])
    ax2.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); logfmt(ax2, "x"); ax2.set_xlabel("Grid size (buses, log scale)")
    nr = T2[T2.cls == "loss_nr"]; nb = T2[T2.cls == "nobody"]
    ax2.scatter(nr.buses, [1.45] * len(nr), s=62, color=ORANGE, edgecolor=SURF, linewidth=1.5, zorder=4, marker="X")
    ax2.scatter(nb.buses, [0.45] * len(nb), s=62, color=GRAYF, edgecolor=SURF, linewidth=1.5, zorder=4)
    ax2.text(3.2, 0.9, "No solver passed (hard at flat start):", fontsize=9.5, color=INK2, va="center")
    c.save(out("analysis", "a2-speedup-vs-buses.png"))
    reg("analysis/a2-speedup-vs-buses.png", "Every case with a result is a win; no dot is below the 1x line.", "lowest ratios: " + "; ".join(f"{r.case} {r.ratio:.2f}x" for r in low.itertuples()) + f"; median {float(np.median(d.ratio)):.2f}x; {len(d)} cases")


BANDS = [("4-30 buses", 0, 30), ("31-100", 31, 100), ("101-1,000", 101, 1000), ("1,001-2,000", 1001, 2000), ("2,001-3,400", 2001, 4000)]
CATC = [("win", BLUE, "GridSim faster (>10%)"), ("tie", LBLUE, "Within 10%"), ("loss", ORANGE, "GridSim slower or no result"), ("nobody", GRAYF, "No solver passed")]


def a_bands():
    title = "Wins, ties and losses by grid size band"
    sub = "Number of cases per size band, by outcome (sparse vs fastest other accurate solver, 10% tie band). The two cases only GridSim solved are counted as wins."
    c = Canvas(1600, 880, title, sub, FOOT2, tsz=26, ssz=13)
    ax = c.axes(220, c.top + 10, 1500, c.bot - 118)
    T = T2.copy(); T["k"] = T.cls.map({"win_only": "win", "loss_nr": "loss"}).fillna(T.cls)
    for i, (lab, lo, hi) in enumerate(BANDS):
        g = T[(T.buses >= lo) & (T.buses <= hi)]
        left = 0
        for k, col, _ in CATC:
            n = int((g.k == k).sum())
            if n:
                ax.barh(i, n - 0.06, left=left, height=0.5, color=col, zorder=3)
                if n >= 2: ax.text(left + (n - 0.06) / 2, i, str(n), ha="center", va="center", fontsize=11, fontweight="bold", color="white" if k in ("win", "loss") else INK)
                elif n == 1: ax.text(left + 0.47, i, "1", ha="center", va="center", fontsize=11, fontweight="bold", color="white" if k in ("win", "loss") else INK)
            left += n
        ax.text(left + 0.4, i, f"{len(g)} cases", va="center", fontsize=10.5, color=INK2)
    ax.set_ylim(len(BANDS) - 0.4, -0.6); ax.set_yticks(range(len(BANDS))); ax.set_yticklabels([b[0] for b in BANDS], fontsize=11.5, color=INK)
    style(ax, "x"); ax.set_xlabel("Number of cases"); ax.set_xlim(0, 34)
    c.fig.legend([Rectangle((0, 0), 1, 1, color=col) for _, col, _ in CATC], [t for _, _, t in CATC], loc="upper center", ncol=4, frameon=False, fontsize=10.5, labelcolor=INK2, bbox_to_anchor=(0.5, 1 - (c.bot - 52) / 880))
    c.save(out("analysis", "a3-outcomes-by-size-band.png"))
    parts = []
    for lab, lo, hi in BANDS:
        g = T[(T.buses >= lo) & (T.buses <= hi)]
        parts.append(f"{lab}: " + "/".join(str(int((g.k == k).sum())) for k, _, _ in CATC))
    reg("analysis/a3-outcomes-by-size-band.png", "Wins in every size band; no losses and no ties (win/tie/loss/nobody per band).", "; ".join(parts))


def cell_marks(ax, x, y, cat, w=0.9, h=0.9):
    """draw failure marks for a cell centred on (x,y)"""
    if cat == "gate":
        ax.add_patch(Rectangle((x - w / 2, y - h / 2), w, h, fc=GRAYF, ec="none"))
        ax.plot([x - w * 0.22, x + w * 0.22], [y, y], color=INK2, lw=1.2, solid_capstyle="round")
    elif cat in ("none", "unsup"):
        ax.add_patch(Rectangle((x - w / 2, y - h / 2), w, h, fc=SURF, ec=GRID, lw=0.8))
        ax.plot([x - w * 0.2, x + w * 0.2], [y - h * 0.2, y + h * 0.2], color=MUTED, lw=1.1)
        ax.plot([x - w * 0.2, x + w * 0.2], [y + h * 0.2, y - h * 0.2], color=MUTED, lw=1.1)


def a_heatmap():
    n = len(T2)
    H = 350 + n * 22 + 130
    title = "Time relative to the fastest accurate solver, every case and solver"
    sub = ("Cells show a solver's mean time divided by the fastest time that passed the gate on that case (hyperstack-3 fixes). Light = fastest, dark = slower, capped at 100x. "
           "Dot = fastest on that case. Grey dash = ran but failed the 1e-8 pu gate. Cross = no result. Cases ordered by size.")
    c = Canvas(1600, H, title, sub, FOOT2, tsz=24, ssz=12.5)
    cols = ["sparse", "dense"] + OTHERS
    ax = c.axes(250, c.top + 56, 1250, c.top + 56 + n * 22)
    ax.set_xlim(-0.5, len(cols) - 0.5); ax.set_ylim(n - 0.5, -0.5)
    norm = LogNorm(1, 100)
    for i, r in enumerate(T2.itertuples()):
        passed = {p: v for p, v in r.ms.items()}
        mn = min(passed.values()) if passed else None
        for j, p in enumerate(cols):
            if p in passed:
                v = passed[p] / mn
                ax.add_patch(Rectangle((j - 0.46, i - 0.45), 0.92, 0.9, fc=RAMP(norm(min(max(v, 1), 100))), ec="none"))
                if v == 1: ax.plot(j, i, "o", ms=4.5, color=INK)
            else:
                cat = D2[(D2.case == r.case) & (D2.path == p)].cat_.iloc[0]
                cell_marks(ax, j, i, cat, 0.92, 0.9)
    ax.set_yticks(range(n)); ax.set_yticklabels([f"{r.case}  {r.buses}" for r in T2.itertuples()], fontsize=7.4, color=INK2)
    ax.xaxis.tick_top(); ax.set_xticks(range(len(cols))); ax.set_xticklabels([NAME[p].replace(" (", chr(10) + "(") for p in cols], fontsize=9.5, color=INK, rotation=0)
    ax.tick_params(length=0)
    for k in ax.spines.values(): k.set_visible(False)
    c.text(250 - 12, c.top + 36, "case   buses", 8.5, MUTED, ha="right")
    cax = c.axes(1290, c.top + 60, 1310, c.top + 60 + 300)
    cax.imshow(np.linspace(0, 1, 256)[:, None], aspect="auto", cmap=RAMP, extent=[0, 1, 0, 1]); cax.set_xticks([]); cax.yaxis.tick_right()
    cax.set_yticks([0, 1 / 3, 2 / 3, 1]); cax.set_yticklabels(["100x+", "10x", "3x", "1x"][::-1][::-1] if False else ["1x", "3x", "10x", "100x+"][::-1][::-1])
    # correct log positions: 1x at top (value 1 -> top), 100x at bottom
    cax.set_yticks([1, 1 - math.log10(3) / 2, 0.5, 0]); cax.set_yticklabels(["1x", "3x", "10x", "100x+"])
    cax.tick_params(colors=INK2, labelsize=9, length=0)
    for k in cax.spines.values(): k.set_visible(False)
    c.save(out("analysis", "a4-heatmap-relative-time.png"))
    reg("analysis/a4-heatmap-relative-time.png", "Per-case, per-solver view: who is fastest, who is far behind, who failed, for all 67 cases.", "relative time = solver mean_ms / min passing mean_ms on that case")


def a_matrix():
    n = len(T2); half = math.ceil(n / 2)
    H = 300 + half * 24 + 270
    title = "Who solved what: accuracy matrix"
    sub = "Every case and solver (hyperstack-3 fixes) under the accuracy gate. Filled blue = passed (10/10 converged, residual <= 1e-8 pu). Grey dash = ran but residual above the gate. Cross = no result (did not converge / no output). Cases ordered by size."
    c = Canvas(1600, H, title, sub, FOOT2, tsz=26, ssz=12.5)
    cols = ["sparse", "dense"] + OTHERS
    for part in range(2):
        sub_t = T2.iloc[part * half:(part + 1) * half]
        l = 60 + part * 780
        ax = c.axes(l + 200, c.top + 90, l + 680, c.top + 90 + half * 24)
        ax.set_xlim(-0.5, len(cols) - 0.5); ax.set_ylim(len(sub_t) - 0.5, -0.5)
        for i, r in enumerate(sub_t.itertuples()):
            for j, p in enumerate(cols):
                row = D2[(D2.case == r.case) & (D2.path == p)].iloc[0]
                if row.ok: ax.add_patch(Rectangle((j - 0.45, i - 0.43), 0.9, 0.86, fc=BLUE if p in ("sparse", "dense") else "#6da7ec", ec="none"))
                else: cell_marks(ax, j, i, row.cat_, 0.9, 0.86)
            if r.cls in ("loss", "loss_nr", "nobody"):
                ax.add_patch(Rectangle((-0.5, i - 0.5), len(cols), 1, fc="none", ec=ORANGE if r.cls != "nobody" else MUTED, lw=1.2))
        ax.set_yticks(range(len(sub_t))); ax.set_yticklabels([f"{r.case}  ({r.buses})" for r in sub_t.itertuples()], fontsize=8.5, color=INK2)
        ax.xaxis.tick_top(); ax.set_xticks(range(len(cols))); ax.set_xticklabels([NAME[p].replace("GridSim ", "GS ") for p in cols], fontsize=8.2, color=INK, rotation=40, ha="left")
        ax.tick_params(length=0)
        for k in ax.spines.values(): k.set_visible(False)
    yl = c.top + 90 + half * 24 + 36
    items = [(BLUE, "passed (GridSim)"), ("#6da7ec", "passed (other solver)"), (GRAYF, "ran, failed gate"), (SURF, "no result")]
    x = 60
    for col, t in items:
        c.fig.add_artist(Rectangle((x / 1600, 1 - (yl + 8) / H), 16 / 1600, 16 / H, fc=col, ec=GRID if col == SURF else "none", transform=c.fig.transFigure))
        c.text(x + 24, yl, t, 10, INK2); x += 230
    c.text(60, yl + 30, "Row outline: orange = GridSim behind; grey = no solver passed.", 10, INK2)
    npass = {p: int((D2[D2.path == p].ok).sum()) for p in cols}
    c.text(60, yl + 60, "Cases passed of 67: " + ", ".join(f"{NAME[p]} {npass[p]}" for p in cols), 10, INK2)
    c.save(out("analysis", "a5-accuracy-matrix.png"))
    reg("analysis/a5-accuracy-matrix.png", f"GridSim sparse passes {npass['sparse']} of 67 cases, more than any other solver; the five it does not pass are the cases nobody solved.", "passes of 67: " + ", ".join(f"{NAME[p]} {npass[p]}" for p in cols))


# ======================================================================================
# BEFORE / AFTER THE FIXES (same box: hyperstack-3 focused base vs cand rounds)
# ======================================================================================
FULLBASE = os.path.join(HS3, "full-base-20261118T1004Z", "comparison.csv")


def rounds_sparse(paths):
    cols = []
    for p in paths:
        d = load(p); d = d[d.path == "sparse"].set_index("case")
        cols.append(d.mean_ms.where(d.ok))
    m = pd.concat(cols, axis=1)
    return m.median(axis=1, skipna=True), m.notna().sum(axis=1)


BASE_MS, BASE_N = rounds_sparse(FOC_BASE)
CAND_MS, CAND_N = rounds_sparse(FOC_CAND)
FOC = pd.DataFrame({"base": BASE_MS, "cand": CAND_MS, "nb": BASE_N, "nc": CAND_N})
FOC["buses"] = [int(T2[T2.case == c].buses.iloc[0]) for c in FOC.index]
FOC["other"] = [T2[T2.case == c].best_ms.iloc[0] for c in FOC.index]
FOC["best"] = [T2[T2.case == c].best.iloc[0] for c in FOC.index]
FOC["x"] = FOC.base / FOC.cand
FORMER = [r.case for r in OLD.itertuples() if r.cls in ("loss", "loss_nr")]
assert set(FORMER) == {"case_ACTIVSg2000", "case3012wp", "case3120sp", "case3375wp", "case2848rte"}, FORMER
BASEFULL_MS = None


def fixes_charts():
    # ---------------- f1: old vs new sparse ms, focused cases
    F = FOC.sort_values("base", ascending=False, na_position="first")
    n = len(F)
    nsolved = int(FOC.x.notna().sum())
    title = "Before and after the fixes: GridSim sparse time, same box"
    sub = (f"The {n} focused cases, GridSim sparse mean solve time before (base code) and after (candidate: minimum-degree LU ordering, Vm floor 0.01), median of 3 rounds each on the same machine, log scale. "
           f"Orange = fastest accurate other solver. Speedup from the fixes ranges {FOC.x.min():.2f}x to {FOC.x.max():.1f}x across the {nsolved} cases both versions solved; case2848rte had no result before.")
    c = Canvas(1600, 1000, title, sub, FOOTBA, tsz=26, ssz=13)
    ax = c.axes(290, c.top + 40, 1330, c.bot - 50)
    for i, (cs, r) in enumerate(F.iterrows()):
        if not pd.isna(r.base): ax.plot([r.cand, r.base], [i, i], color=GRID, lw=2, zorder=2)
        if pd.isna(r.base):
            ax.text(r.cand * 0.8, i, "no result before", ha="right", va="center", fontsize=9.5, color=INK2)
        else:
            ax.scatter([r.base], [i], s=60, color=LBLUE, edgecolor=SURF, linewidth=1, zorder=3)
        ax.scatter([r.cand], [i], s=60, color=BLUE, edgecolor=SURF, linewidth=1, zorder=4)
        if not pd.isna(r.other): ax.scatter([r.other], [i], s=60, marker="D", color=ORANGE, edgecolor=SURF, linewidth=1, zorder=3)
        txt = f"{r.x:.1f}x faster" if not pd.isna(r.x) else "now solved"
        ax.text(1.12, i, txt, transform=ax.get_yaxis_transform(), va="center", fontsize=10, color=INK, fontweight="bold")
    ax.set_xscale("log"); style(ax, "both"); logfmt(ax, "x")
    ax.set_xlim(0.2, 1500)
    ax.set_ylim(n - 0.5, -0.7); ax.set_yticks(range(n)); ax.set_yticklabels([f"{cs}  ({int(r.buses):,})" for cs, r in F.iterrows()], fontsize=10, color=INK)
    ax.set_xlabel("Mean solve time (ms, log scale)")
    ax.legend(handles=[Line2D([], [], ls="none", marker="o", color=LBLUE, ms=7), Line2D([], [], ls="none", marker="o", color=BLUE, ms=7), Line2D([], [], ls="none", marker="D", color=ORANGE, ms=7)],
              labels=["GridSim before (base)", "GridSim after (candidate)", "Fastest other accurate solver"], loc="lower right", frameon=False, fontsize=10.5, labelcolor=INK2)
    c.save(out("analysis", "f1-before-after-sparse-ms.png"))
    reg("analysis/f1-before-after-sparse-ms.png", "Fixes cut GridSim sparse time on every focused case that was solved before; case2848rte is solved only after.",
        "; ".join(f"{cs} {r.base:.4g} -> {r.cand:.4g} ms ({r.x:.2f}x)" if not pd.isna(r.x) else f"{cs} no result -> {r.cand:.4g} ms" for cs, r in F.iterrows()))

    # ---------------- f2: speedup per case from the fixes
    G = FOC[FOC.x.notna()].sort_values("x", ascending=True)
    title = "Speedup from the fixes, per case"
    sub = (f"GridSim sparse time before divided by after, same box, median of 3 focused rounds each. Bars start at zero; 1x = unchanged. "
           f"{len(G)} cases solved by both versions; median {G.x.median():.1f}x. case2848rte is not shown: no result before the fixes.")
    c = Canvas(1600, 900, title, sub, FOOTBA, tsz=26, ssz=13)
    ax = c.axes(250, c.top + 20, 1500, c.bot - 50)
    ax.barh(range(len(G)), G.x, height=0.62, color=BLUE, zorder=3)
    for i, (cs, r) in enumerate(G.iterrows()):
        ax.text(r.x + G.x.max() * 0.01, i, f"{r.x:.2f}x  ({r.base:.4g} to {r.cand:.4g} ms)", va="center", fontsize=9.5, color=INK)
    ax.axvline(1, color=INK2, lw=1.2, zorder=4)
    ax.set_yticks(range(len(G))); ax.set_yticklabels([f"{cs}  ({int(r.buses):,})" for cs, r in G.iterrows()], fontsize=10, color=INK)
    ax.set_ylim(-0.6, len(G) - 0.4); ax.set_xlim(0, G.x.max() * 1.45)
    style(ax, "x"); ax.set_xlabel("Speedup from the fixes (before ms / after ms)")
    ax.set_xticks(range(0, int(G.x.max()) + 2, 1)); ax.set_xticklabels([f"{i}x" for i in range(0, int(G.x.max()) + 2)])
    c.save(out("analysis", "f2-speedup-from-fixes.png"))
    reg("analysis/f2-speedup-from-fixes.png", "Speedup from the fixes per focused case (same box).",
        f"median {G.x.median():.2f}x; max {G.x.max():.2f}x ({G.x.idxmax()}); min {G.x.min():.2f}x ({G.x.idxmin()}); " + "; ".join(f"{cs} {r.x:.2f}x" for cs, r in G.sort_values('x', ascending=False).iterrows()))

    # ---------------- f3: the former losses become wins
    cases = sorted(FORMER, key=lambda c_: -(FOC.loc[c_, "buses"]))
    title = "The five former losses are now wins"
    sub = ("Cases where GridSim was behind (or had no result) in the box 2 run, now measured before and after the fixes on the same box. Each panel has its own zero-based scale: "
           "GridSim before, GridSim after, fastest other accurate solver (hyperstack-3 focused-others round).")
    c = Canvas(1600, 900, title, sub, FOOTBA, tsz=30, ssz=13)
    gw = (1600 - 2 * 64 - 4 * 24) / 5
    nums = []
    for i, cs in enumerate(cases):
        r = FOC.loc[cs]
        l = 64 + i * (gw + 24)
        ax = c.axes(l, c.top + 130, l + gw, c.bot - 62)
        vals = [r.base, r.cand, r.other]; cols = [LBLUE, BLUE, ORANGE]
        mx = np.nanmax(vals)
        for k, (v, col) in enumerate(zip(vals, cols)):
            if pd.isna(v):
                ax.text(k, mx * 0.04, "no\nresult", ha="center", va="bottom", fontsize=10, color=MUTED, fontweight="bold")
            else:
                ax.bar(k, v, width=0.62, color=col, zorder=3)
                ax.text(k, v + mx * 0.02, f"{v:.0f}" if v >= 100 else f"{v:.1f}", ha="center", va="bottom", fontsize=11.5, fontweight="bold", color=INK)
        ax.set_ylim(0, mx * 1.2); ax.set_xlim(-0.6, 2.6)
        ax.set_xticks([0, 1, 2]); ax.set_xticklabels(["before", "after", NAME[r.best]], fontsize=9.5, color=INK2)
        style(ax, "y"); ax.set_yticks([]); ax.spines["left"].set_visible(False); ax.yaxis.grid(False)
        rb = r.other / r.base if not pd.isna(r.base) else None; ra = r.other / r.cand
        c.text(l, c.top + 28, cs, 13, INK, "bold"); c.text(l, c.top + 52, f"{int(r.buses):,} buses", 11, INK2)
        t1 = f"before: {rb:.2f}x ({'behind' if rb < 1 else 'ahead'})" if rb else "before: no result"
        c.text(l, c.top + 78, t1, 11, INK2); c.text(l, c.top + 100, f"after: {ra:.2f}x faster", 12, BLUE, "bold")
        nums.append(f"{cs}: before {r.base:.4g} ms, after {r.cand:.4g} ms, fastest other {r.other:.4g} ms ({r.best}); ratio before {rb:.2f}x, after {ra:.2f}x" if rb else f"{cs}: before no result, after {r.cand:.4g} ms, fastest other {r.other:.4g} ms ({r.best}); ratio after {ra:.2f}x")
    c.text(64, c.bot - 8, "Bars: mean solve time in ms, one core. Ratio = fastest other accurate solver's ms / GridSim sparse ms (above 1x GridSim is faster).", 10, INK2)
    c.save(out("analysis", "f3-former-losses-now-wins.png"))
    reg("analysis/f3-former-losses-now-wins.png", "All five former losses (ACTIVSg2000, 3012wp, 3120sp, 3375wp, 2848rte) are wins after the fixes.", "; ".join(nums))

    # ---------------- f4: case2848rte now solved
    cs = "case2848rte"
    rows = D2[(D2.case == cs)].copy()
    ok = rows[rows.ok & rows.path.isin(["sparse"] + OTHERS)].sort_values("mean_ms")
    title = "case2848rte: from no result to solved"
    sub = (f"A 2,848-bus case. Before the fixes GridSim sparse returned no converged result (focused base rounds: {int(FOC.loc[cs, 'nb'])} of 3 passed the gate). After the fixes it passes in "
           f"{FOC.loc[cs, 'cand']:.0f} ms (median of 3 rounds). Mean solve time of every solver that passed the gate on this case; bars start at zero. Other solvers from the hyperstack-3 focused-others round.")
    c = Canvas(1600, 700, title, sub, FOOTBA, tsz=28, ssz=13)
    ax = c.axes(250, c.top + 20, 1500, c.bot - 50)
    names = []; vals = []; cols = []
    names.append("GridSim before"); vals.append(np.nan); cols.append(LBLUE)
    names.append("GridSim after"); vals.append(float(FOC.loc[cs, "cand"])); cols.append(BLUE)
    for r in ok[ok.path != "sparse"].itertuples(): names.append(NAME[r.path]); vals.append(r.mean_ms); cols.append(ORANGE)
    mx = np.nanmax(vals)
    for i, (nme, v, col) in enumerate(zip(names, vals, cols)):
        if np.isnan(v): ax.text(mx * 0.01, i, "no result: did not converge", va="center", fontsize=11, color=INK, fontweight="bold")
        else:
            ax.barh(i, v, height=0.6, color=col, zorder=3); ax.text(v + mx * 0.01, i, ms_s(v), va="center", fontsize=11, color=INK, fontweight="bold" if col == BLUE else "normal")
    ax.set_yticks(range(len(names))); ax.set_yticklabels(names, fontsize=11.5, color=INK); ax.set_ylim(len(names) - 0.4, -0.6); ax.set_xlim(0, mx * 1.2)
    style(ax, "x"); ax.set_xlabel("Mean solve time (ms)")
    c.save(out("analysis", "f4-case2848rte-now-solved.png"))
    reg("analysis/f4-case2848rte-now-solved.png", "case2848rte: no GridSim result before, solved and fastest after.",
        f"after {FOC.loc[cs,'cand']:.4g} ms; others passing: " + ", ".join(f"{NAME[r.path]} {r.mean_ms:.4g} ms" for r in ok[ok.path != 'sparse'].itertuples()) + f"; base rounds passing gate: {int(FOC.loc[cs,'nb'])} of 3")

    # ---------------- f5: full suite before/after (full-base vs full-cand, same box, GridSim sparse)
    fb = load(FULLBASE); fb = fb[fb.path == "sparse"].set_index("case"); fb = fb.mean_ms.where(fb.ok)
    fcd = D2[D2.path == "sparse"].set_index("case"); fcm = fcd.mean_ms.where(fcd.ok)
    bu = fcd.buses
    both = fb.notna() & fcm.reindex(fb.index).notna()
    x = (fb[both] / fcm.reindex(fb.index)[both])
    title = "Whole suite before and after the fixes, same box"
    sub = (f"GridSim sparse time before (full-base run) divided by after (full-cand run), one dot per case solved by both ({len(x)} cases), hyperstack-3, one run each. "
           f"Median {x.median():.2f}x; {int((x > 1.1).sum())} cases more than 10% faster, {int((x < 1 / 1.1).sum())} more than 10% slower. Small cases are unchanged within noise; the 2,000+ bus cases gain most.")
    c = Canvas(1600, 900, title, sub, FOOTBA.replace("both measured on the same box in the focused rounds (median of 3 rounds each, sparse path)", "both measured on the same box in the full-suite runs (one run each, sparse path)"), tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1500, c.bot - 50)
    ax.scatter(bu.reindex(x.index), x, s=54, color=BLUE, edgecolor=SURF, linewidth=1.3, zorder=3)
    ax.axhline(1, color=INK2, lw=1.2, zorder=2)
    ax.set_xscale("log"); ax.set_yscale("log"); style(ax, "both"); ax.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); logfmt(ax, "x")
    ax.set_yticks([0.5, 1, 2, 4, 8, 16]); ax.set_yticklabels(["0.5x", "1x", "2x", "4x", "8x", "16x"]); ax.set_ylim(0.4, 20)
    ax.set_xlabel("Grid size (buses, log scale)"); ax.set_ylabel("Speedup from the fixes (log scale)")
    top = x.sort_values(ascending=False).head(3)
    for k, (cs, v) in enumerate(top.items()):
        ax.annotate(f"{cs}  {v:.1f}x", (bu[cs], v), (1500, 15.5 / (1.35 ** k)), fontsize=9.5, color=INK, ha="right", va="center", arrowprops=dict(arrowstyle="-", color=MUTED, lw=0.8))
    c.save(out("analysis", "f5-whole-suite-before-after.png"))
    reg("analysis/f5-whole-suite-before-after.png", "Whole-suite same-box before/after: small cases flat, large cases much faster.",
        f"{len(x)} cases; median {x.median():.3f}x; max {x.max():.2f}x ({x.idxmax()}); min {x.min():.2f}x ({x.idxmin()}); newly solved vs full-base: " + ", ".join(c_ for c_ in fcm.dropna().index if c_ not in fb.dropna().index))


def a_dense_sparse():
    title = "GridSim sparse vs dense path"
    sub = "Dense solve time divided by sparse solve time per case, for cases where both passed the gate (hyperstack-3 fixes). Above 1x the sparse path is faster."
    c = Canvas(1600, 900, title, sub, FOOT2, tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1500, c.bot - 50)
    g = T2[T2.spv.notna() & T2.dnv.notna()].copy(); g["r"] = g.dnv / g.spv
    ax.scatter(g.buses, g.r, s=60, color=BLUE, edgecolor=SURF, linewidth=1.5, zorder=4)
    ax.axhline(1, color=INK2, lw=1.2, zorder=2)
    ax.set_xscale("log"); ax.set_yscale("log"); style(ax, "both")
    ax.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); logfmt(ax, "x"); ax.set_ylim(0.6, 130); ax.set_yticks([1, 2, 5, 10, 20, 50, 100]); ax.set_yticklabels(["1x", "2x", "5x", "10x", "20x", "50x", "100x"])
    ax.set_xlabel("Grid size (buses, log scale)"); ax.set_ylabel("Dense time / sparse time (log scale)")
    lo = g.loc[g.r.idxmin()]; hi = g.loc[g.r.idxmax()]
    ax.annotate(f"{hi.case}  {hi.r:.0f}x", (hi.buses, hi.r), (hi.buses * 0.9, hi.r * 1.9), fontsize=10, color=INK, ha="center", va="center", arrowprops=dict(arrowstyle="-", color=MUTED, lw=0.8))
    ax.annotate(f"{lo.case}  {lo.r:.2f}x", (lo.buses, lo.r), (lo.buses * 1.3, lo.r * 0.75), fontsize=10, color=INK, ha="left", va="center", arrowprops=dict(arrowstyle="-", color=MUTED, lw=0.8))
    cross = g[g.r <= 1].buses.tolist()
    c.save(out("analysis", "a7-sparse-vs-dense.png"))
    reg("analysis/a7-sparse-vs-dense.png", "Sparse is faster than dense on every case where both passed, with the advantage growing from ~1x at 4-6 buses to 10-60x at 1,000+ buses; no crossover." if not cross else "Sparse is faster than dense on nearly every case; see numbers for exceptions.",
        f"min ratio {lo.r:.2f}x ({lo.case}), max {hi.r:.1f}x ({hi.case}); {len(g)} cases both passed; cases with dense faster or equal: {cross if cross else 'none'}")


def a_large():
    d = T2[T2.buses >= 2000].reset_index(drop=True)
    title = "Cases of 2,000+ buses: where the fixes landed"
    sub = ("GridSim sparse vs fastest other accurate solver on all cases with 2,000+ buses (hyperstack-3 fixes). Bars start at zero. This is where the regression fixes landed: "
           "GridSim is now ahead on every one that any solver solved. Some cases are intentionally hard at flat start.")
    c = Canvas(1600, 1250, title, sub, FOOT2, tsz=26, ssz=13)
    ax = c.axes(330, c.top + 50, 1540, c.bot - 70)
    mx = max(d.spv.max(), d.best_ms.max())
    bh = 0.30
    for i, r in enumerate(d.itertuples()):
        y0 = i - 0.17; y1 = i + 0.17
        if not pd.isna(r.spv):
            ax.barh(y0, r.spv, height=bh, color=BLUE, zorder=3); ax.text(r.spv + mx * 0.01, y0, ms_s(r.spv), va="center", fontsize=9.5, color=INK)
        else:
            ax.text(mx * 0.01, y0, "GridSim: no result (did not converge)", va="center", fontsize=9.5, color=INK, fontweight="bold")
        if not pd.isna(r.best):
            ax.barh(y1, r.best_ms, height=bh, color=ORANGE, zorder=3); ax.text(r.best_ms + mx * 0.01, y1, f"{ms_s(r.best_ms)}  {NAME[r.best]}", va="center", fontsize=9.5, color=INK)
        else:
            ax.text(mx * 0.01, y1, "no other solver passed" if r.cls != "nobody" else "no solver passed (any)", va="center", fontsize=9.5, color=INK2)
        if r.cls in ("loss", "loss_nr"):
            tag = "GridSim behind: no result" if r.cls == "loss_nr" else f"GridSim {1 / r.ratio:.1f}x slower"
            ax.text(mx * 1.55, i, tag, va="center", ha="right", fontsize=10.5, color=INK, fontweight="bold")
            ax.add_patch(Rectangle((mx * 1.56, i - 0.38), mx * 0.012, 0.76, fc=ORANGE, ec="none", clip_on=False))
        elif r.cls in ("win", "tie"):
            ax.text(mx * 1.55, i, (f"{r.ratio:.2f}x faster" if r.cls == "win" else f"tie ({r.ratio:.2f}x)"), va="center", ha="right", fontsize=10.5, color=INK2)
        elif r.cls == "win_only":
            ax.text(mx * 1.55, i, "only GridSim passed", va="center", ha="right", fontsize=10.5, color=INK2)
    ax.set_ylim(len(d) - 0.4, -0.6); ax.set_xlim(0, mx * 1.60)
    ax.set_yticks(range(len(d))); ax.set_yticklabels([f"{r.case}  ({r.buses:,})" for r in d.itertuples()], fontsize=10.5, color=INK)
    style(ax, "x"); ax.set_xlabel("Mean solve time (ms)")
    ax.legend(handles=[Rectangle((0, 0), 1, 1, color=BLUE), Rectangle((0, 0), 1, 1, color=ORANGE)], labels=["GridSim (sparse)", "Fastest other accurate solver"],
              loc="lower left", bbox_to_anchor=(0.0, 1.0), ncol=2, frameon=False, fontsize=10.5, labelcolor=INK2)
    c.save(out("analysis", "a8-large-cases.png"))
    tg = d[d.cls.isin(["loss", "loss_nr"])]
    reg("analysis/a8-large-cases.png", "All cases with 2,000+ buses: GridSim vs the fastest other accurate solver.",
        "; ".join(f"{r.case} ({r.buses}): " + (f"{r.ratio:.2f}x" if pd.notna(r.ratio) else r.cls) for r in d.itertuples()))


def write_index():
    L = ["# GridSim benchmark chart pack: regression fixes (2026-11-18)", "",
         f"Sources (read-only): GridSim paths from `{shown(FULLCAND)}` (hyperstack-3 full-cand, EPYC 7763 28 vCPU); other solvers from `{shown(FOCUS_OTH)}` (same box, 16 focused cases) and, for the remaining cases, `{shown(B2OLD)}` (hyperstack-2, same CPU class), because the hyperstack-3 full runs measured GridSim dense/sparse only. "
         f"Before/after charts: focused base rounds {len(FOC_BASE)} x / cand rounds {len(FOC_CAND)} x under `{shown(HS3)}`, and full-base `{shown(FULLBASE)}`. Old box 2 (RC-3) scorecard comes from `{shown(B2OLD)}`. "
         "Generated by `charts.py` (python charts.py). Method: one core, Release, tiered JIT off, mean of 10 timed solves, solve time only, flat start, 67 MATPOWER 8.1 cases. "
         "Accuracy gate: status complete, converged 10, max residual <= 1e-8 pu. Win/tie/loss: sparse vs fastest accurate other solver, 10% tie band.", "",
         f"Scorecard here: {nwin} wins / {NTIE} ties / {NLOSS} losses / {CNT2['nobody']} nobody solved. Old box 2 (RC-3): {OLDC[0]} / {OLDC[1]} / {OLDC[2]} / {OLDC[3]}.", "",
         "Headline tone is deliberately loud; every number comes from the data and the small print states hardware, method and gate. Squares are 1080x1080.", "",
         "## Headline charts", ""]
    for f, cl, n in REG:
        if f.startswith("headline"): L += [f"- `{f}`", f"  - Claim: {cl}", f"  - Numbers: {n}", ""]
    L += ["## Analysis charts (full suite, fixed code)", ""]
    for f, cl, n in REG:
        if f.startswith("analysis") and not os.path.basename(f).startswith("f"): L += [f"- `{f}`", f"  - Claim: {cl}", f"  - Numbers: {n}", ""]
    L += ["## Before / after the fixes (same box, focused rounds)", ""]
    for f, cl, n in REG:
        if f.startswith("analysis") and os.path.basename(f).startswith("f"): L += [f"- `{f}`", f"  - Claim: {cl}", f"  - Numbers: {n}", ""]
    L += ["## Notes", "",
          "- The old pack's a6 chart (box 1 vs box 2) is not repeated here: box 1 ran the old code, so it is not like-for-like with the fixed run.",
          "- Headline speedup chart covers all cases with an accurate other solver; the time-saved and capacity charts use four named example cases stated in their subtitles.",
          "- Nobody solved: " + ", ".join(T2[T2.cls == 'nobody'].case) + ".",
          "- Per-solver palette: skill reference slots in a fixed order (GridSim blue first); marker shapes, a legend and fixed order carry identity on multi-solver charts.",
          "- History dates on the maths chart (Newton 1600s-1700s, Gauss-Seidel 1800s, Kirchhoff 1845) are from the brief, not the data; no company or commercial product is named."]
    open(os.path.join(HERE, "INDEX.md"), "w", encoding="utf-8").write("\n".join(L) + "\n")


if __name__ == "__main__":
    h_catch(1600, 900, "h1-catch-me-if-you-can.png")
    h_scorecard(1600, 900, "h2-undefeated-scorecard.png"); h_scorecard(1080, 1080, "h2-undefeated-scorecard-square.png")
    h_speedup(1600, 900, "h3-speedup-vs-field.png"); h_speedup(1080, 1080, "h3-speedup-vs-field-square.png")
    h_saved(1600, 900, "h4-time-saved-30000-solves.png"); h_saved(1080, 1080, "h4-time-saved-30000-solves-square.png")
    h_maths(1600, 900, "h5-built-on-300-year-old-maths.png")
    h_capacity(1600, 900, "h6-solves-per-day.png")
    h_only(1600, 900, "h7-only-gridsim-solves.png")
    a_time_vs_buses(); a_speedup_vs_buses(); a_bands(); a_heatmap(); a_matrix(); a_dense_sparse(); a_large()
    fixes_charts()
    write_index()
    print("done", CNT2, OLDC)
