"""Charts for the RC4 Task 6 large-case run. Usage: python make-charts.py <folder>
Reads data/*.csv, writes charts/*.png."""
import csv, collections, sys, os, textwrap, statistics
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import FixedLocator, FuncFormatter, NullFormatter

base = sys.argv[1]
data, out = os.path.join(base, "data"), os.path.join(base, "charts")
os.makedirs(out, exist_ok=True)

INK, INK2, GRID, SURF = "#1f1f1e", "#5f5e58", "#e6e5e0", "#fcfcfb"
BLUE, ORANGE, AQUA, YELLOW, GREY = "#2a78d6", "#eb6834", "#1baf7a", "#d9a21b", "#b9b8b1"
RED = "#d03b3b"
plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 10, "axes.edgecolor": GRID,
                     "axes.labelcolor": INK2, "xtick.color": INK2, "ytick.color": INK2,
                     "figure.facecolor": SURF, "axes.facecolor": SURF})
FOOT = ("Build box: 4 vCPU / 20 GB, three tiers run concurrently, each pinned to its own CPU (timings carry "
        "memory-bandwidth contention). GridSim 7b648759, Release, tiered JIT off, flat start, tol 1e-8, 50 it, Q limits off. "
        "Solve time = mean of 10 attempts. FINAL 2026-11-19: standard 162/162, large 40/47 (7 not run: box out of memory at 25k buses), models 36/36.")
OTHERS = ["matpower", "pandapower", "pypsa", "veragrid", "powsybl", "andes", "powermodels"]
GS_ONLY = {"case16am.m", "case2868rte.m"}


def load(tier):
    by = collections.defaultdict(dict)
    for r in csv.DictReader(open(os.path.join(data, f"{tier}-comparison.csv"))):
        by[r["case"]][r["path"]] = r
    return by


def ok(r):
    try:
        return r["status"] == "complete" and r["converged"] == "10" and float(r["max_original_residual_pu"]) <= 1e-8
    except (ValueError, KeyError, TypeError):
        return False


def style(ax):
    ax.grid(True, which="major", color=GRID, lw=0.8); ax.set_axisbelow(True)
    for s in ("top", "right"): ax.spines[s].set_visible(False)


def logx(ax, xs):
    ticks = [t for t in (2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000)
             if min(xs) * 0.8 <= t <= max(xs) * 1.25]
    ax.xaxis.set_major_locator(FixedLocator(ticks)); ax.xaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:,.0f}"))
    ax.xaxis.set_minor_formatter(NullFormatter())


def foot(fig, extra=""):
    fig.text(0.01, 0.01, textwrap.fill(FOOT + extra, 160), fontsize=7, color=INK2)


tiers = {"standard": load("standard"), "large": load("large")}
totals = {"standard": 162, "large": 47}

# ---- outcome counts: taken from SUMMARY.md (the CSV does not record parser refusals) ----
import re
summ = open(os.path.join(data, "SUMMARY.md")).read()
MAP = {"pass": "Pass", "no solver converged (flat start)": "No solver converged",
       "parser refused (gen PMIN>PMAX or QMIN>QMAX)": "Parser refused (PMIN>PMAX)",
       "GRIDSIM-ONLY NON-CONVERGENCE": "Fails only in GridSim",
       "not materialised (Case has unsupported electrical field dcline)": "Unsupported field (dcline)"}
outcomes = {}
for t in ("standard", "large"):
    sec = summ.split(f"## {t} tier")[1].split("All paths")[0]
    c = collections.Counter()
    for m in re.finditer(r"^\| (.+?) \| (\d+) \|$", sec, re.M):
        if m.group(1) in MAP: c[MAP[m.group(1)]] += int(m.group(2))
    c["Not run (box out of memory)"] = totals[t] - sum(c.values())
    outcomes[t] = c
    print(t, dict(c))

# ---- pairs: GridSim sparse vs fastest other, both clean ----
pts = []  # buses, case, gs_ms, other_ms, other_name, tier
per_tool = collections.defaultdict(list)  # tool -> [(buses, ratio)]
for t, by in tiers.items():
    for case, p in by.items():
        sp = p.get("sparse")
        if not (sp and ok(sp)): continue
        gs = float(sp["mean_ms"]); b = int(sp["buses"])
        ext = [p[o] for o in OTHERS if o in p and ok(p[o])]
        for e in ext: per_tool[e["path"]].append((b, float(e["mean_ms"]) / gs))
        if ext:
            e = min(ext, key=lambda r: float(r["mean_ms"]))
            pts.append((b, case, gs, float(e["mean_ms"]), e["path"], t))
pts.sort()
x = [p[0] for p in pts]

