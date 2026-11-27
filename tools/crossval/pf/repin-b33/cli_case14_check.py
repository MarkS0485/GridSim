"""B33 re-pin proof for the case14 CLI fixtures (dcpf stdout, dcpf --out CSVs, lodf stdout).

Every number in the fixtures is checked against the NumPy makeBdc transcription (../dcpf_fidelity.py):
- cli-files/dcpf-case14.csv flow_mw and dcpf-case14-buses.csv va_deg: to 4096 eps of the norm (per unit / radians),
  as dcpf_repin_check.py checks the --json output; p_inj_mw against the transcription's scheduled injections;
- cli-stdout/dcpf-case14.txt: each printed flow equals the transcription's flow formatted the same way (2 decimals), the
  rows are the 12 largest |flow| in that order, and the island line's figures equal sum Pd, sum Pg and the reference
  injection to the printed decimals;
- cli-stdout/lodf-case14.txt: each printed LODF and percentage equals the transcription's LODF(:, 3) formatted the same
  way (4 decimals, 1 decimal of percent).
A printed value is accepted only if the transcription's value is more than 4096 eps of the norm away from the rounding
boundary, so rounding cannot hide a disagreement.

    python cli_case14_check.py <repo root>
"""
import csv
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import dcpf_fidelity as d  # noqa: E402

EPS = 2.220446049250313e-16


def rounds_to(value, text, decimals, margin):
    q = round(value, decimals)
    edge = abs(abs(value * 10 ** decimals) % 1 - 0.5) / 10 ** decimals   # distance to the rounding boundary
    return f'{q:.{decimals}f}' == f'{float(text):.{decimals}f}' and edge > margin


def main():
    root = sys.argv[1]
    fx = os.path.join(root, 'tests', 'GridSim.Tests', 'fixtures')
    m, _ = d.load(os.path.join(root, 'data', 'matpower', 'lib', 'case14.m'))
    base = m['base']
    va, pf, pinj, ref, parts = d.dcpf(m)
    ok = True

    rows = list(csv.DictReader(open(os.path.join(fx, 'cli-files', 'dcpf-case14.csv'), encoding='utf-8')))
    got = np.array([float(r['flow_mw']) for r in rows]) / base
    r1 = float(np.max(np.abs(got - pf))) / (EPS * max(np.max(np.abs(pf)), 1e-3))
    buses = list(csv.DictReader(open(os.path.join(fx, 'cli-files', 'dcpf-case14-buses.csv'), encoding='utf-8')))
    gva = np.array([float(r['va_deg']) for r in buses]) * np.pi / 180
    r2 = float(np.max(np.abs(gva - va))) / (EPS * max(np.max(np.abs(va)), 1e-3))
    ginj = np.array([float(r['p_inj_mw']) for r in buses])
    r3 = float(np.max(np.abs(ginj - pinj))) / (EPS * max(np.max(np.abs(pinj)), 1e-3))
    print(f'dcpf-case14.csv flows {r1:.1f} eps, -buses.csv angles {r2:.1f} eps, injections {r3:.1f} eps (allowed 4096)')
    ok &= max(r1, r2, r3) <= 4096

    margin = 4096 * EPS * np.max(np.abs(pf)) * base
    txt = open(os.path.join(fx, 'cli-stdout', 'dcpf-case14.txt'), encoding='utf-8').read()
    printed = [(int(a), b) for a, b in re.findall(r'branch (\d+) .*?\s(-?\d+\.\d\d) MW', txt)]
    order = sorted(range(len(pf)), key=lambda k: -abs(pf[k]))[:12]
    same_set = sorted(k for k, _ in printed) == sorted(order)
    good = all(rounds_to(pf[k] * base, v, 2, margin) for k, v in printed)
    isl = re.search(r'demand\s+([\d.]+) MW\s+scheduled generation\s+([\d.]+) MW\s+reference injects\s+([\d.]+) MW', txt)
    pd = sum(b['pd'] for b in m['bus'])
    pg = sum(g['pg'] for g in m['gen'])
    good_isl = (f'{pd:.1f}' == isl.group(1) and f'{pg:.1f}' == isl.group(2) and f'{pinj[ref[0]]:.1f}' == isl.group(3))
    print(f'dcpf-case14.txt: {len(printed)} printed flows, top-12 set {"same" if same_set else "DIFFERENT"}, '
          f'values {"PASS" if good else "FAIL"}, island line {"PASS" if good_isl else "FAIL"}')
    ok &= same_set and good and good_isl

    _, lodf = d.ptdf_lodf(m, parts)
    txt = open(os.path.join(fx, 'cli-stdout', 'lodf-case14.txt'), encoding='utf-8').read()
    printed = re.findall(r'branch (\d+)\s+\S+\s+LODF\s+(-?\d+\.\d{4})\s+\(\s*(-?\d+\.\d)%', txt)
    lm = 4096 * EPS
    good = all(rounds_to(lodf[int(k), 3], v, 4, lm) and rounds_to(100 * lodf[int(k), 3], p, 1, 100 * lm)
               for k, v, p in printed)
    print(f'lodf-case14.txt: {len(printed)} printed factors of column 3 {"PASS" if good else "FAIL"}')
    ok &= good and len(printed) > 0
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
