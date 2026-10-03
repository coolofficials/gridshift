from hashlib import sha256
import json
from pathlib import Path
import re
import sys

sys.dont_write_bytecode = True
from pe_metadata import codeview_paths

root = Path(__file__).resolve().parents[1]
publish = root / "artifacts" / "publish"
if not publish.is_dir():
    raise SystemExit("missing exact win-x64 publish payload")
files = sorted(path for path in publish.rglob("*") if path.is_file())
if not files:
    raise SystemExit("empty release payload")

markers = (b"/" + b"Users" + b"/", b"/" + b"home" + b"/", b"C:" + bytes([92]) + b"Users" + bytes([92]), b"todo-" + b"tracker.md", b"AGENTS" + b".md")
secret_patterns = [re.compile(rb"gh[pousr]_[A-Za-z0-9_]{20,}"), re.compile(rb"github_pat_[A-Za-z0-9_]{20,}"), re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")]
for path in files:
    relative = path.relative_to(publish).as_posix()
    if path.suffix.lower() in {".pdb", ".dbg", ".ilk"}:
        raise SystemExit(f"debug/symbol file must not ship: {relative}")
    data = path.read_bytes()
    encodings = (data, data.decode("utf-16le", errors="ignore").encode("utf-8", errors="ignore"))
    if any(marker.lower() in blob.lower() for marker in markers for blob in encodings):
        raise SystemExit(f"release payload contains personal path/task metadata in {relative}")
    if any(pattern.search(data) for pattern in secret_patterns):
        raise SystemExit(f"release payload contains credential/private-key pattern in {relative}")

codeview_count = 0
for path in files:
    if path.suffix.lower() not in {".exe", ".dll"}:
        continue
    data = path.read_bytes()
    relative = path.relative_to(publish).as_posix()
    if data[:2] != b"MZ":
        raise SystemExit(f"invalid Windows PE payload: {relative}")
    for _, _, raw_path in codeview_paths(data):
        codeview_count += 1
        pdb_path = raw_path.decode("utf-8", errors="replace")
        if any(marker.decode("ascii").lower() in pdb_path.lower() for marker in markers):
            raise SystemExit(f"PE CodeView metadata contains personal path/task metadata in {relative}")
        if relative in {"GridShift.exe", "GridShift.dll"} and not pdb_path.startswith("/_/GridShift/"):
            raise SystemExit(f"application CodeView path is not deterministic in {relative}")

for relative in ("GridShift.exe", "GridShift.dll"):
    if not (publish / relative).is_file():
        raise SystemExit(f"missing application PE payload: {relative}")

runtime_metadata_path = publish / "GridShift.deps.json"
with runtime_metadata_path.open(encoding="utf-8") as stream:
    runtime_metadata = json.load(stream)
actual_runtime_packs = {
    library.split("/", 1)[0].removeprefix("runtimepack.").lower(): library.split("/", 1)[1]
    for library, metadata in runtime_metadata["libraries"].items()
    if metadata.get("type") == "runtimepack"
}
expected_runtime_packs = {
    "microsoft.netcore.app.runtime.win-x64": "8.0.15",
    "microsoft.windowsdesktop.app.runtime.win-x64": "8.0.15",
}
if actual_runtime_packs != expected_runtime_packs:
    raise SystemExit(f"actual framework-dependent metadata does not match pinned runtime packs: {actual_runtime_packs}")
inventory = publish / "runtime-inventory.txt"
if not inventory.is_file():
    raise SystemExit("release runtime/license inventory is missing")
lines = inventory.read_text(encoding="utf-8").splitlines()
expected_runtime_line = "Runtime packages: microsoft.netcore.app.runtime.win-x64 8.0.15, microsoft.windowsdesktop.app.runtime.win-x64 8.0.15"
if len(lines) < 3 or lines[2].lower() != expected_runtime_line.lower():
    raise SystemExit("release runtime inventory does not match pinned .NET runtime pack version 8.0.15")
listed = {}
for line in lines:
    if "\t" not in line:
        continue
    fields = line.split("\t")
    if len(fields) != 3 or fields[0] in listed or not fields[1].isdigit() or not re.fullmatch(r"[0-9a-f]{64}", fields[2]):
        raise SystemExit(f"malformed or duplicate runtime inventory entry: {line}")
    listed[fields[0]] = (int(fields[1]), fields[2])
actual = {path.relative_to(publish).as_posix(): path for path in files if path != inventory}
if listed.keys() != actual.keys():
    raise SystemExit(f"runtime inventory does not match actual installer payload: missing={sorted(actual.keys()-listed.keys())}, extra={sorted(listed.keys()-actual.keys())}")
for relative, path in actual.items():
    data = path.read_bytes()
    if listed[relative] != (len(data), sha256(data).hexdigest()):
        raise SystemExit(f"runtime inventory byte-count/hash mismatch: {relative}")
licenses = [path for path in (publish / "LICENSES").glob("*") if path.is_file()]
if len(licenses) != 7:
    raise SystemExit(f"unexpected runtime/license notice inventory: {len(licenses)} files")
print(f"PASS exact win-x64 payload audit ({len(files)} files; {codeview_count} PE CodeView records checked; no PDB/symbols, personal paths, task metadata, or credentials; exact 7-file license inventory)")