# Chart 1: outcomes per tier
order = ["Pass", "No solver converged", "Parser refused (PMIN>PMAX)", "Fails only in GridSim",
         "Unsupported field (dcline)", "Not run (box out of memory)"]
cols = {"Pass": BLUE, "No solver converged": GREY, "Parser refused (PMIN>PMAX)": YELLOW,
        "Fails only in GridSim": RED, "Unsupported field (dcline)": AQUA, "Not run (box out of memory)": "#e6e5e0"}
fig, ax = plt.subplots(figsize=(9, 3.6), dpi=150)
labels = ["Standard (<= 5,000 buses)", "Large (> 5,000 buses)"]
for i, t in enumerate(["standard", "large"]):
    left = 0
    for k in order:
        v = outcomes[t][k]
        if not v: continue
        ax.barh(i, v, left=left, color=cols[k], edgecolor=SURF, linewidth=2, height=0.55, label=k if i == 0 or k not in outcomes["standard"] else None)
        if v >= 3: ax.text(left + v / 2, i, str(v), ha="center", va="center", fontsize=9, color=INK if k in ("Not run (box out of memory)", "Parser refused (PMIN>PMAX)", "No solver converged") else "white")
        left += v
ax.set_yticks([0, 1]); ax.set_yticklabels(labels); ax.invert_yaxis()
ax.set_xlabel("Cases (GridSim CPU sparse)")
ax.grid(True, axis="x", color=GRID, lw=0.8); ax.set_axisbelow(True)
for s in ("top", "right"): ax.spines[s].set_visible(False)
h, l = ax.get_legend_handles_labels(); seen = {}
for hh, ll in zip(h, l): seen.setdefault(ll, hh)
ax.legend([seen[k] for k in order if k in seen], [k for k in order if k in seen], frameon=False, ncol=3, fontsize=8,
          loc="upper center", bbox_to_anchor=(0.45, -0.28), labelcolor=INK)
ax.set_title("MATPOWER/PGLib cases: what happened to each", loc="left", color=INK, fontsize=13, pad=10)
foot(fig, " 'No solver converged' = none of the 8 tools converged from flat start (mostly OPF-stressed PGLib cases).")
fig.tight_layout(rect=(0, 0.12, 1, 1)); fig.savefig(os.path.join(out, "01-outcomes.png")); plt.close(fig)

# Chart 2: solve time vs buses
fig, ax = plt.subplots(figsize=(9, 5.2), dpi=150)
ax.plot(x, [p[3] for p in pts], color=ORANGE, lw=0, marker="o", ms=6, mec=SURF, mew=1.5, label="Fastest other solver (per case)")
ax.plot(x, [p[2] for p in pts], color=BLUE, lw=0, marker="o", ms=6, mec=SURF, mew=1.5, label="GridSim sparse")
ax.axvline(5000, color=INK2, lw=0.8, ls=(0, (4, 3))); ax.text(5200, ax.get_ylim()[0] * 1.5 if ax.get_ylim()[0] > 0 else 0.1, "large tier", color=INK2, fontsize=8)
ax.set_xscale("log"); ax.set_yscale("log"); logx(ax, x)
ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:g}"))
ax.set_xlabel("Buses (log)"); ax.set_ylabel("Mean solve time, ms (log)"); style(ax)
ax.legend(frameon=False, loc="upper left", labelcolor=INK)
ax.set_title(f"AC power flow solve time by network size ({len(pts)} cases both solved)", loc="left", color=INK, fontsize=13, pad=12)
foot(fig, " Only cases where GridSim and the other solver both met 10/10 and residual <= 1e-8 pu.")
fig.tight_layout(rect=(0, 0.08, 1, 1)); fig.savefig(os.path.join(out, "02-solve-time-vs-buses.png")); plt.close(fig)

# Chart 3: speedup over fastest other
fig, ax = plt.subplots(figsize=(9, 5.2), dpi=150)
r = [p[3] / p[2] for p in pts]
ax.axhline(1, color=INK2, lw=1, ls=(0, (4, 3))); ax.text(x[0], 1.08, "parity", color=INK2, fontsize=8)
ax.axvline(5000, color=INK2, lw=0.8, ls=(0, (4, 3)))
ax.plot(x, r, color=BLUE, lw=0, marker="o", ms=6, mec=SURF, mew=1.5)
ax.set_xscale("log"); ax.set_yscale("log"); logx(ax, x)
ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:g}x"))
ax.set_xlabel("Buses (log)"); ax.set_ylabel("Fastest other / GridSim sparse (log)"); style(ax)
big = [k for k in range(len(pts)) if pts[k][0] > 5000]
if big:
    med = statistics.median(r[k] for k in big)
    ax.annotate(f"> 5k buses: median {med:.2f}x", (x[big[-1]], r[big[-1]]), xytext=(-150, 30), textcoords="offset points", color=INK, fontsize=9,
                arrowprops=dict(arrowstyle="-", color=INK2, lw=0.8))
