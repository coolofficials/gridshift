from hashlib import sha256
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
excluded = {".git", "bin", "obj", "artifacts", ".tools", ".tools4"}
files = sorted((path for path in root.rglob("*") if path.is_file() and not excluded.intersection(path.relative_to(root).parts)), key=lambda path: path.relative_to(root).as_posix())
markers = ("/" + "Users" + "/", "/" + "home" + "/", "C:\\" + "Users" + "\\", "todo-" + "tracker.md", "AGENTS" + ".md")
secret_patterns = [re.compile(rb"gh[pousr]_[A-Za-z0-9_]{20,}"), re.compile(rb"github_pat_[A-Za-z0-9_]{20,}"), re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")]
records = []
file_records = []
for path in files:
    data = path.read_bytes()
    if (path.suffix.lower() in {".cs", ".csproj", ".md", ".nsi", ".nsh", ".ps1", ".py", ".rs", ".sh", ".txt", ".yml", ".json", ".manifest"}
            or path.name == ".gitignore" or (not path.suffix and b"\0" not in data)):
        fingerprint_data = data.replace(b"\r\n", b"\n")
    else:
        fingerprint_data = data
    if any(marker.encode() in data for marker in markers):
        raise SystemExit(f"public source audit found private path/task metadata in {path.relative_to(root)}")
    if any(pattern.search(data) for pattern in secret_patterns):
        raise SystemExit(f"public source audit found token/private-key pattern in {path.relative_to(root)}")
    rel = path.relative_to(root).as_posix()
    file_hash = sha256(fingerprint_data).hexdigest()
    records.append(f"{rel}\0{file_hash}\n".encode())
    file_records.append(f"{rel}\t{file_hash}\n")
fingerprint = sha256(b"".join(records)).hexdigest()
manifest = root / "artifacts" / "source-file-fingerprints.tsv"
manifest.parent.mkdir(parents=True, exist_ok=True)
manifest.write_text("".join(file_records), encoding="utf-8", newline="\n")
print(f"PASS public-source privacy audit ({len(files)} files; no task metadata, personal absolute paths, or credential patterns)")
print(f"source-fingerprint-sha256: {fingerprint}")
