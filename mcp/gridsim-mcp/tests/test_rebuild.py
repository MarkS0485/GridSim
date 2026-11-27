"""rebuild_on_demand: off unless enabled; every refusal path; and a dry run on a small git checkout (no dotnet build)."""

import shutil
import subprocess
from pathlib import Path

import pytest

from gridsim_mcp import registry, tools_rebuild, tuning
from gridsim_mcp.allowlist import Refused

PF_OPTIONS = """namespace GridSim.Core.PowerFlow;
public sealed record PowerFlowOptions
{
    private readonly double _tolerance = 1e-8;
}
"""
CLI_DEFAULTS = """namespace GridSim.Cli;
internal static class CliDefaults
{
    public const double RmsStepS = 0.005, RmsDurationS = 3.0;
    public const double ClassicalStepS = 0.002, ClassicalPostClearS = 5.0;
}
"""
GUARD = """namespace GridSim.Core.Diagnostics;
public static class ForwardInferenceGuard { public const double HorizonHours = 169.0; }
"""


def _git(root, *args):
    subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True)


@pytest.fixture
def checkout(tmp_path):
    root = tmp_path / "src-checkout"
    files = {
        "src/GridSim.Cli/GridSim.Cli.csproj": "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
        "src/GridSim.Cli/CliDefaults.cs": CLI_DEFAULTS,
        "src/GridSim.Cli/Program.cs": "// the CLI\n",
        "src/GridSim.Core/PowerFlow/PowerFlowOptions.cs": PF_OPTIONS,
        "src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs": GUARD,
        "src/GridSim.Core/Physics/Laws.cs": "// physics\n",
        "Directory.Build.props": "<Project />\n",
        "data/case.json": "{}\n",
        "notes.txt": "not copied\n",
    }
    for rel, text in files.items():
        p = root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(text.encode("utf-8"))
    _git(root, "init", "-q")
    _git(root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "add", "-A")
    _git(root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "c")
    return root


def test_off_unless_enabled(monkeypatch, pool):
    monkeypatch.delenv(tools_rebuild.ENV_ENABLE, raising=False)
    assert "rebuild_on_demand" in registry.TOOLS and registry.TOOLS["rebuild_on_demand"].group == "rebuild"
    assert "rebuild_on_demand" not in registry.enabled_tools()
    assert "rebuild_on_demand" not in registry.enabled_tools({tools_rebuild.ENV_ENABLE: "${user_config.enable_rebuild}"})
    assert "rebuild_on_demand" in registry.enabled_tools({tools_rebuild.ENV_ENABLE: "true"})
    st, text, err, _ = registry.dispatch(pool, "rebuild_on_demand", {"source": "."}, "t")
    assert err and text.startswith("REFUSED") and "not available through the GridSim MCP server" in text


@pytest.mark.parametrize("edits,words", [
    ([{"symbol": "EarthReturn.Mu0", "value": 1e-6}], "not on the rebuild list"),
    ([{"symbol": "Iec60909.VoltageFactor", "value": 1.0}], "not on the rebuild list"),
    ([{"symbol": "ForwardInferenceGuard.HorizonHours", "value": 1}], "not on the rebuild list"),
    ([{"symbol": "CliDefaults.RmsStepS", "value": 0.5}], "outside its safe range"),
    ([{"symbol": "CliDefaults.RmsStepS", "value": "0.004"}], "outside its safe range"),
    ([{"symbol": "CliDefaults.RmsStepS", "file": "src/GridSim.Cli/Program.cs", "value": 0.004}], "may not touch"),
    ([{"symbol": "CliDefaults.RmsStepS", "value": 0.004, "text": "x"}], "nothing else"),
    ([{"symbol": "CliDefaults.RmsStepS", "value": 0.004}, {"symbol": "CliDefaults.RmsStepS", "value": 0.003}],
     "edited twice"),
    ("CliDefaults.RmsStepS=0.004", "list of"),
])
def test_bad_edits_are_refused(edits, words):
    with pytest.raises(Refused) as ex:
        tools_rebuild.check_edits(edits)
    assert words in str(ex.value) and "Nothing was built" in str(ex.value)


def test_dry_run_copies_edits_and_proves_guards(checkout, tmp_path):
    report, build_dir = tools_rebuild.prepare(checkout, [{"symbol": "CliDefaults.ClassicalStepS", "value": 0.001},
                                                         {"symbol": "PowerFlowOptions.DefaultTolerance",
                                                          "value": 1e-9}], tmp_path / "builds")
    assert report["guard_files_identical"] and report["other_files_identical"]
    guards = {g["file"] for g in report["guard_files"]}
    assert {"src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs", "src/GridSim.Cli/Program.cs",
            "src/GridSim.Cli/GridSim.Cli.csproj", "Directory.Build.props"} <= guards
    assert "src/GridSim.Cli/CliDefaults.cs" not in guards
    text = (build_dir / "src/GridSim.Cli/CliDefaults.cs").read_text(encoding="utf-8")
    assert "ClassicalStepS = 0.001," in text and "RmsStepS = 0.005," in text
    assert "_tolerance = 1e-09;" in (build_dir / "src/GridSim.Core/PowerFlow/PowerFlowOptions.cs").read_text()
    assert not (build_dir / "notes.txt").exists() and (build_dir / "data/case.json").is_file()
    # The checkout itself is never changed.
    assert (checkout / "src/GridSim.Cli/CliDefaults.cs").read_text(encoding="utf-8") == CLI_DEFAULTS
    assert not tools_rebuild._inside(build_dir, checkout)


