"""The case lab: generator determinism, the steps, dedup, shrink, the propose template, and the tools' gating. No
GridSim or Lab is needed except for the gated campaign test at the end (GRIDSIM_MCP_LAB_TESTS=1 and the harness)."""

import json
import os

import pytest

from gridsim_mcp import registry, tools_caselab, tools_compare
from gridsim_mcp.caselab import campaign, generator, model, propose, shrink, triage
from gridsim_mcp.caselab.rng import Rng, derive

CASELAB_TOOLS = {"caselab_run", "caselab_status", "caselab_findings", "caselab_reproduce", "caselab_propose"}


# -- generator determinism ------------------------------------------------------------------------------------------

def test_rng_is_splitmix64():
    # Reference outputs of SplitMix64 from seed 0 (Vigna's splitmix64.c).
    r = Rng(0)
    assert [r.next_u64() for _ in range(3)] == [0xE220A8397B1DCDAF, 0x6E789E6AA1B965F4, 0x06C45D188009454F]
    assert derive(42, 0, "radial") == derive(42, 0, "radial") != derive(42, 1, "radial")


@pytest.mark.parametrize("family", generator.FAMILIES)
def test_same_seed_same_bytes(family):
    for i in range(10):
        seed = derive(7, i, family)
        a, b = generator.generate(family, seed), generator.generate(family, seed)
        assert campaign._dump(a) == campaign._dump(b)
        assert model.to_matpower(a) == model.to_matpower(b)
    assert model.to_matpower(generator.generate(family, 1)) != model.to_matpower(generator.generate(family, 2))


@pytest.mark.parametrize("family", generator.FAMILIES)
def test_generated_cases_are_well_formed_and_small(family):
    for i in range(30):
        c = generator.generate(family, derive(3, i, family))
        assert model.check_structure(c) == []
        assert 2 <= len(c["buses"]) <= 30
        assert c["provenance"]["tag"] == "Synthetic"
        assert all(g["pmax"] >= g["pmin"] and g["qmax"] >= g["qmin"] for g in c["gens"])
        assert sum(g["pmax"] for g in c["gens"]) > sum(b["pd"] for b in c["buses"])
        text = model.to_matpower(c)
        assert "mpc.gencost = [" in text and text.isascii()


def test_case_i_does_not_depend_on_n():
    a = campaign.case_list(campaign.params_of(5, 4, None, ["pf"]))
    b = campaign.case_list(campaign.params_of(5, 9, None, ["pf"]))
    assert [(lbl, campaign._dump(c)) for lbl, c in a] == [(lbl, campaign._dump(c)) for lbl, c in b[:4]]
    assert [lbl for lbl, _ in a] == ["c001-radial", "c002-meshed", "c003-transmission", "c004-radial"]


def test_params_are_checked():
    with pytest.raises(Exception):
        campaign.params_of(1, 3, ["nonsense"], None)
    with pytest.raises(Exception):
        campaign.params_of(1, 3, None, ["cpf"])
    p = campaign.params_of(1, 3, None, None)
    assert p["modes"] == list(campaign.DEFAULT_MODES) and p["families"] == list(generator.FAMILIES)


# -- steps ----------------------------------------------------------------------------------------------------------

def _radial_with_tap():
    for i in range(200):
        c = generator.generate("radial", derive("shrink", i))
        if model.has_off_nominal_tap(c) and len(c["buses"]) >= 8 and len(c["gens"]) >= 2:
            return c
    raise AssertionError("no radial case with a tap and an embedded generator in 200 seeds")


