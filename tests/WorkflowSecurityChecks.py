from pathlib import Path
import json
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
if len(uses) != 4:
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
    raise SystemExit("workflow must request only read access to repository contents by default")
if not re.search(r"(?ms)^  windows-validation:\n.*?^    runs-on: windows-2022\n.*?^    timeout-minutes: (\d+)\n.*?^    permissions:\n      contents: read$", workflow):
    raise SystemExit("build job must use hosted Windows 2022, bounded timeout, and contents read only")
windows_timeout = int(re.search(r"(?ms)^  windows-validation:\n.*?^    timeout-minutes: (\d+)$", workflow).group(1))
if not 1 <= windows_timeout <= 60:
    raise SystemExit(f"Windows build timeout is outside 1–60 minutes: {windows_timeout}")
if not re.search(r"(?ms)^  preserve-full-actions-logs:\n.*?^    needs: windows-validation\n.*?^    if: always\(\)\n.*?^    runs-on: windows-2022\n.*?^    timeout-minutes: (\d+)\n.*?^    permissions:\n      actions: read$", workflow):
    raise SystemExit("full-log job must run after the build with Actions read-only access")
log_timeout = int(re.search(r"(?ms)^  preserve-full-actions-logs:\n.*?^    timeout-minutes: (\d+)$", workflow).group(1))
if not 1 <= log_timeout <= 15:
    raise SystemExit(f"Actions-log retrieval timeout is outside 1–15 minutes: {log_timeout}")
if "persist-credentials: false" not in workflow or "submodules: false" not in workflow:
    raise SystemExit("checkout must avoid persistent credentials and submodule execution")
if "core.autocrlf false" not in workflow or "git checkout-index --force --all" not in workflow:
    raise SystemExit("Windows checkout must preserve canonical source bytes for an exact source fingerprint")
if "NSIS%203/3.13/nsis-3.13.zip" not in workflow or "ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8" not in workflow:
    raise SystemExit("NSIS toolchain download is not pinned to the audited archive hash")
if "curl.exe" not in workflow or "--location" not in workflow or "--fail" not in workflow or "Invoke-WebRequest" in workflow:
    raise SystemExit("NSIS archive download must follow official binary redirects and fail on transport errors")
if not (root / "global.json").is_file():
    raise SystemExit("workflow must use the repository's exact global.json SDK pin")
if "-Name 'dotnet-version'" not in workflow or "-Arguments '--version'" not in workflow or "DOTNET_SDK_SELECTED=$sdkVersion" not in workflow:
    raise SystemExit("workflow must assert and log the selected .NET SDK version")
if "8.0.408" not in workflow:
    raise SystemExit("workflow does not assert the exact intended .NET SDK version")
if "Start-Process" not in workflow or "-RedirectStandardOutput" not in workflow or "-RedirectStandardError" not in workflow:
    raise SystemExit("native build/UI/audit process output must be explicitly captured to separate stdout/stderr logs")
if "NATIVE_PROCESS=$Name EXIT_CODE=$($process.ExitCode)" not in workflow or "if ($process.ExitCode -ne 0)" not in workflow:
    raise SystemExit("native process exit status must be preserved and fail-fast")
for required in ("dotnet-info", "build-release", "installer-smoke", "artifacts/*.stdout.log",
                "artifacts/*.stderr.log", "artifacts/f3-final-current-guard-mutation.json"):
    if required not in workflow:
        raise SystemExit(f"workflow artifact is missing durable native/mutation evidence: {required}")
if "gh run view $env:GITHUB_RUN_ID" not in workflow or "artifacts/actions-full-run.log" not in workflow:
    raise SystemExit("workflow must preserve the complete post-build Actions run log")
if "GH_TOKEN: ${{ github.token }}" not in workflow or not re.search(r"(?ms)^  preserve-full-actions-logs:\n.*?^    permissions:\n      actions: read$", workflow):
    raise SystemExit("only the post-build log job may use the automatic read-only token to download run logs")
if "retention-days: 30" not in workflow or "if-no-files-found: error" not in workflow or "if: always()" not in workflow:
    raise SystemExit("Windows evidence/log artifacts must be retained and missing full logs must fail")
for path in ("artifacts/source-file-fingerprints.tsv", "artifacts/publish/runtime-inventory.txt", "artifacts/uninstall-manifest.nsh",
             "artifacts/GridShift-0.2.0-x64-setup.exe"):
    if path not in workflow:
        raise SystemExit(f"Windows artifact must retain exact candidate evidence: {path}")

sdk = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]
if sdk != {"version": "8.0.408", "rollForward": "disable", "allowPrerelease": False}:
    raise SystemExit(f"global.json must lock only .NET SDK 8.0.408: {sdk}")
project = (root / "Launcher.csproj").read_text(encoding="utf-8")
if "<RuntimeFrameworkVersion>8.0.15</RuntimeFrameworkVersion>" not in project:
    raise SystemExit("self-contained payload runtime framework is not pinned to .NET 8.0.15")
release_support = (root / "build-release-support.py").read_text(encoding="utf-8")
payload_audit = (root / "tests" / "ReleasePayloadAudit.py").read_text(encoding="utf-8")
if 'expected_runtime_version = "8.0.15"' not in release_support or "expected_runtime_line =" not in payload_audit:
    raise SystemExit("actual published runtime packs must be locked and checked against the inventory")
installer_smoke = (root / "tests" / "InstallerSmoke.ps1").read_text(encoding="utf-8")
if ("Start-Process -FilePath $installer " not in installer_smoke or "$installer.Path" in installer_smoke
        or '$arguments = "/S /D=$installDir"' not in installer_smoke):
    raise SystemExit("installer smoke must launch its resolved installer path")
private_markers = ("/" + "Users" + "/", "/" + "home" + "/", "C:" + chr(92) + "Users" + chr(92),
                   "todo-" + "tracker.md", "AGENTS" + ".md")
if any(marker.lower() in workflow.lower() for marker in private_markers):
    raise SystemExit("workflow source contains private path/task metadata marker")
print("PASS exact test-branch workflow, SHA-pinned actions, least-privilege log-only Actions read, bounded runners, fixed SDK/runtime, and durable full build/UI/process/Actions evidence")
