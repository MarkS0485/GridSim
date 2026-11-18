"""GridSim AC power-flow benchmark chart pack.

Reads (read-only) the box 2 and box 1 comparison.csv, writes PNGs to
headline/ and analysis/ next to this script, and writes INDEX.md.
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
B2 = r"W:\Benchmarks\hyperstack-2-canada-2026-11-17\suite-full-20261117T2356Z\comparison.csv"
B1 = r"W:\Benchmarks\hyperstack-1-norway-2026-11-17\suite-full-20261117T2159Z\comparison.csv"
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
         "GridSim = sparse path unless stated; 'fastest other' = fastest accurate non-GridSim solver; 10% tie band. "
         "Data: benchmark box 2 (hyperstack-2-canada, suite-full-20261117T2356Z), GridSim 2.1.071 RC-3.")
FOOT1 = FOOT2.replace("AMD EPYC 7763 (Zen 3) KVM VM, 28 vCPU", "AMD EPYC 7452 (Zen 2) KVM VM, 4 vCPU") \
    .replace("box 2 (hyperstack-2-canada, suite-full-20261117T2356Z)", "box 1 (hyperstack-1-norway, suite-full-20261117T2159Z)")
FOOTBOTH = ("Hardware: box 2 = AMD EPYC 7763 (Zen 3) KVM VM, 28 vCPU; box 1 = AMD EPYC 7452 (Zen 2) KVM VM, 4 vCPU. "
            "Method on both: one solver path at a time, pinned to one core, Release build, tiered JIT off; 67 MATPOWER 8.1 cases, flat start, "
            "mean of 10 timed solves, solve time only. Accuracy gate: 10/10 converged and max residual <= 1e-8 pu. "
            "Data: hyperstack-2-canada suite-full-20261117T2356Z and hyperstack-1-norway suite-full-20261117T2159Z.")


# ---- data ----------------------------------------------------------------------------
def load(p):
    d = pd.read_csv(p)
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


D2, D1 = load(B2), load(B1)
T2, T1 = table(D2), table(D1)
CNT2 = T2.cls.value_counts().to_dict(); CNT1 = T1.cls.value_counts().to_dict()
nwin = CNT2.get("win", 0) + CNT2.get("win_only", 0)
assert (nwin, CNT2.get("tie", 0), CNT2.get("loss", 0) + CNT2.get("loss_nr", 0), CNT2.get("nobody", 0)) == (54, 3, 5, 5), CNT2
assert (CNT1.get("win", 0) + CNT1.get("win_only", 0), CNT1.get("tie", 0), CNT1.get("loss", 0) + CNT1.get("loss_nr", 0), CNT1.get("nobody", 0)) == (54, 3, 5, 5), CNT1
LOSSES = T2[T2.cls.isin(["loss", "loss_nr"])]
assert set(LOSSES.case) == {"case_ACTIVSg2000", "case3012wp", "case3120sp", "case3375wp", "case2848rte"}
SOLVABLE = 67 - CNT2["nobody"]

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
    def __init__(s, w, h, title, sub, foot, tsz=30, ssz=14.5, fsz=8.2, pad=64, tag="GridSim AC power-flow benchmark"):
        s.w, s.h = w, h
        s.fig = plt.figure(figsize=(w / DPI, h / DPI), dpi=DPI, facecolor=SURF)
        s.fig.canvas.draw()
        y = 34
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


# ---- numbers shared by the headline charts ---------------------------------------------
W2 = T2[T2.cls.isin(["win", "tie", "loss"]) & T2.ratio.notna()]
SMALL = T2[(T2.buses <= 1400) & T2.ratio.notna()]
MAXR = W2.loc[W2.ratio.idxmax()]
SMALL_MED = float(np.median(SMALL.ratio)); SMALL_MIN = SMALL.loc[SMALL.ratio.idxmin()]
SMALL_UNSOLVED = T2[(T2.buses <= 1400) & (T2.cls == "nobody")]
assert (SMALL.ratio > TIE).all()
ALLWIN = W2[W2.cls == "win"]
ALLMED = float(np.median(ALLWIN.ratio))


def T(case): return T2[T2.case == case].iloc[0]


# ======================================================================================
# HEADLINE
# ======================================================================================
def h_speedup(w, h, fname):
    sq = w == h
    title = f"Up to {MAXR.ratio:.1f}x faster than the fastest other solver"
    sub = (f"Per-case speedup of GridSim over the fastest other solver that passed the accuracy gate, on the {len(SMALL)} cases of up to 1,400 buses that at "
           f"least one solver solved. GridSim was faster on all {len(SMALL)} (median {SMALL_MED:.1f}x, lowest {SMALL_MIN.ratio:.1f}x). "
           "Larger cases are not shown here: GridSim is behind on some of them, see the scorecard.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=31 if not sq else 28)
    ax = c.axes(110, c.top + 10, w - 80, c.bot - 60)
    ax.scatter(SMALL.buses, SMALL.ratio, s=70, color=BLUE, edgecolor=SURF, linewidth=1.5, zorder=3)
    ax.axhline(1, color=INK2, lw=1.2, zorder=2)
    ax.set_xscale("log"); ax.set_xlim(3, 1800); ax.set_ylim(0, 8.5)
    style(ax, "y"); logfmt(ax); ax.set_xticks([4, 10, 30, 100, 300, 1000]); logfmt(ax)
    ax.set_yticks(range(0, 9)); ax.set_yticklabels([f"{i}x" for i in range(9)])
    ax.set_xlabel("Grid size (buses, log scale)"); ax.set_ylabel("Speedup vs fastest other solver")
    ax.text(3.2, 1.12, "1x = same speed as the fastest other solver", color=INK2, fontsize=10, va="bottom")
    ax.annotate(f"{MAXR.case}  {MAXR.ratio:.1f}x\n({MAXR.buses:,} buses, vs {NAME[MAXR.best]})", (MAXR.buses, MAXR.ratio),
                (MAXR.buses * 1.5, 7.95), fontsize=10.5, color=INK, fontweight="bold",
                arrowprops=dict(arrowstyle="-", color=MUTED, lw=1), va="center")
    ax.annotate(f"{SMALL_MIN.case}  {SMALL_MIN.ratio:.1f}x" + chr(10) + f"({SMALL_MIN.buses:,} buses, vs {NAME[SMALL_MIN.best]})", (SMALL_MIN.buses, SMALL_MIN.ratio),
                (SMALL_MIN.buses * 0.9, 1.55), fontsize=10, color=INK2, ha="right",
                arrowprops=dict(arrowstyle="-", color=MUTED, lw=1), va="center")
    c.save(out("headline", fname))
    reg(f"headline/{fname}", title + " (cases <= 1,400 buses; range stated in subtitle)",
        f"max {MAXR.ratio:.2f}x ({MAXR.case}, {MAXR.buses} buses; sparse {MAXR.spv:.4g} ms vs {MAXR.best} {MAXR.best_ms:.4g} ms); "
        f"{len(SMALL)} cases <=1,400 buses, all wins; median {SMALL_MED:.2f}x; min {SMALL_MIN.ratio:.2f}x ({SMALL_MIN.case}); "
        f"median over all {len(ALLWIN)} outright wins {ALLMED:.2f}x")


def h_scorecard(w, h, fname):
    sq = w == h
    nl = len(LOSSES)
    title = f"GridSim fastest on {nwin} of {SOLVABLE} solvable cases"
    sub = (f"One tile per MATPOWER case, smallest grid first. Blue = GridSim sparse faster than every other accurate solver by more than 10%. "
           f"{CNT2['nobody']} of 67 cases were solved by no solver at flat start; they are not counted as GridSim failures.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=34 if not sq else 30, ssz=14 if not sq else 13.5)
    colmap = {"win": BLUE, "win_only": BLUE, "tie": LBLUE, "loss": ORANGE, "loss_nr": ORANGE, "nobody": GRAYF}
    cols = 17 if not sq else 12
    rows = math.ceil(67 / cols)
    x0, y0 = 64, c.top + 4
    gridw = w - 2 * 64
    cw_ = gridw / cols
    th = 68 if not sq else 36
    ax = c.axes(x0, y0, x0 + gridw, y0 + rows * th)
    ax.set_xlim(0, cols); ax.set_ylim(rows, 0); ax.axis("off")
    for i, r in enumerate(T2.itertuples()):
        rr(ax, i % cols + 0.04, i // cols + 0.05, 0.92, 0.90, colmap[r.cls], r=0.05)
        if r.cls == "win_only":
            ax.text(i % cols + 0.5, i // cols + 0.55, "*", ha="center", va="center", color="white", fontsize=16, fontweight="bold")
    yb = y0 + rows * th + 4
    c.text(x0, yb + 8, "smallest: 4 buses", 9.5, INK2); c.text(x0 + gridw, yb + 8, "largest: 3,374 buses", 9.5, INK2, ha="right")
    ties = ", ".join(T2[T2.cls == "tie"].case)
    loss = ", ".join(f"{r.case} ({'no GridSim result' if r.cls == 'loss_nr' else f'{1 / r.ratio:.1f}x slower'})" for r in LOSSES.itertuples())
    nob = ", ".join(T2[T2.cls == "nobody"].case)
    blocks = [(BLUE, f"{nwin}", f"GridSim faster by more than 10% (1.1x to {MAXR.ratio:.1f}x). * = only GridSim passed the gate: case2736sp, case2746wop."),
              (LBLUE, f"{CNT2['tie']}", "Within 10% of the fastest other solver: " + ties + "."),
              (ORANGE, f"{nl}", "GridSim behind: " + loss + "."),
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
    reg(f"headline/{fname}", title,
        f"box 2: {nwin} wins (incl. 2 where only GridSim passed: case2736sp, case2746wop), {CNT2['tie']} ties, {nl} losses, {CNT2['nobody']} nobody solved, of 67; "
        f"{SOLVABLE} solvable; box 1 identical ({CNT1.get('win',0)+CNT1.get('win_only',0)}/{CNT1.get('tie',0)}/{CNT1.get('loss',0)+CNT1.get('loss_nr',0)}/{CNT1.get('nobody',0)}); "
        f"losses: {loss}")


REP = ["case118", "case300", "case1197", "case1354pegase"]


def h_saved(w, h, fname):
    sq = w == h
    N = 30000
    rows = [T(c) for c in REP]
    mins = lambda ms: ms * N / 1000 / 60
    sav = [(mins(r.best_ms) - mins(r.spv), r) for r in rows]
    top = max(sav, key=lambda z: z[0])
    title = f"Up to {top[0]:.0f} minutes saved per 30,000 solves"
    sub = (f"Single-core time to run 30,000 AC power flows (for example a contingency or Monte Carlo study), GridSim vs the fastest other solver that passed the accuracy gate. "
           f"Four example cases from 118 to 1,354 buses where GridSim is {min(r.ratio for r in rows):.1f}x to {max(r.ratio for r in rows):.1f}x faster; "
           "not representative of the cases above 2,000 buses (see the scorecard).")
    c = Canvas(w, h, title, sub, FOOT2, tsz=34 if not sq else 31)
    ax = c.axes(250 if not sq else 220, c.top + 40, w - 150, c.bot - 50)
    mx = max(mins(r.best_ms) for r in rows)
    bh = 0.30
    for i, r in enumerate(rows):
        for j, (val, col) in enumerate(((mins(r.spv), BLUE), (mins(r.best_ms), ORANGE))):
            y = i + (j - 0.5) * (bh + 0.04)
            rr(ax, 0, y - bh / 2, val, bh, col, r=0) if False else ax.barh(y, val, height=bh, color=col, zorder=3)
            lab = f"{val:.1f} min" if val >= 1 else f"{val * 60:.0f} s"
            ax.text(val + mx * 0.012, y, lab + ("" if j == 0 else f"  ({NAME[r.best]})"), va="center", fontsize=11, color=INK,
                    fontweight="bold" if j == 0 else "normal")
        ax.text(mx * 1.55, i - 0.40, f"{r.ratio:.1f}x faster, {mins(r.best_ms) - mins(r.spv):.1f} min saved", ha="right", va="center", fontsize=10, color=INK2)
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
           f"({min(r.ratio for r in rows):.1f}x to {max(r.ratio for r in rows):.1f}x). Each panel has its own zero-based scale. Not representative of cases above 2,000 buses.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=31)
    gw = (w - 2 * 64 - 3 * 28) / 4
    for i, r in enumerate(rows):
        l = 64 + i * (gw + 28)
        ax = c.axes(l, c.top + 84, l + gw, c.bot - 36)
        vals = [per(r.spv), per(r.best_ms)]
        ax.bar([0, 1], vals, width=0.55, color=[BLUE, ORANGE], zorder=3)
        for x, v in enumerate(vals):
            ax.text(x, v + max(vals) * 0.02, f"{v:.1f}M" if v < 10 else f"{v:.0f}M", ha="center", va="bottom", fontsize=13, fontweight="bold", color=INK)
        ax.set_ylim(0, max(vals) * 1.18); ax.set_xlim(-0.6, 1.6)
        ax.set_xticks([0, 1]); ax.set_xticklabels(["GridSim", NAME[r.best]], fontsize=10, color=INK2)
        style(ax, "y"); ax.set_yticks([]); ax.spines["left"].set_visible(False)
        ax.yaxis.grid(False)
        c.text(l, c.top + 28, r.case, 13, INK, "bold"); c.text(l, c.top + 50, f"{r.buses:,} buses, {r.ratio:.1f}x", 11, INK2)
    c.text(64, c.bot + 2, "Millions of solves per 24 h on one core. Bars start at zero.", 10, INK2)
    c.save(out("headline", fname))
    reg(f"headline/{fname}", title + " (4 example cases)",
        "; ".join(f"{r.case}: GridSim {per(r.spv):.2f}M/day vs {r.best} {per(r.best_ms):.2f}M/day" for r in rows))


def h_only(w, h, fname):
    cases = ["case2736sp", "case2746wop"]
    title = "Only GridSim passes the gate on case2736sp and case2746wop"
    sub = ("Two 2,700-bus cases. Other solvers either returned no result or returned a 'converged' answer whose power-flow residual is above the 1e-8 pu gate, "
           "so their times are not counted. GridSim sparse and dense both pass. Raw times are shown only for solvers that passed.")
    c = Canvas(w, h, title, sub, FOOT2, tsz=28)
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
    yb = y0 + 2 * (ch + 14)
    pp = D2[(D2.case == "case2736sp") & (D2.path == "pandapower")].iloc[0]
    c.text(64, yb + 4, f"Note: pandapower is faster on the clock on case2736sp ({pp.mean_ms:.0f} ms) but its residual is {pp.max_original_residual_pu:.1f} pu, which is not a solved power flow.", 11, INK2, va="top")
    c.save(out("headline", fname))
    g = lambda cs, p: D2[(D2.case == cs) & (D2.path == p)].iloc[0]
    reg(f"headline/{fname}", title,
        f"case2736sp sparse {g('case2736sp','sparse').mean_ms:.4g} ms (residual {g('case2736sp','sparse').max_original_residual_pu:.2g}), dense {g('case2736sp','dense').mean_ms:.4g} ms; "
        f"case2746wop sparse {g('case2746wop','sparse').mean_ms:.4g} ms (residual {g('case2746wop','sparse').max_original_residual_pu:.2g}), dense {g('case2746wop','dense').mean_ms:.4g} ms; "
        f"no other path passes either case; pandapower case2736sp {pp.mean_ms:.4g} ms but residual {pp.max_original_residual_pu:.3g} pu")


# ======================================================================================
# ANALYSIS
# ======================================================================================
def a_time_vs_buses():
    title = "Solve time vs grid size, all solvers"
    sub = "Mean solve time of each solver on every case it solved within the accuracy gate (box 2). Log-log. Cases a solver failed are simply absent for that solver; see the accuracy matrix."
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
    reg("analysis/a1-time-vs-buses.png", "GridSim sparse sits at or below the lowest curve over most of the size range; the gap narrows and flips above ~2,000 buses.", "all solver times in comparison.csv (box 2)")


def a_speedup_vs_buses():
    title = "Speedup over the fastest other solver, by grid size"
    sub = "Each dot is one case: ms of the fastest other accurate solver divided by GridSim sparse ms. Above the 1x line GridSim is faster. All five losses are labelled; the strips below hold cases with no GridSim result or no solver result."
    c = Canvas(1600, 1000, title, sub, FOOT2, tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1500, c.bot - 190)
    d = T2[T2.ratio.notna()]
    colm = {"win": BLUE, "tie": LBLUE, "loss": ORANGE}
    for k in ("win", "tie", "loss"):
        g = d[d.cls == k]
        ax.scatter(g.buses, g.ratio, s=62, color=colm[k], edgecolor=SURF, linewidth=1.5, zorder=4, label={"win": "GridSim faster (>10%)", "tie": "Within 10%", "loss": "GridSim slower (>10%)"}[k])
    ax.axhline(1, color=INK2, lw=1.2, zorder=2)
    ax.set_xscale("log"); ax.set_yscale("log"); ax.set_ylim(0.15, 10); ax.set_xlim(3, 4500)
    style(ax, "both"); ax.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); ax.set_xticklabels([])
    ax.set_yticks([0.2, 0.5, 1, 2, 5, 10]); ax.set_yticklabels(["0.2x", "0.5x", "1x", "2x", "5x", "10x"])
    ax.set_ylabel("Speedup (log scale)")
    lab_y = {"case3120sp": 0.62, "case3375wp": 0.44, "case3012wp": 0.31, "case_ACTIVSg2000": 0.22}
    for r in d[d.cls == "loss"].itertuples():
        ax.annotate(f"{r.case} ({r.ratio:.2f}x, {NAME[r.best]})", (r.buses, r.ratio), (1750, lab_y[r.case]), fontsize=9.5, color=INK, ha="right", va="center",
                    arrowprops=dict(arrowstyle="-", color=MUTED, lw=0.8, shrinkA=3, shrinkB=4))
    ax.legend(loc="lower left", frameon=False, fontsize=10.5, labelcolor=INK2)
    ax2 = c.axes(110, c.bot - 170, 1500, c.bot - 50)
    ax2.set_xscale("log"); ax2.set_xlim(3, 4500); ax2.set_ylim(0, 2.2); style(ax2, "x"); ax2.set_yticks([])
    ax2.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); logfmt(ax2, "x"); ax2.set_xlabel("Grid size (buses, log scale)")
    nr = T2[T2.cls == "loss_nr"]; nb = T2[T2.cls == "nobody"]
    ax2.scatter(nr.buses, [1.45] * len(nr), s=62, color=ORANGE, edgecolor=SURF, linewidth=1.5, zorder=4, marker="X")
    ax2.scatter(nb.buses, [0.45] * len(nb), s=62, color=GRAYF, edgecolor=SURF, linewidth=1.5, zorder=4)
    ax2.text(3.2, 1.9, "GridSim no result, another solver passed:", fontsize=9.5, color=INK2, va="center")
    for r in nr.itertuples(): ax2.text(r.buses * 0.93, 1.45, r.case, fontsize=9.5, color=INK, va="center", ha="right")
    ax2.text(3.2, 0.9, "No solver passed (hard at flat start):", fontsize=9.5, color=INK2, va="center")
    c.save(out("analysis", "a2-speedup-vs-buses.png"))
    reg("analysis/a2-speedup-vs-buses.png", "Speedup is 2.4-7x up to ~1,400 buses and falls through parity above ~2,000 buses; all 5 losses labelled or in strip.", "losses: " + "; ".join(f"{r.case} {1 / r.ratio:.2f}x slower" for r in d[d.cls == 'loss'].itertuples()) + "; case2848rte no GridSim result")


BANDS = [("4-30 buses", 0, 30), ("31-100", 31, 100), ("101-1,000", 101, 1000), ("1,001-2,000", 1001, 2000), ("2,001-3,400", 2001, 4000)]
CATC = [("win", BLUE, "GridSim faster (>10%)"), ("tie", LBLUE, "Within 10%"), ("loss", ORANGE, "GridSim slower or no result"), ("nobody", GRAYF, "No solver passed")]


def a_bands():
    title = "Wins, ties and losses by grid size band"
    sub = "Number of cases per size band, by outcome (sparse vs fastest other accurate solver, 10% tie band; box 2). The two cases only GridSim solved are counted as wins."
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
    reg("analysis/a3-outcomes-by-size-band.png", "Every case up to 1,000 buses that anyone solved is a win; the losses are all above 2,000 buses (win/tie/loss/nobody per band).", "; ".join(parts))


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
    sub = ("Cells show a solver's mean time divided by the fastest time that passed the gate on that case (box 2). Light = fastest, dark = slower, capped at 100x. "
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
    sub = "Every case and solver (box 2) under the accuracy gate. Filled blue = passed (10/10 converged, residual <= 1e-8 pu). Grey dash = ran but residual above the gate. Cross = no result (did not converge / no output). Cases ordered by size."
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
    reg("analysis/a5-accuracy-matrix.png", "GridSim sparse passes 61 of 67 cases, more than any other solver; the six it does not pass are 5 nobody-solved plus case2848rte.", "passes of 67: " + ", ".join(f"{NAME[p]} {npass[p]}" for p in cols))


def a_boxes():
    title = "Box 1 vs box 2: per-solver time ratio"
    sub = "Each dot is one case: box 1 (EPYC 7452, Zen 2) mean time divided by box 2 (EPYC 7763, Zen 3) mean time, for cases where the solver passed the gate on both boxes and took more than 5 ms. Above 1x = box 1 slower. Bar = median."
    c = Canvas(1600, 900, title, sub, FOOTBOTH, tsz=26, ssz=13)
    ax = c.axes(220, c.top + 20, 1500, c.bot - 50)
    ps = ["sparse", "dense", "matpower", "pandapower", "powsybl", "powermodels", "veragrid", "pypsa"]
    meds = {}
    rng = np.random.default_rng(3)
    for i, p in enumerate(ps):
        vals = []
        for r in T2.itertuples():
            r1 = T1[T1.case == r.case]
            if r1.empty: continue
            a, b = r.ms.get(p), r1.iloc[0].ms.get(p)
            if a and b and a > 5: vals.append(b / a)
        if len(vals) < 3: continue
        meds[p] = (np.median(vals), len(vals), min(vals), max(vals))
        col = SLOT.get(p, BLUE)
        ax.scatter(vals, i + rng.uniform(-0.15, 0.15, len(vals)), s=36, color=col, edgecolor=SURF, linewidth=1, zorder=4, alpha=0.9)
        ax.plot([meds[p][0]] * 2, [i - 0.28, i + 0.28], color=INK, lw=2, zorder=5)
        ax.text(1.74, i, f"median {meds[p][0]:.2f}x  (n={len(vals)})", va="center", fontsize=10, color=INK2)
    ax.axvline(1, color=INK2, lw=1.2, zorder=2)
    ps = [p for p in ps if p in meds]
    ax.set_ylim(len(ps) - 0.5, -0.5); ax.set_yticks(range(len(ps))); ax.set_yticklabels([NAME[p] for p in ps], fontsize=11, color=INK)
    ax.set_xlim(0.6, 2.3); style(ax, "x"); ax.set_xlabel("Time on box 1 / time on box 2")
    ax.set_xticks([0.6, 0.8, 1, 1.2, 1.4, 1.6]); ax.set_xticklabels([f"{v:g}x" for v in [0.6, 0.8, 1, 1.2, 1.4, 1.6]])
    c.save(out("analysis", "a6-box1-vs-box2-scaling.png"))
    reg("analysis/a6-box1-vs-box2-scaling.png", "Results reproduce across two different CPUs: box 2 is roughly 0-20% faster per solver, and outcomes per case are identical (54/3/5/5 on both).",
        "filter: passed on both boxes and box-2 time > 5 ms (SUMMARY.md quotes powsybl 1.19x and PowerModels 1.06x with a different, unstated filter; sparse/dense/MATPOWER/pandapower match it); medians box1/box2: " + ", ".join(f"{NAME[p]} {m[0]:.2f}x (n={m[1]}, range {m[2]:.2f}-{m[3]:.2f})" for p, m in meds.items()))


def a_dense_sparse():
    title = "GridSim sparse vs dense path"
    sub = "Dense solve time divided by sparse solve time per case, for cases where both passed the gate (box 2). Above 1x the sparse path is faster."
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
    title = "Cases of 2,000+ buses: where GridSim falls behind"
    sub = ("GridSim sparse vs fastest other accurate solver on all 12 cases with 2,000+ buses (box 2). Bars start at zero. Orange labels mark the performance-fix targets: "
           "cases where GridSim is more than 10% slower or has no result. Some cases are intentionally hard at flat start.")
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
    reg("analysis/a8-large-cases.png", "Perf-fix target list: 5 of 12 cases with 2,000+ buses are losses (one is non-convergence), 3 are ties, 2 are only-GridSim, 1 nobody, 1 small win.",
        "; ".join(f"{r.case} ({r.buses}): " + (f"sparse {r.spv:.4g} ms vs {r.best} {r.best_ms:.4g} ms = {1 / r.ratio:.2f}x slower" if r.cls == "loss" else f"sparse no result, {r.best} {r.best_ms:.4g} ms") for r in tg.itertuples()))


def write_index():
    L = ["# GridSim benchmark chart pack (2026-11-18)", "",
         "Source: box 2 (hyperstack-2-canada, EPYC 7763, 28 vCPU) `suite-full-20261117T2356Z/comparison.csv`, box 1 (hyperstack-1-norway, EPYC 7452, 4 vCPU) `suite-full-20261117T2159Z/comparison.csv` for chart a6 only. "
         "Generated by `charts.py` (python charts.py). Method: one core, Release, tiered JIT off, mean of 10 timed solves, solve time only, flat start, 67 MATPOWER 8.1 cases. "
         "Accuracy gate: status complete, converged 10, max residual <= 1e-8 pu. Win/tie/loss: sparse vs fastest accurate other solver, 10% tie band (matches summarize.py: 54 / 3 / 5 / 5 on both boxes).", "",
         "Every PNG carries a footer with hardware, method and data source. Headline charts 1600x900; `-square` variants 1080x1080.", "",
         "## Headline charts", ""]
    for f, cl, n in REG:
        if f.startswith("headline"): L += [f"- `{f}`", f"  - Claim: {cl}", f"  - Numbers: {n}", ""]
    L += ["## Analysis charts", ""]
    for f, cl, n in REG:
        if f.startswith("analysis"): L += [f"- `{f}`", f"  - Claim: {cl}", f"  - Numbers: {n}", ""]
    L += ["## Notes on honesty", "",
          "- Headline speedup, time-saved and capacity charts state their range in the subtitle (<=1,400 buses, or the four named example cases). The scorecard and analysis charts show all losses.",
          "- Losses (box 2): " + "; ".join(f"{r.case} ({'GridSim no result, ' + r.best + ' passed' if r.cls == 'loss_nr' else f'{1 / r.ratio:.2f}x slower than {r.best}'})" for r in LOSSES.itertuples()) + ".",
          "- Ties: " + ", ".join(T2[T2.cls == 'tie'].case) + ". Nobody solved: " + ", ".join(T2[T2.cls == 'nobody'].case) + " (case_RTS_GMLC was not materialised for any solver: unsupported dcline field).",
          "- Per-solver palette: skill reference slots in fixed order (GridSim blue first); adjacent-pair validation passes (validate_palette.py, light mode); the all-pairs check fails beyond 3 slots, so marker shapes, a legend and fixed order carry identity on the multi-solver charts."]
    open(os.path.join(HERE, "INDEX.md"), "w", encoding="utf-8").write("\n".join(L) + "\n")


if __name__ == "__main__":
    h_speedup(1600, 900, "h1-speedup.png"); h_speedup(1080, 1080, "h1-speedup-square.png")
    h_scorecard(1600, 900, "h2-scorecard.png"); h_scorecard(1080, 1080, "h2-scorecard-square.png")
    h_saved(1600, 900, "h3-time-saved-30000-solves.png"); h_saved(1080, 1080, "h3-time-saved-30000-solves-square.png")
    h_capacity(1600, 900, "h4-solves-per-day.png")
    h_only(1600, 900, "h5-only-solver-case2736sp-case2746wop.png")
    a_time_vs_buses(); a_speedup_vs_buses(); a_bands(); a_heatmap(); a_matrix(); a_boxes(); a_dense_sparse(); a_large()
    write_index()
    print("done", CNT2, CNT1)
