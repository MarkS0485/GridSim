import sys
import textwrap

from conftest import CLI, needs_cli
from gridsim_mcp import build_status


COMMIT = "0123456789abcdef0123456789abcdef01234567"


def _fake(tmp_path, compliance_exit, reported=None, name="fake_gridsim.py", commit=COMMIT):
    script = tmp_path / name
    hello = "" if reported is None else (
        '{"protocol":"gridsim-rpc/1","id":1,"op":"hello","status":"ok","exitCode":0,"case_sha256":null,'
        '"result":{"protocol":"gridsim-rpc/1","tool":"gridsim 9.9.9+' + commit + '","build_status":"' + reported + '",'
        '"operations":["hello"]}}')
    script.write_text(textwrap.dedent(f"""
        import sys
        if sys.argv[1:] == ["--version"]:
            print("gridsim 9.9.9+{commit}")
        elif sys.argv[1:3] == ["compliance", "verify"]:
            print("  verdict        test")
            sys.exit({compliance_exit})
        elif sys.argv[1:] == ["rpc"]:
            sys.stdin.read()
            print({hello!r})
    """), encoding="utf-8")
    return [sys.executable, str(script)]


def test_release_needs_a_listed_commit_and_binary_not_compliance(tmp_path):
    cmd = _fake(tmp_path, 1)
    commits = tmp_path / "COMMITS"
    commits.write_text("# comment\n", encoding="utf-8")
    ident = build_status.identify(cmd, sums_file=tmp_path / "none", commits_file=commits)
    assert ident["build_status"] == build_status.UNSUPPORTED and ident["commit"] == COMMIT
    assert not ident["commit_listed"]
    commits.write_text(f"{COMMIT}  v9.9.9\n", encoding="utf-8")
    # The commit is listed but the program's SHA-256 is not a published binary: UNSUPPORTED BUILD (a self-built binary
    # of a release commit names that commit too).
    ident = build_status.identify(cmd, sums_file=tmp_path / "none", commits_file=commits)
    assert ident["build_status"] == build_status.UNSUPPORTED and ident["commit_listed"] and not ident["program_listed"]
    assert any("SHA-256 is not in the list of published release binaries" in r for r in ident["reasons"])
    ident = build_status.identify(cmd, sums_file=_sums(tmp_path, cmd), commits_file=commits)
    # Compliance verify exits 1 (not compliant) and the build is still RELEASE: compliance is reported, not decisive.
    assert ident["build_status"] == build_status.RELEASE and ident["version"] == f"gridsim 9.9.9+{COMMIT}"
    assert ident["compliance_exit"] == 1 and ident["commit_listed"] and ident["program_listed"] is True
    assert ident["reasons"] == [GRIDSIM_SILENT]
    # A short or missing commit never matches a listed one.
    cmd_short = _fake(tmp_path, 0, name="short.py", commit="01234567")
    commits.write_text("01234567  v9.9.9\n", encoding="utf-8")
    ident = build_status.identify(cmd_short, sums_file=tmp_path / "none", commits_file=commits)
    assert ident["build_status"] == build_status.UNSUPPORTED and ident["commit"] is None
    assert any("names no full commit" in r for r in ident["reasons"])


def _sums(tmp_path, cmd):
    sums = tmp_path / "SUMS"
    sums.write_text(f"# published binaries\n{build_status.sha256_file(build_status.program_file(cmd))}  gridsim\n",
                    encoding="utf-8")
    return sums


GRIDSIM_SILENT = "GridSim did not report a build status; the server's check alone decides"


def test_listed_program_hash_alone_is_not_release(tmp_path):
    cmd = _fake(tmp_path, 0, name="h.py")
    ident = build_status.identify(cmd, sums_file=_sums(tmp_path, cmd), commits_file=tmp_path / "none")
    assert ident["program_listed"] is True and ident["build_status"] == build_status.UNSUPPORTED
    assert any("not in the list of published release commits" in r for r in ident["reasons"])


def test_shipped_lists_are_empty_for_now():
    assert build_status.shipped_sums() == set()
    assert build_status.shipped_commits() == set()
    assert build_status.COMMITS_FILE.is_file() and build_status.COMMITS_FILE.parent == build_status.SUMS_FILE.parent