def test_changed_guard_in_checkout_is_refused(checkout, tmp_path):
    (checkout / "src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs").write_text(GUARD.replace("169.0", "1.0"))
    with pytest.raises(Refused, match="guard file"):
        tools_rebuild.prepare(checkout, [], tmp_path / "builds")
    assert not (tmp_path / "builds").exists()


def test_a_new_file_using_a_guard_symbol_is_a_guard(checkout, tmp_path):
    p = checkout / "src/GridSim.Core/Other.cs"
    p.write_text("class X { double h = ForwardInferenceGuard.HorizonHours; }\n")
    _git(checkout, "add", "-A")
    _git(checkout, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "c2")
    report, _ = tools_rebuild.prepare(checkout, [], tmp_path / "builds")
    assert "src/GridSim.Core/Other.cs" in {g["file"] for g in report["guard_files"]}


def test_build_folder_inside_checkout_is_refused(checkout):
    with pytest.raises(Refused, match="outside the source checkout"):
        tools_rebuild.prepare(checkout, [], checkout / "builds")


def test_not_a_checkout_is_refused(tmp_path):
    with pytest.raises(Refused, match="not a GridSim checkout"):
        tools_rebuild.prepare(tmp_path, [], tmp_path.parent / "b")


def test_declaration_not_found_once_is_refused(checkout, tmp_path):
    p = checkout / "src/GridSim.Cli/CliDefaults.cs"
    p.write_text(CLI_DEFAULTS + CLI_DEFAULTS)
    with pytest.raises(Refused, match="occurs 2 times"):
        tools_rebuild.prepare(checkout, [{"symbol": "CliDefaults.RmsStepS", "value": 0.004}], tmp_path / "builds")


def test_a_copy_that_differs_is_refused_and_removed(checkout, tmp_path, monkeypatch):
    real = shutil.copyfile

    def tampering(src, dst):
        real(src, dst)
        if str(dst).replace("\\", "/").endswith("ForwardInferenceGuard.cs"):
            Path(dst).write_text(GUARD.replace("169.0", "0.0"))
    monkeypatch.setattr(tools_rebuild.shutil, "copyfile", tampering)
    with pytest.raises(Refused, match="not the source plus the allowed edits"):
        tools_rebuild.prepare(checkout, [], tmp_path / "builds")
    assert list((tmp_path / "builds").iterdir()) == []


def test_build_environment_cannot_stamp_a_release(tmp_path):
    env = tools_rebuild.build_env(tmp_path / "b", {"GridSimSbomSha256": "a" * 64, "GRIDSIMSOURCETREE": "clean",
                                                   "GitCommitValue": "b" * 40, "GITHUB_ACTIONS": "true", "CI": "1",
                                                   "PATH": "p"})
    assert set(env) - {"GIT_CEILING_DIRECTORIES", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO",
                       "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"} == {"PATH"}
    assert "-p:GridSimSbomSha256=" in tools_rebuild.BUILD_ARGS and "-p:GridSimSourceTree=" in tools_rebuild.BUILD_ARGS
    assert tools_rebuild.BUILD_ARGS[:5] == ["dotnet", "build", "src/GridSim.Cli", "-c", "Release"]


def test_real_repository_allowlisted_files_are_not_guards():
    repo = Path(__file__).resolve().parents[3]
    if not (repo / "src" / "GridSim.Cli" / "GridSim.Cli.csproj").is_file():
        pytest.skip("not inside a GridSim checkout")
    files = tools_rebuild.tracked_files(repo)
    guards = set(tools_rebuild.guard_files(repo, files))
    for must in ("src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs", "src/GridSim.Core/Diagnostics/UtcTime.cs",
                 "src/GridSim.Core/Diagnostics/TelemetryGuard.cs", "src/GridSim.Core/Licensing/LicenceGate.cs",
                 "src/GridSim.Core/Licensing/BundledNetworks.cs", "src/GridSim.Cli/Program.cs",
                 "src/GridSim.Core/Diagnostics/SupplyChain/BuildStatus.cs", "Directory.Build.targets"):
        assert must in guards, must
    for entry in tuning.load()["rebuild_allowlist"]:
        assert entry["file"] not in guards
        text = (repo / entry["file"]).read_text(encoding="utf-8")
        assert len(__import__("re").findall(entry["pattern"], text)) == 1, entry["symbol"]
