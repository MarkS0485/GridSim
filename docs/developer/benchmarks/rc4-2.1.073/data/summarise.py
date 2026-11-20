import csv,json,glob,collections,shutil,sys,os,re
from pathlib import Path
"""Summarise a Task 6 run. Usage: python summarise.py RUNS_DIR OUT_DIR SNAPSHOT_LABEL
(RUNS_DIR holds standard/, large/ and models/ from run-suite.py and run-models.py)."""
R=Path(sys.argv[1]); OUT=Path(sys.argv[2]); OUT.mkdir(parents=True,exist_ok=True)
GS=('dense','sparse','gpu-dense','gpu-sparse'); EXT=('matpower','pandapower','pypsa','veragrid','powsybl','andes','powermodels')
L=[]; P=L.append
def classify(tier):
    rows=list(csv.DictReader(open(R/tier/'comparison.csv'))); by=collections.defaultdict(dict)
    for r in rows: by[r['case']][r['path']]=r
    schedl=json.load(open(R/tier/'suite-manifest.json'))['scheduledCases']; sched=len(schedl)
    cls=collections.defaultdict(list)
    for case,p in by.items():
        s=p['sparse']; folder=R/tier/case[:-2]/'sparse'
        log=(folder/'driver.log').read_text(errors='replace') if (folder/'driver.log').is_file() else ''
        others=[k for k in EXT if k!='andes' and p[k]['status']=='complete' and float(p[k]['max_original_residual_pu'] or 1)<=1e-8]
        if s['status']=='complete':
            k='pass' if float(s['max_original_residual_pu'] or 1)<=1e-8 else 'residual>1e-8'
        elif s['status']=='case not materialised': k='not materialised ('+s['note']+')'
        elif 'QMIN above QMAX or PMIN above PMAX' in log: k='parser refused (gen PMIN>PMAX or QMIN>QMAX)'
        elif others: k='GRIDSIM-ONLY NON-CONVERGENCE'
        else: k='no solver converged (flat start)' if not others else ''
        cls[k].append((case,s['buses'],others))
    cls_missing=[c for c in schedl if c not in by]
    return rows,by,sched,cls,cls_missing
P('# Task 6: extended / large-case suite on GridSim 7b648759 (FINAL)\n')
P(f'Snapshot: {sys.argv[3]}. FINAL. Models and standard tiers finished; the large tier ran out of hardware at 41/47 (details below).\n')
P('Setup: Release, TieredCompilation=false, BenchmarkDriver 10 timed attempts after warm-up, flat start, tol 1e-8, max 50 iterations, Q limits off. Three streams ran concurrently, each pinned to its own CPU (standard CPU 1 / 5 GB cgroup, large CPU 2 / 13 GB, models CPU 3 / 2 GB): timings carry shared-memory-bandwidth contention and are not clean single-stream numbers. Dense paths capped at 5000 buses (CPU) and 1500 buses (GPU) in the MATPOWER tiers; 6000 in the models tier. Per-path timeout 1800 s standard, 3600 s large, 900 s models.\n')
P('Catalogue: 277 unique case files (MATPOWER 79, PGLib-OPF 198) + 36 GridSim-Models; 68 power-flow-equivalent duplicates skipped (66 PGLib `__sad`, case30Q, case30pwl), so 209 MATPOWER-format cases scheduled (162 standard <= 5000 buses, 47 large). Licences: PGLib CC BY 4.0 and case_RTS_GMLC (NREL notice) and GridSim-Models (ODbL) vendored; 52 MATPOWER cases with no redistribution grant (43 none stated, 9 "with permission") fetched at run time from the pinned commit. Not obtainable: NESTA (discontinued), TAMU Texas7k/Western/Eastern (HTTP 403 to automated download).\n')
for tier in ('standard','large'):
    rows,by,sched,cls,missing=classify(tier); shutil.copy(R/tier/'comparison.csv',OUT/f'{tier}-comparison.csv')
    done=(R/tier/'attempt-results.csv').is_file()
    if done: shutil.copy(R/tier/'attempt-results.csv',OUT/f'{tier}-attempt-results.csv')
    P(f'## {tier} tier: {len(by)}/{sched} cases done' + (' (finished)' if done else ' (STOPPED: out of hardware, see below)') + '\n')
    if missing and not done: P(f'**Out of hardware: the large tier is incomplete.** At 15:59 UTC on 2026-11-19 PyPSA reached 13.4 GB RSS on case_ACTIVSg25k and hit the stream\'s 13 GB memory cap (as much as one stream gets on this 20 GB box). The OOM kill stopped the whole capped run. The box was then due back at 19:00 GMT, so Mark chose not to run anything more.\n\nPartial: case_ACTIVSg25k.m (25000 buses). GridSim CPU sparse 10/10 converged, 9 iterations, max mismatch 7.6e-12 pu (GridSim\'s own figure, no independent audit), mean 1033 ms per solve. GPU sparse, MATPOWER and pandapower also finished 10 attempts (raw output in the release archive); ANDES, PowerModels, PowSyBl, PyPSA and VeraGrid did not run.\n\n**Not run ({len(missing)-1}):** '+', '.join(m for m in missing if m!='case_ACTIVSg25k.m')+'\n')
    P('GridSim CPU sparse, by outcome:\n'); P('| Outcome | Cases |'); P('|---|---:|')
    for k,v in sorted(cls.items(),key=lambda x:-len(x[1])): P(f'| {k} | {len(v)} |')
    P('')
    for k,v in cls.items():
        if k.startswith('GRIDSIM-ONLY') or k.startswith('residual'):
            P(f'**{k}:**')
            for case,b,o in v:
                a=sorted((R/tier/case[:-2]/'sparse').glob('attempt-*.json')); info=''
                if a: r=json.load(open(a[0]))['result']; info=f"converged={r['converged']}, {r['iterations']} iterations, mismatch {r['maxMismatch']:.2e} pu"
                P(f'- {case} ({b} buses): {info}; converged elsewhere: {", ".join(o)}')
            P('')
    c=collections.Counter((r['path'],r['status']) for r in rows)
    P('All paths (`complete` = 10/10 converged with reference-residual evidence):\n')
    st=sorted({s for _,s in c}); P('| Path | '+' | '.join(st)+' |'); P('|---|'+'---:|'*len(st))
    for p in GS+EXT: P(f'| {p} | '+' | '.join(str(c.get((p,s),0)) for s in st)+' |')
    P('')