def test_steps_apply_and_refuse():
    c = _radial_with_tap()
    slack = model.slack_bus(c)
    with pytest.raises(model.StepRefused):
        model.apply_step(c, {"op": "merge_leaf", "bus": slack})
    leaf = next(s for s in model.reductions(c) if s["op"] == "merge_leaf")
    m = model.apply_step(c, leaf)
    assert len(m["buses"]) == len(c["buses"]) - 1 and m["steps"] == [leaf]
    assert abs(sum(b["pd"] for b in m["buses"]) - sum(b["pd"] for b in c["buses"])) < 1e-4   # the load moved
    assert "steps" not in c                                                                    # the input is unchanged
    tx = next(br for br in c["branches"] if model.is_transformer(br) and br["tap"] != 1.0)
    assert not model.has_off_nominal_tap(model.apply_step(c, {"op": "nominal_tap", "branch": tx["id"]})) or \
        sum(1 for br in c["branches"] if model.is_transformer(br) and br["tap"] != 1.0) > 1
    moved = model.apply_step(c, {"op": "move_tap", "branch": tx["id"], "steps": 1})
    assert next(br for br in moved["branches"] if br["id"] == tx["id"])["tap"] == round(tx["tap"] + 0.0125, 6)
    g = next(g for g in c["gens"] if g["bus"] != slack)
    assert len(model.apply_step(c, {"op": "drop_gen", "gen": g["id"]})["gens"]) == len(c["gens"]) - 1
    with pytest.raises(model.StepRefused):     # a radial feeder has no loop branch
        model.apply_step(c, {"op": "drop_branch", "branch": c["branches"][-1]["id"]})
    with pytest.raises(model.StepRefused):
        model.apply_step(c, {"op": "warp"})
    assert model.apply_steps(c, [leaf]) == m


def test_perturbations_are_seeded():
    c = _radial_with_tap()
    a = model.perturbations(c, Rng(9))
    assert a == model.perturbations(c, Rng(9))
    assert {s["op"] for s in a} <= {"scale_load", "move_tap", "tighten_q", "drop_branch"}


# -- dedup ----------------------------------------------------------------------------------------------------------

def test_signature_ignores_numbers():
    a = triage.signature("rival gate fail", ["pandapower: angle differs from reference by 3.132e-01 deg at bus 7"],
                         None)
    b = triage.signature("rival gate fail", ["pandapower: angle differs from reference by 1.5e-02 deg at bus 12"],
                         None)
    assert a == b == "pandapower: angle differs from reference by # deg at bus #"
    assert triage.signature("documented model difference", ["x"], "powermodels-dcpf-taps") == \
        "documented:powermodels-dcpf-taps"
    g = triage.signature("genuine disagreement", ["max |dVa| 2.0e-04 at bus 3 exceeds 1e-05",
                                                  "max |dVm| 1e-5 at bus 4 exceeds 1e-06",
                                                  "cross-check (gridsim against matpower): differs"], None)
    assert g == "exceeds:dVa,dVm;cross-check"


def test_proposals_are_text_and_name_an_unreadable_answer():
    p = triage.proposal("rival gate fail", "matpower", None,
                        ["matpower: unreadable result: TypeError: cannot unpack non-iterable int object"])
    assert "harness could not read matpower's answer" in p and "not a verdict" in p
    assert triage.proposal("documented model difference", "powermodels", "powermodels-dcpf-taps").startswith(
        "No change proposed")
    assert "Question for Mark" in triage.proposal("genuine disagreement", "matpower", None)


def test_store_dedups_by_class_mode_tool_signature():
    s = triage.Store()
    k = triage.key("documented model difference", "dcpf", "powermodels", "documented:powermodels-dcpf-taps")
    built = []

    def first(fid):
        built.append(fid)
        return {"id": fid}
    f1, new1 = s.add(k, {"case": "c001"}, first)
    f2, new2 = s.add(dict(k), {"case": "c004"}, first)
    assert new1 and not new2 and f1 is f2 and len(built) == 1
    assert [o["case"] for o in f1["occurrences"]] == ["c001", "c004"]
    s.add(triage.key("documented model difference", "dcpf", "pandapower", k["signature"]), {"case": "c001"}, first)
    assert len(s.findings) == 2
    assert triage.finding_id(k) == triage.finding_id(dict(reversed(list(k.items()))))
    assert all(json.loads(line)["id"].startswith("F-") for line in s.lines())


# -- shrink ---------------------------------------------------------------------------------------------------------

