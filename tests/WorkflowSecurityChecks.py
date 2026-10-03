from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
workflow_path = root / ".github" / "workflows" / "windows-test-validation.yml"
workflow = workflow_path.read_text(encoding="utf-8")
branch = "test/v0.2-windows-validation-rev3-20261003"
expected_actions = {
    "actions/checkout": "11bd71901bbe5b1630ceea73d27597364c9af683",
    "actions/setup-dotnet": "67a3573c9a986a3f9c594539f4ab511d57bb3ce9",
    "actions/upload-artifact": "ea165f8d65b6e75b540449e92b4886f43607fa02",
}
uses = re.findall(r"^\s*uses:\s*([^\s#]+)", workflow, re.MULTILINE)
if len(uses) != len(expected_actions):
    raise SystemExit(f"unexpected external action count: {len(uses)}")
for use in uses:
    name, separator, revision = use.partition("@")
    if not separator or expected_actions.get(name) != revision or not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise SystemExit(f"unapproved/unpinned workflow action: {use}")
if "on:\n  push:\n    branches:\n      - " + branch not in workflow:
    raise SystemExit("workflow is not limited to the authorized test-branch push")
for forbidden in ("pull_request", "pull_request_target", "workflow_dispatch", "self-hosted", "secrets.", "write-all"):
    if forbidden.lower() in workflow.lower():
        raise SystemExit(f"forbidden workflow trigger, runner, or secret reference: {forbidden}")
if not re.search(r"(?m)^permissions:\n  contents: read$", workflow):
    raise SystemExit("workflow must request only read access to repository contents")
if not re.search(r"(?ms)^  windows-validation:\n.*?^    runs-on: windows-2022\n.*?^    timeout-minutes: (\d+)\n.*?^    permissions:\n      contents: read$", workflow):
    raise SystemExit("job must use hosted Windows 2022, bounded timeout, and read-only permissions")
timeout = int(re.search(r"(?m)^    timeout-minutes: (\d+)$", workflow).group(1))
if not 1 <= timeout <= 60:
    raise SystemExit(f"workflow timeout is outside 1–60 minutes: {timeout}")
if "persist-credentials: false" not in workflow or "submodules: false" not in workflow:
    raise SystemExit("checkout must avoid persistent credentials and submodule execution")
if "core.autocrlf false" not in workflow or "git checkout-index --force --all" not in workflow:
    raise SystemExit("Windows checkout must preserve canonical source bytes for an exact source fingerprint")
if "NSIS%203/3.13/nsis-3.13.zip" not in workflow or "ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8" not in workflow:
    raise SystemExit("NSIS toolchain download is not pinned to the audited archive hash")
if "curl.exe" not in workflow or "--location" not in workflow or "--fail" not in workflow or "Invoke-WebRequest" in workflow:
    raise SystemExit("NSIS archive download must follow official binary redirects and fail on transport errors")
if "retention-days: 30" not in workflow or "if: always()" not in workflow:
    raise SystemExit("Windows evidence/installer artifact is not retained on failures")
if "artifacts/source-file-fingerprints.tsv" not in workflow:
    raise SystemExit("Windows artifact must retain the audited source file-hash manifest")
installer_smoke = (root / "tests" / "InstallerSmoke.ps1").read_text(encoding="utf-8")
if ("Start-Process -FilePath $installer " not in installer_smoke or "$installer.Path" in installer_smoke
        or '$arguments = "/S /D=$installDir"' not in installer_smoke):
    raise SystemExit("installer smoke must launch its resolved installer path")
private_markers = ("/" + "Users" + "/", "/" + "home" + "/", "C:" + chr(92) + "Users" + chr(92),
                   "todo-" + "tracker.md", "AGENTS" + ".md")
if any(marker.lower() in workflow.lower() for marker in private_markers):
    raise SystemExit("workflow source contains private path/task metadata marker")
print("PASS Windows workflow uses only exact test-branch push, SHA-pinned actions, read-only permissions, bounded hosted runner, and hash-verified NSIS")