P('Correction (2026-11-19 13:50Z): "converged elsewhere" now requires the other tool\'s independently checked residual <= 1e-8 pu, not just its own converged flag. The earlier interim listed case16am, case2868rte and pglib_opf_case20758_epigrids as GridSim-only failures; on that check no tool reached 1e-8 on them (case16am: GridSim 2.9e-8 was the closest, pandapower 4.0e-8, PowerModels 2.0e-7).\n\nNotes: `no evidence` on dense/gpu-dense = above the dense bus cap (skipped by design). The parser refusals are GridSim\'s MatpowerParser rejecting PGLib `__api` generator rows with PMIN > PMAX (the API cases raise loads and PMIN past PMAX); MATPOWER and the other tools ignore those OPF limits in a power flow. ANDES adds 1e-8 pu to line R and X, so its reference-residual check fails by design.\n')
m=list(csv.DictReader(open(R/'models/models-comparison.csv'))); shutil.copy(R/'models/models-comparison.csv',OUT/'models-comparison.csv')
c=collections.Counter((r['path'],r['status']) for r in m)
P('## models tier: 36/36 GridSim-Models done (finished)\n')
P('Gate (GridSim only, no independent audit): 10/10 converged, mismatch <= 1e-8 pu, 10 results bit-identical.\n')
P('| Path | pass | FAIL | error | skipped |'); P('|---|---:|---:|---:|---:|')
for p in GS: P(f'| {p} | '+' | '.join(str(c.get((p,s),0)) for s in ('pass','FAIL','error','skipped'))+' |')
P('\nErrors (all resource limits, none numerical):\n')
for r in m:
    if r['status']=='error':
        lg=(R/'models'/r['model']/r['path']/'driver.log').read_text(errors='replace')
        n='.NET "Out of memory." in the 2 GB cgroup' if 'Out of memory' in lg else ('timeout 900 s' if 'timed out' in r['note'] else r['note'])
        P(f"- {r['model']} ({r['buses']} buses) {r['path']}: {n}, peak RSS {r['peak_rss_mb']} MB")
rss=[float(r['peak_rss_mb']) for r in m if r['path']=='sparse' and r['peak_rss_mb']]
P(f'\nCPU sparse peak RSS across models: max {max(rss):.0f} MB.\n')
(OUT/'SUMMARY.md').write_text('\n'.join(L)+'\n')
print('\n'.join(L))
