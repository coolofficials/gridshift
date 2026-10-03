from hashlib import sha256
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
source_relative = Path("VirtualDesktops.cs")
needle = 'if (api.GetCurrentDesktop() != fallback) return "현재 데스크톱이 안전한 복귀 대상과 달라져 정리를 취소합니다.";'
mutation = 'if (false) return "현재 데스크톱이 안전한 복귀 대상과 달라져 정리를 취소합니다.";'
expected_failure = "final current-guard after the second window enumeration blocks removal"
source = root / source_relative
original = source.read_bytes()
source_hash = sha256(original).hexdigest()
output_path = root / "artifacts" / "f3-final-current-guard-mutation.json"


def run_checks(project_root: Path) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    environment["DOTNET_NOLOGO"] = "1"
    return subprocess.run(
        ["dotnet", "run", "--project", "tests/SafetyChecks/SafetyChecks.csproj", "-c", "Release", "-v", "quiet"],
        cwd=project_root,
        env=environment,
        text=True,
        encoding="utf-8",
        errors="replace",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )


ignore = shutil.ignore_patterns(".git", "bin", "obj", "artifacts", ".tools", ".tools4", "__pycache__")
with tempfile.TemporaryDirectory(prefix="gridshift-f3-mutation-") as temporary:
    scratch = Path(temporary) / "source"
    shutil.copytree(root, scratch, ignore=ignore)
    scratch_source = scratch / source_relative
    scratch_original = scratch_source.read_text(encoding="utf-8")
    if scratch_source.read_bytes() != original or scratch_original.count(needle) != 1:
        raise SystemExit("isolated scratch copy differs from production or final guard is not uniquely identifiable")

    baseline = run_checks(scratch)
    if baseline.returncode != 0 or expected_failure not in baseline.stdout:
        raise SystemExit("unmutated scratch regression did not pass; see emitted baseline output")

    before_mutation_hash = sha256(scratch_source.read_bytes()).hexdigest()
    scratch_source.write_text(scratch_original.replace(needle, mutation), encoding="utf-8", newline="")
    mutated_hash = sha256(scratch_source.read_bytes()).hexdigest()
    if mutated_hash == before_mutation_hash:
        raise SystemExit("bounded scratch mutation did not change the final-current guard")
    mutated = run_checks(scratch)
    mutated_output = mutated.stdout + mutated.stderr
    if mutated.returncode == 0 or expected_failure not in mutated_output:
        raise SystemExit("mutated scratch regression did not fail at the final-current-guard assertion")

    if source.read_bytes() != original or sha256(source.read_bytes()).hexdigest() != source_hash:
        raise SystemExit("production source changed during isolated mutation test")

    scratch_prefix = str(scratch)
    def redact_scratch(text: str) -> str:
        return text.replace(scratch_prefix, "<isolated-scratch>").replace(scratch_prefix.replace("\\", "/"), "<isolated-scratch>")

    evidence = {
        "result": "PASS: production regression rejects removal of the final current-desktop guard",
        "production_file": source_relative.as_posix(),
        "production_sha256_before_and_after": source_hash,
        "scratch_copy": "temporary isolated copy; deleted after check",
        "scratch_file_sha256_before_mutation": before_mutation_hash,
        "scratch_file_sha256_after_guard_bypass_mutation": mutated_hash,
        "mutation": "replace final current-desktop comparison with if (false), in scratch copy only",
        "baseline_exit_code": baseline.returncode,
        "baseline_stdout": redact_scratch(baseline.stdout),
        "baseline_stderr": redact_scratch(baseline.stderr),
        "mutated_exit_code": mutated.returncode,
        "expected_failure_assertion": expected_failure,
        "mutated_stdout": redact_scratch(mutated.stdout),
        "mutated_stderr": redact_scratch(mutated.stderr),
        "production_source_unchanged": True,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

print(f"PASS final-current guard mutation sensitivity; production SHA-256 unchanged: {source_hash}")
print(f"PASS isolated mutation evidence: {output_path.relative_to(root).as_posix()}")
