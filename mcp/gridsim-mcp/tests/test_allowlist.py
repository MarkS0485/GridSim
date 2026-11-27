import re
from pathlib import Path

import pytest

from gridsim_mcp import allowlist
from gridsim_mcp.allowlist import Refused, check_verb

PLAN_REFUSED = ["gda", "gda-replay", "estimate", "estimate-noise-floor", "serve", "interpose", "pmu-ingest",
                "pmu-estimate", "cycle", "replay", "replay-event", "lake_conformance"]


@pytest.mark.parametrize("verb", PLAN_REFUSED + sorted(allowlist.REFUSED_VERBS))
def test_refused_verbs_are_refused_with_a_reason(verb):
    with pytest.raises(Refused) as ex:
        check_verb(verb, ["case9"])
    assert str(ex.value).startswith("REFUSED:") and len(str(ex.value)) > 30


@pytest.mark.parametrize("flag", sorted(allowlist.REFUSED_FLAGS) + ["--utc-now=2020-01-01T00:00:00Z",
                                                                     "--horizon-hours", "--HORIZON", "--lake-root",
                                                                     "--gda-dir", "--live-anything"])
def test_refused_flags(flag):
    with pytest.raises(Refused):
        check_verb("pf", ["case9", flag, "x"])


def test_allowed_and_unknown():
    assert check_verb("pf", ["case9"]) == "pf"
    assert check_verb("n-1", ["case14"]) == "nminus1"
    with pytest.raises(Refused, match="not available through the GridSim MCP server"):
        check_verb("no-such-verb", ["case9"])
    assert not (allowlist.ALLOWED_VERBS & set(allowlist.REFUSED_VERBS))


@pytest.mark.parametrize("case", ["uk", "gb", "gb-full", "gb-synthetic", "gb-reduced-29", "gb-network-2224",
                                  "data/gb-full", "data/matpower/gb/GBnetwork.m"])
def test_gb_cases_static_verbs_only(case):
    assert check_verb("pf", [case]) == "pf"
    with pytest.raises(Refused, match="static verbs only"):
        check_verb("qsts", [case, "--study", "s.json"])
    with pytest.raises(Refused, match="static verbs only"):
        check_verb("stability", [], case_tokens=[case])


def test_no_case_named_is_refused_for_non_static_verbs():
    with pytest.raises(Refused, match="name the case"):
        check_verb("transient", ["--seconds", "5"])
    assert check_verb("simdcheck", []) == "simdcheck"


@pytest.mark.parametrize("arg", ["lake/2024/x.parquet", "my-lake/x", "GDA/model", "gda-x/x.json",
                                 "data/neso/frequency-sample.csv", "data/events/catalog.json"])
def test_private_and_recorded_paths_refused(arg):
    with pytest.raises(Refused):
        check_verb("pf", [arg])


def test_refused_subcommands():
    with pytest.raises(Refused):
        check_verb("user-model", ["check-plugin", "x.dll"])
    with pytest.raises(Refused):
        check_verb("audit", ["sign", "log.jsonl"])


def test_every_manual_verb_is_classified():
    """Every verb of manual chapter 19's verb index is either allowed or refused by name (when the manual is here)."""
    manual = Path(__file__).resolve().parents[3] / "docs" / "manual" / "0019 CLI reference.md"
    if not manual.is_file():
        pytest.skip("manual chapter 19 not beside the plugin")
    text = manual.read_text(encoding="utf-8")
    index = text[text.index("## Verb index"):text.index("## Parsing rules")]
    verbs = set(re.findall(r"\[`([a-z0-9-]+)(?: --[a-z]+)?`\]", index))
    assert len(verbs) > 100
    unclassified = sorted(v for v in verbs if v not in allowlist.ALLOWED_VERBS and v not in allowlist.REFUSED_VERBS)
    assert unclassified == []


_SENSITIVE = re.compile(r"gda|169|lake|guarded|horizon|forward-inference", re.IGNORECASE)


@pytest.mark.parametrize("verb", sorted(allowlist.REFUSED_VERBS) + ["no-such-verb"])
def test_verb_refusals_are_neutral(verb):
    """A refused verb gets the same neutral words as an unlisted one, plus at most a neutral pointer."""
    with pytest.raises(Refused) as ex:
        check_verb(verb, ["case9"])
    msg = str(ex.value)
    assert msg.startswith(f"REFUSED: '{verb}' is not available through the GridSim MCP server.")
    assert not _SENSITIVE.search(msg.replace(f"'{verb}'", "")), msg


@pytest.mark.parametrize("flag", sorted(allowlist.REFUSED_FLAGS) + ["--horizon-hours", "--lake-root", "--gda-dir"])
def test_flag_refusals_are_neutral(flag):
    with pytest.raises(Refused) as ex:
        check_verb("pf", ["case9", flag])
    msg = str(ex.value)
    assert msg.startswith(f"REFUSED: flag {flag} is not available through the GridSim MCP server.")
    assert not _SENSITIVE.search(msg.replace(flag, "")), msg


def test_location_refusal_and_allowlist_resource_are_neutral():
    with pytest.raises(Refused) as ex:
        check_verb("pf", ["lake/2024/x.parquet"])
    assert not _SENSITIVE.search(str(ex.value).replace("lake/2024/x.parquet", ""))
    text = allowlist.allowlist_text()
    assert not _SENSITIVE.search(text), text
    assert "pf" in text and "--json" in text