def test_commit_parsing(tmp_path):
    assert build_status.commit_of(f"gridsim 2.1.174+{COMMIT.upper()}") == COMMIT
    assert build_status.commit_of("gridsim 2.1.174") is None
    assert build_status.commit_of("gridsim 2.1.174+abc") is None
    f = tmp_path / "C"
    f.write_text(f"# x\n{COMMIT} v1\nnot-a-commit\n{'a' * 64}\n", encoding="utf-8")
    assert build_status.shipped_commits(f) == {COMMIT, "a" * 64}


def test_gridsim_report_preferred_and_cross_checked(tmp_path):
    commits = tmp_path / "COMMITS"
    commits.write_text(f"{COMMIT}  v9.9.9\n", encoding="utf-8")
    # GridSim says RELEASE and the server's check agrees: RELEASE.
    cmd = _fake(tmp_path, 0, reported="RELEASE", name="rel.py")
    sums = _sums(tmp_path, cmd)
    ident = build_status.identify(cmd, sums_file=sums, commits_file=commits)
    assert ident["gridsim_reported"] == "RELEASE" and ident["server_check"] == "RELEASE"
    assert ident["build_status"] == "RELEASE" and ident["reasons"] == []
    # GridSim says RELEASE but the commit is not a published release commit: UNSUPPORTED BUILD, and why.
    ident = build_status.identify(cmd, sums_file=sums, commits_file=tmp_path / "none")
    assert ident["build_status"] == build_status.UNSUPPORTED
    assert any("GridSim reports RELEASE but the server's check gives UNSUPPORTED BUILD" in r for r in ident["reasons"])
    # GridSim says RELEASE and the commit is listed, but the binary is not a published one: UNSUPPORTED BUILD.
    ident = build_status.identify(cmd, sums_file=tmp_path / "none", commits_file=commits)
    assert ident["build_status"] == build_status.UNSUPPORTED and ident["server_check"] == build_status.UNSUPPORTED
    assert any("not in the list of published release binaries" in r for r in ident["reasons"])
    # The commit is listed but GridSim says UNSUPPORTED BUILD (a local build of the release commit): UNSUPPORTED BUILD.
    cmd2 = _fake(tmp_path, 0, reported="UNSUPPORTED BUILD", name="unsup.py")
    ident = build_status.identify(cmd2, sums_file=sums, commits_file=commits)
    assert ident["build_status"] == build_status.UNSUPPORTED and ident["server_check"] == "RELEASE"
    assert any("GridSim reports UNSUPPORTED BUILD but the server's check gives RELEASE" in r for r in ident["reasons"])


def test_combine_and_document_status():
    assert build_status.combine(None, "RELEASE")[0] == "RELEASE"
    assert build_status.combine("RELEASE", "RELEASE") == ("RELEASE", [])
    assert build_status.combine("UNSUPPORTED BUILD", "UNSUPPORTED BUILD") == ("UNSUPPORTED BUILD", [])
    assert build_status.combine("RELEASE", "UNSUPPORTED BUILD")[0] == "UNSUPPORTED BUILD"
    assert build_status.document_status({"schema": "gridsim-cli-result/1", "buildStatus": "RELEASE"}) == "RELEASE"
    assert build_status.document_status({"schema": "gridsim-cli-result/1"}) is None
    assert build_status.document_status({"schema": "other", "buildStatus": "RELEASE"}) is None


@needs_cli
def test_local_build_is_unsupported():
    ident = build_status.identify([CLI])
    assert ident["build_status"] == build_status.UNSUPPORTED
    assert ident["gridsim_reported"] == build_status.UNSUPPORTED and ident["server_check"] == build_status.UNSUPPORTED
    assert build_status.reported_status([CLI]) == build_status.UNSUPPORTED
    assert ident["compliance_exit"] == 1 and ident["sha256"] and len(ident["sha256"]) == 64
    assert ident["commit"] and len(ident["commit"]) == 40 and not ident["commit_listed"]
    assert ident["program_file"].lower().startswith("gridsim")