def _dc_angles(case, taps=True):
    """DC power flow angles (radians) of a connected case, slack at 0: B' theta = P (b = 1 / (x tap))."""
    ids = [b["id"] for b in case["buses"]]
    slack = model.slack_bus(case)
    idx = {i: k for k, i in enumerate(x for x in ids if x != slack)}
    n = len(idx)
    bmat = [[0.0] * n for _ in range(n)]
    for br in case["branches"]:
        y = 1.0 / (br["x"] * (br["tap"] if taps and model.is_transformer(br) else 1.0))
        f, t = idx.get(br["from"]), idx.get(br["to"])
        for a, c in ((f, t), (t, f)):
            if a is not None:
                bmat[a][a] += y
                if c is not None:
                    bmat[a][c] -= y
    p = [0.0] * n
    for b in case["buses"]:
        if b["id"] in idx:
            p[idx[b["id"]]] -= b["pd"] / case["baseMVA"]
    for g in case["gens"]:
        if g["bus"] in idx:
            p[idx[g["bus"]]] += g["pg"] / case["baseMVA"]
    # Gaussian elimination (small dense system).
    m = [row[:] + [p[k]] for k, row in enumerate(bmat)]
    for c in range(n):
        piv = max(range(c, n), key=lambda r: abs(m[r][c]))
        m[c], m[piv] = m[piv], m[c]
        for r in range(n):
            if r != c and m[r][c]:
                f = m[r][c] / m[c][c]
                m[r] = [a - f * b for a, b in zip(m[r], m[c])]
    return [m[k][n] / m[k][k] for k in range(n)]


def _tap_difference(case):
    """The PowerModels dcpf model difference, in closed form: angles with taps differ from angles without."""
    a, b = _dc_angles(case, True), _dc_angles(case, False)
    return max((abs(x - y) for x, y in zip(a, b)), default=0.0) > 1e-10


def test_shrink_finds_the_two_bus_tap_case():
    c = _radial_with_tap()
    assert _tap_difference(c)
    best, trail, evals, minimal = shrink.shrink(c, _tap_difference, budget=200)
    assert minimal and _tap_difference(best)
    assert (len(best["buses"]), len(best["branches"])) == (2, 1)
    assert model.is_transformer(best["branches"][0]) and best["branches"][0]["tap"] != 1.0
    assert best["steps"] == trail and model.apply_steps(c, trail) == best     # the recipe rebuilds it
    assert len(best["gens"]) == 1                                               # only the slack generator is left
    # A budget that runs out returns the smaller case found so far, marked not minimal.
    part, _, used, done = shrink.shrink(c, _tap_difference, budget=2)
    assert not done and used == 2 and model.size(part) <= model.size(c)


def test_shrink_never_calls_the_predicate_on_a_larger_case():
    c = _radial_with_tap()
    seen = []

    def pred(x):
        seen.append(model.size(x))
        return False
    best, trail, evals, minimal = shrink.shrink(c, pred, budget=500)
    assert best is c and trail == [] and minimal and evals == len(seen)
    assert all(s < model.size(c) for s in seen)


# -- propose --------------------------------------------------------------------------------------------------------

def test_template_is_a_valid_case():
    t = propose.normalise(propose.template())
    assert model.check_structure(t) == []
    assert t["family"] == "proposed" and t["provenance"]["tag"] == "Synthetic"
    assert propose.digest(t) == propose.digest(propose.normalise(propose.template()))
    assert "ohlPerKm" in propose.ranges()


def test_structure_errors_are_named():
    t = propose.template()
    t["buses"][0]["type"] = 1
    t["branches"].append({"id": "b9", "from": 3, "to": 99, "r": 0, "x": 0.1, "b": 0, "rate": 0, "tap": 1,
                          "shift": 0, "kind": "ohl"})
    errs = model.check_structure(propose.normalise(t))
    assert any("exactly one slack" in e for e in errs) and any("b9" in e for e in errs)


# -- gating ---------------------------------------------------------------------------------------------------------

def _harness(tmp_path):
    h = tmp_path / "harness"
    (h / "lab").mkdir(parents=True)
    (h / "run.py").write_text("", encoding="utf-8")
    (h / "lab" / "serve.py").write_text("", encoding="utf-8")
    return h


