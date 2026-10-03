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

inventory = publish / "runtime-inventory.txt"
if not inventory.is_file():
    raise SystemExit("release runtime/license inventory is missing")
lines = inventory.read_text(encoding="utf-8").splitlines()
listed = {line.split("\t", 1)[0] for line in lines if "\t" in line}
actual = {path.relative_to(publish).as_posix() for path in files if path != inventory}
if listed != actual:
    raise SystemExit(f"runtime inventory does not match actual installer payload: missing={sorted(actual-listed)}, extra={sorted(listed-actual)}")
licenses = [path for path in (publish / "LICENSES").glob("*") if path.is_file()]
if len(licenses) != 7:
    raise SystemExit(f"unexpected runtime/license notice inventory: {len(licenses)} files")
print(f"PASS exact win-x64 payload audit ({len(files)} files; {codeview_count} PE CodeView records checked; no PDB/symbols, personal paths, task metadata, or credentials; exact 7-file license inventory)")