ax.set_title("GridSim sparse speed relative to the fastest other solver", loc="left", color=INK, fontsize=13, pad=12)
foot(fig, " Above 1x = GridSim faster.")
fig.tight_layout(rect=(0, 0.08, 1, 1)); fig.savefig(os.path.join(out, "03-speedup-vs-buses.png")); plt.close(fig)

# Chart 4: per-tool median ratio, split by tier
fig, ax = plt.subplots(figsize=(9, 4.6), dpi=150)
tools = sorted(per_tool, key=lambda t: -statistics.median(v for _, v in per_tool[t]))
yy = range(len(tools))
for off, (lab, cond, col) in zip((-0.18, 0.18), (("<= 5,000 buses", lambda b: b <= 5000, BLUE), ("> 5,000 buses", lambda b: b > 5000, ORANGE))):
    vals = [statistics.median([v for b, v in per_tool[t] if cond(b)] or [float("nan")]) for t in tools]
    ns = [len([1 for b, _ in per_tool[t] if cond(b)]) for t in tools]
    ax.barh([y + off for y in yy], vals, height=0.34, color=col, edgecolor=SURF, linewidth=2, label=lab)
    for y, v, n in zip(yy, vals, ns):
        if v == v: ax.text(v * 1.08, y + off, f"{v:.1f}x (n={n})", va="center", fontsize=7.5, color=INK)
ax.set_xscale("log"); ax.axvline(1, color=INK2, lw=1, ls=(0, (4, 3)))
ax.xaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:g}x"))
ax.set_yticks(list(yy)); ax.set_yticklabels(tools); ax.invert_yaxis()
ax.set_xlabel("Median of (other tool time / GridSim sparse time), log; > 1x = GridSim faster")
ax.grid(True, axis="x", color=GRID, lw=0.8); ax.set_axisbelow(True)
for s in ("top", "right"): ax.spines[s].set_visible(False)
ax.legend(frameon=False, loc="lower right", labelcolor=INK)
ax.set_title("GridSim sparse vs each tool, by network size", loc="left", color=INK, fontsize=13, pad=12)
foot(fig, " Tool times are the harness's per-solve means; check whether interpreter start-up is included before quoting.")
fig.tight_layout(rect=(0, 0.08, 1, 1)); fig.savefig(os.path.join(out, "04-per-tool-ratio.png")); plt.close(fig)

# Chart 5: GridSim-Models tier, solve time per path
mods = list(csv.DictReader(open(os.path.join(data, "models-comparison.csv"))))
fig, ax = plt.subplots(figsize=(9, 5.2), dpi=150)
pc = {"sparse": BLUE, "dense": ORANGE, "gpu-sparse": AQUA, "gpu-dense": YELLOW}
allx = []
for path, col in pc.items():
    rr = sorted((int(m["buses"]), float(m["mean_ms"])) for m in mods if m["path"] == path and m["status"] == "pass" and m["mean_ms"])
    if rr:
        allx += [b for b, _ in rr]
        ax.plot([b for b, _ in rr], [v for _, v in rr], color=col, lw=2, marker="o", ms=5, mec=SURF, mew=1.2, label=f"{path} ({len(rr)}/36 pass)")
errs = [(int(m["buses"]), m["path"]) for m in mods if m["status"] == "error"]
ax.set_xscale("log"); ax.set_yscale("log"); logx(ax, allx)
ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:g}"))
ax.set_xlabel("Buses (log)"); ax.set_ylabel("Mean solve time, ms (log)"); style(ax)
ax.legend(frameon=False, loc="upper left", labelcolor=INK)
ax.set_title("GridSim-Models (36): solve time by path", loc="left", color=INK, fontsize=13, pad=12)
foot(fig, f" Models tier: 2 GB cap, 900 s timeout; {len(errs)} errors, all memory cap or timeout on dense/GPU paths, none numerical. All 10 results bit-identical per model.")
fig.tight_layout(rect=(0, 0.08, 1, 1)); fig.savefig(os.path.join(out, "05-models-solve-time.png")); plt.close(fig)

print(len(pts), "paired cases;", sum(v > 1 for v in r), "GridSim faster")

# Chart 6: GridSim sparse vs every other tool, solve time by network size (MATPOWER/PGLib cases)
CAT = {"GridSim sparse": "#2a78d6", "matpower": "#eb6834", "pandapower": "#1baf7a", "pypsa": "#eda100",
       "veragrid": "#e87ba4", "powsybl": "#008300", "andes": "#6250d6", "powermodels": "#e34948"}