def test_caselab_tools_absent_unless_enabled(tmp_path, monkeypatch):
    for k in (tools_compare.ENV_ENABLE, tools_compare.ENV_HARNESS, tools_caselab.ENV_ENABLE):
        monkeypatch.delenv(k, raising=False)
    assert CASELAB_TOOLS <= set(registry.TOOLS)
    assert not CASELAB_TOOLS & set(registry.enabled_tools())
    h = str(_harness(tmp_path))
    compare_on = {tools_compare.ENV_ENABLE: "true", tools_compare.ENV_HARNESS: h}
    assert not CASELAB_TOOLS & set(registry.enabled_tools(compare_on))                 # compare alone: no lab
    assert not CASELAB_TOOLS & set(registry.enabled_tools({tools_caselab.ENV_ENABLE: "true"}))   # no harness
    on = dict(compare_on, **{tools_caselab.ENV_ENABLE: "true"})
    assert CASELAB_TOOLS <= set(registry.enabled_tools(on))
    for name in CASELAB_TOOLS:
        spec = registry.TOOLS[name]
        assert spec.group == "caselab" and len(name) <= 64 and spec.title
    assert registry.TOOLS["caselab_status"].read_only and registry.TOOLS["caselab_findings"].read_only
    assert not registry.TOOLS["caselab_run"].read_only


def test_a_disabled_caselab_tool_is_refused(pool, monkeypatch):
    monkeypatch.delenv(tools_caselab.ENV_ENABLE, raising=False)
    _, text, is_error, _ = registry.dispatch(pool, "caselab_propose", {}, "t")
    assert is_error and text.startswith("REFUSED")


def test_propose_through_dispatch(pool, tmp_path, monkeypatch):
    """With the lab on (a stub harness is enough: propose runs GridSim only), the template comes back and the template
    itself passes GridSim's gates."""
    monkeypatch.setenv(tools_compare.ENV_ENABLE, "true")
    monkeypatch.setenv(tools_compare.ENV_HARNESS, str(_harness(tmp_path)))
    monkeypatch.setenv(tools_caselab.ENV_ENABLE, "true")
    structured, _, is_error, _ = registry.dispatch(pool, "caselab_propose", {}, "t")
    assert not is_error and structured["document"]["template"]["schema"] == model.SCHEMA
    assert structured["evidence"]["tool"] == "caselab_propose"
    structured, text, is_error, _ = registry.dispatch(pool, "caselab_propose",
                                                      {"case": structured["document"]["template"]}, "t")
    assert not is_error, text
    d = structured["document"]
    assert d["accepted"], d
    assert d["gates"]["validate"] == 0 and d["gates"]["physics_check"] == "PASS"
    assert set(d["sha256"]) == {"model", "source", "v2", "v2network", "lab"}
    again, _, _, _ = registry.dispatch(pool, "caselab_propose", {"case": propose.template()}, "t")
    assert again["document"]["sha256"] == d["sha256"]                      # same design, same bytes
    bad = propose.template()
    bad["branches"][1]["x"] = 0
    structured, _, is_error, _ = registry.dispatch(pool, "caselab_propose", {"case": bad}, "t")
    assert not is_error and not structured["document"]["accepted"]


@pytest.mark.skipif(not (os.environ.get("GRIDSIM_MCP_LAB_TESTS") and os.environ.get("GRIDSIM_MCP_HARNESS")),
                    reason="needs the Lab Mode harness (GRIDSIM_MCP_LAB_TESTS=1, GRIDSIM_MCP_HARNESS)")
def test_lab_campaign_dcpf_tap_reproducer(pool, monkeypatch):
    monkeypatch.setenv(tools_compare.ENV_ENABLE, "true")
    monkeypatch.setenv(tools_caselab.ENV_ENABLE, "true")
    structured, text, is_error, _ = registry.dispatch(
        pool, "caselab_run", {"seed": 42, "n": 1, "modes": ["dcpf"], "refine_budget": 20,
                              "tools": ["gridsim", "powermodels"]}, "lab")
    assert not is_error, text
    doc = structured["document"]
    f = next(x for x in doc["findingsBrief"] if x["tool"] == "powermodels")
    assert f["class"] == "documented model difference" and f["documented"] == "powermodels-dcpf-taps"
    assert f["reproducer"]["buses"] == 2 and f["reproducer"]["offNominalTaps"] == 1
    r, _, is_error, _ = registry.dispatch(pool, "caselab_reproduce", {"campaign": doc["id"], "finding": f["id"]},
                                          "lab")
    assert not is_error and r["document"]["verdict"] == "REPRODUCED"
