"""GridSim benchmark journey pack: laptop -> box 1 -> box 2 -> regression fixes.
Reads (read-only) the four comparison.csv files; writes PNGs next to this script and INDEX.md.
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

# ---- data ----------------------------------------------------------------------------
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




# ---- stages ----------------------------------------------------------------------------
LAP = r"W:\Code\GridSim-Benchmarks\suite-rehearsal-20261115-165137\comparison.csv"
LAP_ATT = r"W:\Code\GridSim-Benchmarks\suite-rehearsal-20261115-165137\attempt-results.csv"
B1 = r"W:\Benchmarks\hyperstack-1-norway-2026-11-17\suite-full-20261117T2159Z\comparison.csv"
B2 = r"W:\Benchmarks\hyperstack-2-canada-2026-11-17\suite-full-20261117T2356Z\comparison.csv"
HS3 = r"W:\Benchmarks\hyperstack-3-canada-2026-11-18\bench-runs"
FULLCAND = HS3 + r"\full-cand-20261118T0918Z\comparison.csv"
FOCUS_OTH = HS3 + r"\focused-others-20261118T1033Z\comparison.csv"


def build_fixed():
    """GridSim paths from the hyperstack-3 full-cand run; other solvers from the same-box focused-others round (16 cases)
    and, for the other cases, from the hyperstack-2 (box 2) run, because the hyperstack-3 full runs measured GridSim only."""
    fc = pd.read_csv(FULLCAND); fo = pd.read_csv(FOCUS_OTH); old = pd.read_csv(B2)
    for d in (fc, fo, old): d["case"] = d["case"].str.replace(r"\.m$", "", regex=True)
    gs = fc[fc.path.isin(["sparse", "dense"])]
    oth = old[~old.path.isin(["sparse", "dense"]) & ~old.case.isin(fo.case)]
    foc = fo[~fo.path.isin(["sparse", "dense"])]
    return pd.concat([gs, foc, oth], ignore_index=True)


FIX = build_fixed()
OTHERS = ["matpower", "pandapower", "powsybl", "powermodels", "veragrid", "pypsa", "andes"]
TIE = 1.10
GREY = "#c3c2b7"


def table(d):
    rows = []
    for c, g in d.groupby("case", sort=False):
        b = int(g.buses.iloc[0])
        ms = {r.path: r.mean_ms for r in g.itertuples() if r.ok}
        sp = ms.get("sparse")
        ex = {k: v for k, v in ms.items() if k in OTHERS}
        be = min(ex, key=ex.get) if ex else None
        if sp is None and be is None: cls = "nobody"
        elif sp is None: cls = "loss"
        elif be is None: cls = "win"
        else:
            r = ex[be] / sp
            cls = "win" if r > TIE else ("loss" if r < 1 / TIE else "tie")
        rows.append(dict(case=c, buses=b, spv=sp, best=be, best_ms=ex[be] if be else None, cls=cls,
                         ratio=(ex[be] / sp) if (sp and be) else None))
    return pd.DataFrame(rows).set_index("case", drop=False)


STAGES = [
    dict(key="L", name="Laptop", long="Busy laptop (rehearsal, 15 Nov, partial)", d=load(LAP)),
    dict(key="B1", name="Box 1", long="Box 1: EPYC 7452 (Zen 2), 4 vCPU", d=load(B1)),
    dict(key="B2", name="Box 2", long="Box 2: EPYC 7763 (Zen 3), 28 vCPU", d=load(B2)),
    dict(key="F", name="Fixes", long="Fixes: EPYC 7763 (Zen 3), 28 vCPU, new code", d=load(FIX)),
]
for s in STAGES: s["t"] = table(s["d"]); s["n"] = len(s["t"])
S = {s["key"]: s for s in STAGES}
def counts(t):
    v = t.cls.value_counts().to_dict()
    return [v.get(k, 0) for k in ("win", "tie", "loss", "nobody")]
for s in STAGES: print(s["key"], s["n"], counts(s["t"]))

FOOT_L = ("Laptop = busy-laptop rehearsal of 15 Nov: PARTIAL (55 of 67 cases in its comparison, two large RTE cases omitted, run stopped during the 2,000-bus ACTIVSg case) "
          "and run on a machine in normal use, so laptop times are noisy and not as precise as the cloud runs. ")
FOOT_C = ("Cloud boxes: box 1 = AMD EPYC 7452 (Zen 2) KVM VM, 4 vCPU; box 2 and fixes = AMD EPYC 7763 (Zen 3) KVM VM, 28 vCPU; one solver path at a time pinned to one core, Release build, tiered JIT off. "
          "Method: MATPOWER 8.1 cases, flat start, mean of 10 timed solves, solve time only. Accuracy gate: status complete, 10/10 converged and max residual <= 1e-8 pu; a time counts only if it passes. "
          "GridSim = sparse path; 'fastest other' = fastest accurate non-GridSim solver; 10% tie band. 'Nobody' = no solver passed (not a GridSim failure). "
          "Fixes stage: GridSim paths from hyperstack-3 full-cand (branch perf-rc3: minimum-degree LU ordering, Vm floor 0.01); other solvers' times from the same-box hyperstack-3 focused-others round (16 cases) and otherwise from the box 2 run (same code-independent solvers, same CPU class), because the hyperstack-3 full runs measured GridSim only.")
FOOT_ALL = FOOT_L + FOOT_C
FOOT_NOLAP = FOOT_C


def nm(a): return S[a]["name"]
CHANGE = {
    ("L", "B1"): ("HARDWARE CHANGE ONLY (same GridSim code)", "busy laptop to dedicated 4 vCPU cloud VM"),
    ("B1", "B2"): ("HARDWARE CHANGE ONLY (same GridSim code)", "EPYC 7452 Zen 2, 4 vCPU to EPYC 7763 Zen 3, 28 vCPU (one pinned core used)"),
    ("B2", "F"): ("CODE CHANGE on the same hardware class", "GridSim RC-3 to branch perf-rc3 (minimum-degree LU ordering, Vm floor 0.01); EPYC 7763 Zen 3, 28 vCPU"),
}
STEPS = [("L", "B1", "a"), ("B1", "B2", "b"), ("B2", "F", "c")]


def common(a, b):
    ta, tb = S[a]["t"], S[b]["t"]
    cs = [c for c in tb.index if c in ta.index]
    return ta.loc[cs], tb.loc[cs]


def ft(a, b): return FOOT_ALL if "L" in (a, b) else FOOT_NOLAP


def step_dots(a, b, letter):
    ta, tb = common(a, b)
    kind, what = CHANGE[(a, b)]
    both = ta.spv.notna() & tb.spv.notna()
    xa, xb = ta[both], tb[both]
    n = len(xa)
    rat = xa.spv / xb.spv
    title = f"GridSim sparse time per case: {nm(a)} to {nm(b)}"
    sub = (f"{kind}: {what}. Each pair is one case GridSim solved on both sides ({n} of {len(ta)} common cases), mean sparse solve time, log scale. "
           f"Median before/after {np.median(rat):.2f}x (above 1 = faster after); range {rat.min():.2f}x to {rat.max():.2f}x.")
    c = Canvas(1600, 900, title, sub, ft(a, b), tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1500, c.bot - 50)
    for xx, y0, y1 in zip(xa.buses, xa.spv, xb.spv):
        ax.plot([xx, xx], [y0, y1], color=GRID, lw=1.2, zorder=2)
    ax.scatter(xa.buses, xa.spv, s=42, color=LBLUE, edgecolor=SURF, linewidth=1, zorder=3, label=S[a]["long"])
    ax.scatter(xb.buses, xb.spv, s=42, color=BLUE, edgecolor=SURF, linewidth=1, zorder=4, label=S[b]["long"])
    ax.set_xscale("log"); ax.set_yscale("log"); style(ax, "both")
    ax.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); logfmt(ax, "x"); logfmt(ax, "y")
    ax.set_xlabel("Grid size (buses, log scale)"); ax.set_ylabel("GridSim sparse mean solve time (ms, log scale)")
    ax.legend(loc="lower right", frameon=False, fontsize=11, labelcolor=INK2, title="Run", title_fontsize=10.5, alignment="left")
    order = rat.sort_values()
    f = f"{letter}1-sparse-ms-{a}-to-{b}.png".lower()
    c.save(out("", f))
    reg(f, f"{nm(a)} to {nm(b)}: {kind.lower()}; GridSim sparse ms per common case.",
        f"{n} cases; median before/after {np.median(rat):.3f}x; min {rat.min():.3f}x ({order.index[0]}), max {rat.max():.3f}x ({order.index[-1]})")


def step_speed(a, b, letter):
    ta, tb = common(a, b)
    kind, what = CHANGE[(a, b)]
    n = len(ta)
    ra, rb = ta.ratio.dropna(), tb.ratio.dropna()
    cs = [c for c in ta.index if c in ra.index and c in rb.index]
    title = f"Speedup over the fastest other solver: {nm(a)} to {nm(b)}"
    sub = (f"{kind}: {what}. Each pair is one common case: fastest accurate other solver's ms divided by GridSim sparse ms; above 1x GridSim is faster. "
           f"{len(cs)} of {n} common cases have a GridSim and an other-solver result on both sides. Median {nm(a)} {np.median(ra[cs]):.2f}x, {nm(b)} {np.median(rb[cs]):.2f}x.")
    c = Canvas(1600, 900, title, sub, ft(a, b), tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1500, c.bot - 50)
    ax.axhline(1, color=INK2, lw=1.2, zorder=2)
    xs = ta.loc[cs].buses
    for xx, y0, y1 in zip(xs, ra[cs], rb[cs]): ax.plot([xx, xx], [y0, y1], color=GRID, lw=1.2, zorder=2)
    ax.scatter(xs, ra[cs], s=42, color=LBLUE, edgecolor=SURF, linewidth=1, zorder=3, label=S[a]["long"])
    ax.scatter(xs, rb[cs], s=42, color=BLUE, edgecolor=SURF, linewidth=1, zorder=4, label=S[b]["long"])
    ax.set_xscale("log"); ax.set_yscale("log"); style(ax, "both")
    ax.set_xticks([4, 10, 30, 100, 300, 1000, 3000]); logfmt(ax, "x")
    ax.set_yticks([0.1, 0.2, 0.5, 1, 2, 5, 10, 20]); ax.set_yticklabels(["0.1x", "0.2x", "0.5x", "1x", "2x", "5x", "10x", "20x"])
    ax.minorticks_off() if False else None
    ax.set_xlabel("Grid size (buses, log scale)"); ax.set_ylabel("Speedup vs fastest other (log scale)")
    ax.legend(loc="lower left", frameon=False, fontsize=11, labelcolor=INK2, title="Run", title_fontsize=10.5)
    f = f"{letter}2-speedup-{a}-to-{b}.png".lower()
    c.save(out("", f))
    la = [x for x in cs if ra[x] < 1 / TIE]; lb = [x for x in cs if rb[x] < 1 / TIE]
    reg(f, f"{nm(a)} to {nm(b)}: speedup vs fastest other solver on common cases.",
        f"{len(cs)} cases; median {np.median(ra[cs]):.2f}x to {np.median(rb[cs]):.2f}x; max {ra[cs].max():.2f}x to {rb[cs].max():.2f}x; min {ra[cs].min():.2f}x to {rb[cs].min():.2f}x; cases >10% behind {len(la)} to {len(lb)}")


def step_score(a, b, letter):
    ta, tb = common(a, b)
    kind, what = CHANGE[(a, b)]
    ca, cb = counts(ta), counts(tb)
    title = f"Win / tie / loss scorecard: {nm(a)} to {nm(b)}"
    sub = (f"{kind}: {what}. The same {len(ta)} common cases on both sides. "
           "Win = GridSim sparse more than 10% faster than the fastest accurate other solver; loss = more than 10% slower, or no GridSim result while another solver passed; nobody = no solver passed.")
    c = Canvas(1600, 780, title, sub, ft(a, b), tsz=26, ssz=13)
    ax = c.axes(470, c.top + 46, 1520, c.bot - 70)
    cols = [(BLUE, "Win"), (LBLUE, "Tie"), (ORANGE, "Loss"), (GREY, "Nobody solved")]
    for i, cn in enumerate((ca, cb)):
        left = 0
        for (col, lab), v in zip(cols, cn):
            if v:
                ax.barh(i, v - 0.15, left=left, height=0.5, color=col, zorder=3)
                ax.text(left + (v - 0.15) / 2, i, str(v), ha="center", va="center", fontsize=14, fontweight="bold", color="white" if col in (BLUE, ORANGE) else INK)
            left += v
    ax.set_ylim(1.6, -0.6); ax.set_yticks([0, 1]); ax.set_yticklabels([S[a]["long"].replace(": ", ":\n", 1).replace(" (", "\n(", 1) if ":" not in S[a]["long"] else S[a]["long"].replace(": ", ":\n", 1),
                                                                    S[b]["long"].replace(": ", ":\n", 1)], fontsize=10.5, color=INK)
    style(ax, "x"); ax.set_xlim(0, len(ta)); ax.set_xlabel("Number of common cases")
    c.fig.legend([Rectangle((0, 0), 1, 1, color=col) for col, _ in cols], [l for _, l in cols], loc="upper center", ncol=4, frameon=False, fontsize=11, labelcolor=INK2,
                 bbox_to_anchor=(0.5, 1 - (c.top - 4) / 780))
    f = f"{letter}3-scorecard-{a}-to-{b}.png".lower()
    c.save(out("", f))
    reg(f, f"{nm(a)} to {nm(b)}: win/tie/loss/nobody on the same common cases.",
        f"{len(ta)} common cases; {nm(a)} {'/'.join(map(str, ca))}; {nm(b)} {'/'.join(map(str, cb))} (win/tie/loss/nobody)")


ANCHORS = ["case118", "case1354pegase", "case_ACTIVSg2000", "case3375wp"]


def overview():
    title = "The journey: win / tie / loss across four stages"
    sub = ("Laptop (partial, busy machine), box 1 and box 2 (same GridSim code, different hardware), then the regression fixes (new code, box 2 class hardware). "
           "Each bar is that stage's whole comparison file; the laptop covers only 55 of the 67 cases, so its bar is shorter and is not like-for-like.")
    c = Canvas(1600, 800, title, sub, FOOT_ALL, tsz=26, ssz=13)
    ax = c.axes(300, c.top + 50, 1500, c.bot - 70)
    cols = [(BLUE, "Win"), (LBLUE, "Tie"), (ORANGE, "Loss"), (GREY, "Nobody solved")]
    for i, s in enumerate(STAGES):
        cn = counts(s["t"]); left = 0
        for (col, lab), v in zip(cols, cn):
            if v:
                ax.barh(i, v - 0.2, left=left, height=0.55, color=col, zorder=3)
                ax.text(left + (v - 0.2) / 2, i, str(v), ha="center", va="center", fontsize=13, fontweight="bold", color="white" if col in (BLUE, ORANGE) else INK)
            left += v
        ax.text(left + 0.8, i, f"{s['n']} cases", va="center", fontsize=10.5, color=INK2)
    kinds = {"L": "partial, busy laptop", "B1": "hardware change", "B2": "hardware change", "F": "CODE change"}
    lab = [f"{s['name']}\n{kinds[s['key']]}" for s in STAGES]
    ax.set_ylim(len(STAGES) - 0.4, -0.6); ax.set_yticks(range(4)); ax.set_yticklabels(lab, fontsize=11, color=INK)
    style(ax, "x"); ax.set_xlim(0, 75); ax.set_xlabel("Number of cases")
    c.fig.legend([Rectangle((0, 0), 1, 1, color=col) for col, _ in cols], [l for _, l in cols], loc="upper center", ncol=4, frameon=False, fontsize=11, labelcolor=INK2,
                 bbox_to_anchor=(0.5, 1 - (c.top - 6) / 800))
    c.save(out("", "j1-scorecard-across-stages.png"))
    reg("j1-scorecard-across-stages.png", "Scorecard (win/tie/loss/nobody) per stage.",
        "; ".join(f"{s['name']} ({s['n']} cases) {'/'.join(map(str, counts(s['t'])))}" for s in STAGES))

    title = "GridSim sparse time on anchor cases, across the stages"
    sub = ("Mean sparse solve time (ms, log scale) where the case is present and GridSim passed the gate at that stage. Laptop to box 1 to box 2 are hardware changes with the same code; "
           "the last step (shaded) is the code change. Hollow laptop marker for the 2,000-bus ACTIVSg case: saved attempts of the interrupted laptop run (sparse path only); case3375wp has no laptop point (omitted).")
    c = Canvas(1600, 900, title, sub, FOOT_ALL, tsz=26, ssz=13)
    ax = c.axes(110, c.top + 10, 1050, c.bot - 70)
    xs = [0, 1, 2, 3]
    ax.axvspan(2.5, 3.5, color="#fdeee6", zorder=0)
    ax.text(3.0, 0.985, "code change", ha="center", va="top", fontsize=10, color=ORANGE, fontweight="bold", transform=ax.get_xaxis_transform())
    lap_att = pd.read_csv(LAP_ATT); g = lap_att[(lap_att.case == "case_ACTIVSg2000.m") & (lap_att.path == "sparse")]
    act_lap = float(g.elapsedMs.mean())
    cols_a = {"case118": "#2a78d6", "case1354pegase": "#1baf7a", "case_ACTIVSg2000": "#6250d6", "case3375wp": "#eda100"}
    mk = {"case118": "o", "case1354pegase": "s", "case_ACTIVSg2000": "^", "case3375wp": "D"}
    vals_reg = {}
    for cs in ANCHORS:
        ys = []
        for s in STAGES:
            v = s["t"].spv.get(cs) if cs in s["t"].index else np.nan
            ys.append(np.nan if (v is None or pd.isna(v)) else float(v))
        vals_reg[cs] = list(ys)
        bus = int(S["B2"]["t"].buses[cs])
        ax.plot(xs, ys, color=cols_a[cs], lw=1.6, marker=mk[cs], ms=8, mec=SURF, mew=1.2, label=f"{cs} ({bus:,} buses)", zorder=4)
        for x, v in zip(xs, ys):
            if not np.isnan(v):
                below = cs == "case_ACTIVSg2000" and x in (1, 2)
                if cs == "case_ACTIVSg2000" and x == 3:
                    ax.text(x + 0.08, v, f"{v:,.4g} ms", ha="left", va="center", fontsize=9.5, color=INK)
                else:
                    ax.text(x, v / 1.18 if below else v * 1.18, f"{v:,.4g} ms", ha="center", va="top" if below else "bottom", fontsize=9.5, color=INK)
    ax.plot([0], [act_lap], marker="^", ms=8, mfc=SURF, mec=cols_a["case_ACTIVSg2000"], mew=1.6, ls="none", zorder=5)
    ax.plot([0, 1], [act_lap, vals_reg["case_ACTIVSg2000"][1]], color=cols_a["case_ACTIVSg2000"], lw=1.2, ls=(0, (3, 3)), zorder=3)
    ax.text(0, act_lap * 1.18, f"{act_lap:,.4g} ms", ha="center", va="bottom", fontsize=9.5, color=INK)
    ax.set_yscale("log"); style(ax, "y"); logfmt(ax, "y")
    sl = {"L": "busy laptop, partial", "B1": "EPYC 7452, 4 vCPU", "B2": "EPYC 7763, 28 vCPU", "F": "EPYC 7763, new code"}
    ax.set_xticks(xs); ax.set_xticklabels([f"{s['name']}\n{sl[s['key']]}" for s in STAGES], fontsize=10.5, color=INK)
    ax.set_xlim(-0.4, 3.5); ax.set_ylim(top=ax.get_ylim()[1] * 1.5)
    ax.set_ylabel("GridSim sparse mean solve time (ms, log scale)")
    ax.legend(loc="center left", bbox_to_anchor=(1.02, 0.5), frameon=False, fontsize=10.5, labelcolor=INK2, title="Anchor case", title_fontsize=10.5)
    c.save(out("", "j2-anchor-cases-across-stages.png"))
    reg("j2-anchor-cases-across-stages.png", "GridSim sparse ms on anchor cases across the four stages.",
        "; ".join(f"{cs}: " + " -> ".join("n/a" if np.isnan(v) else f"{v:.4g}" for v in vals_reg[cs]) for cs in ANCHORS) + f"; laptop ACTIVSg2000 (interrupted-run attempts) {act_lap:.4g} ms")


def write_index():
    L = ["# GridSim benchmark journey pack (2026-11-18)", "",
         "Back-to-back comparisons across four stages: busy laptop rehearsal (15 Nov, partial) -> box 1 -> box 2 -> regression fixes. Generated by `charts.py` (python charts.py), read-only on the inputs.", "",
         "Inputs:",
         f"- Laptop: `{LAP}` (the other rehearsal folder, suite-rehearsal-20261115-164830, has an empty comparison.csv so it was not used). `attempt-results.csv` of the same folder is used only for the 2,000-bus ACTIVSg anchor point.",
         f"- Box 1: `{B1}`", f"- Box 2: `{B2}`", f"- Fixes: GridSim paths from `{FULLCAND}`; other solvers from `{FOCUS_OTH}` (16 focused cases, same box) and, for all other cases, from the box 2 file above (the hyperstack-3 full runs contain GridSim dense/sparse only). In step c the other-solver times are therefore the box 2 values for 51 cases, so the change in speedup there is due to GridSim alone.", "",
         "What changed at each step: (a) laptop to box 1 and (b) box 1 to box 2 are hardware/environment changes with the same GridSim code; (c) box 2 to fixes is a code change (minimum-degree LU ordering + Vm floor 0.01, branch perf-rc3) on the same hardware class.", "",
         "Accuracy gate: status complete, converged 10, max residual <= 1e-8 pu. Win/tie/loss: sparse vs fastest accurate other solver, 10% tie band. Step charts use only cases present in both comparison files.", "",
         "## Charts", ""]
    for f, cl, n in REG: L += [f"- `{f}`", f"  - Claim: {cl}", f"  - Numbers: {n}", ""]
    L += ["## Caveats", "",
          "- The laptop run was partial (55 cases in the comparison, 2 large RTE cases omitted, stopped during case_ACTIVSg2000) and on a busy machine; its times are noisy. Its scorecard counts case_RTS_GMLC (no result for any solver) as nobody.",
          "- Step charts compare common cases only; absolute differences in steps a and b mix hardware, load and noise, and in step c reflect the code change.",
          "- Steps a and b assume the same GridSim code (as stated in the brief); the laptop build was not independently verified for this pack."]
    open(os.path.join(HERE, "INDEX.md"), "w", encoding="utf-8").write("\n".join(L) + "\n")


if __name__ == "__main__":
    overview()
    for a, b, l in STEPS:
        step_dots(a, b, l); step_speed(a, b, l); step_score(a, b, l)
    write_index()
    print("done")