fig, ax = plt.subplots(figsize=(10, 5.8), dpi=150)
allx = []
for name, col in CAT.items():
    path = "sparse" if name == "GridSim sparse" else name
    rr = sorted((int(p[path]["buses"]), float(p[path]["mean_ms"])) for by in tiers.values() for p in by.values()
                if path in p and ok(p[path]) and p[path]["mean_ms"])
    if not rr: continue
    allx += [b for b, _ in rr]
    gs = name == "GridSim sparse"
    ax.plot([b for b, _ in rr], [v for _, v in rr], color=col, lw=2.6 if gs else 1.4, alpha=1 if gs else 0.85,
            marker="o", ms=4 if gs else 3, mec=SURF, mew=0.8, zorder=5 if gs else 3,
            label=f"{name} ({len(rr)} cases)")
ax.axvline(5000, color=INK2, lw=0.8, ls=(0, (4, 3)))
ax.set_xscale("log"); ax.set_yscale("log"); logx(ax, allx)
ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:g}"))
ax.set_xlabel("Buses (log)"); ax.set_ylabel("Mean solve time, ms (log)"); style(ax)
ax.legend(frameon=False, loc="lower right", labelcolor=INK, fontsize=8.5)
ax.set_title("AC power flow solve time: GridSim sparse vs every other tool", loc="left", color=INK, fontsize=13, pad=12)
foot(fig, " Each line = cases that tool solved 10/10 with residual <= 1e-8 pu (ANDES fails the residual check by design above small cases)."
     " Tool times are the harness's per-solve means; check whether interpreter start-up is included before quoting.")
fig.tight_layout(rect=(0, 0.08, 1, 1)); fig.savefig(os.path.join(out, "06-gridsim-vs-all-tools.png")); plt.close(fig)

# Chart 00: one-page headline summary (stat tiles)
gs_pass = sum(1 for by in tiers.values() for p in by.values() if ok(p.get("sparse", {})))
mods_pass = sum(1 for m in mods if m["path"] == "sparse" and m["status"] == "pass")
big = max(((int(p["sparse"]["buses"]), c, float(p["sparse"]["mean_ms"])) for by in tiers.values() for c, p in by.items()
           if ok(p.get("sparse", {}))), default=(0, "", 0))
gs_only = outcomes["standard"]["Fails only in GridSim"] + outcomes["large"]["Fails only in GridSim"]
refused = outcomes["standard"]["Parser refused (PMIN>PMAX)"] + outcomes["large"]["Parser refused (PMIN>PMAX)"]
tiles = [
    (f"{gs_pass + mods_pass}", "cases solved by GridSim", f"{gs_pass} MATPOWER/PGLib + {mods_pass}/36 GridSim-Models"),
    (f"{sum(v > 1 for v in r)}/{len(r)}", "faster than every other tool", "cases GridSim and at least one other tool solved"),
    (f"{big[0]:,}", "buses: largest case solved", f"{big[1].replace('.m','').replace('pglib_opf_','')}, {big[2]/1000:.2f} s per solve"),
    (f"{gs_only}", "case fails only in GridSim", "case13659pegase: fixed after this run (DC-angle retry)"),
    (f"{refused}", "cases the reader refused", "PGLib __api PMIN>PMAX rows: fixed after this run"),
    ("8", "tools compared", "MATPOWER, pandapower, PyPSA, VeraGrid, powsybl, ANDES, PowerModels + GridSim"),
]
fig = plt.figure(figsize=(10, 5.4), dpi=150)
fig.text(0.03, 0.93, "GridSim RC4 large-case benchmark: at a glance", fontsize=15, color=INK)
for i, (big_txt, label, sub) in enumerate(tiles):
    col, row = i % 3, i // 3
    x0, y0 = 0.03 + col * 0.325, 0.50 - row * 0.40
    fig.patches.append(matplotlib.patches.FancyBboxPatch((x0, y0), 0.30, 0.34, boxstyle="round,pad=0.005,rounding_size=0.012",
                       transform=fig.transFigure, facecolor="#f3f2ee", edgecolor=GRID, lw=1))
    fig.text(x0 + 0.02, y0 + 0.20, big_txt, fontsize=24, color=BLUE if i < 3 else INK, weight="bold")
    fig.text(x0 + 0.02, y0 + 0.13, label, fontsize=10, color=INK)
    fig.text(x0 + 0.02, y0 + 0.03, textwrap.fill(sub, 46), fontsize=7.5, color=INK2)
foot(fig)
fig.savefig(os.path.join(out, "00-summary.png")); plt.close(fig)
