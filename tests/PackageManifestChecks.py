from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory() as temporary:
    temp = Path(temporary)
    payload = temp / "payload"
    install = temp / "custom install"
    payload.mkdir()
    (payload / "GridShift.exe").write_text("owned")
    (payload / "runtime").mkdir()
    (payload / "runtime" / "runtime.dll").write_text("owned")
    install.mkdir()
    (install / "GridShift.exe").write_text("owned")
    (install / "Uninstall.exe").write_text("owned")
    (install / "keep-user-file.txt").write_text("unrelated")
    (install / "runtime").mkdir()
    (install / "runtime" / "runtime.dll").write_text("owned")
    (install / "runtime" / "user-data.bin").write_text("unrelated")
    manifest = temp / "manifest.nsh"
    subprocess.run([sys.executable, str(root / "build-uninstall-manifest.py"), str(payload), str(manifest)], check=True)
    lines = manifest.read_text(encoding="utf-8").splitlines()
    if any("/r" in line or "*" in line for line in lines):
        raise SystemExit("uninstall manifest contains recursive or wildcard deletion")
    for line in lines:
        operation, quoted = line.split(" ", 1)
        relative = quoted.strip('"').replace("$INSTDIR\\", "").replace("\\", "/")
        target = install / relative if relative else install
        if operation == "Delete" and target.is_file():
            target.unlink()
        elif operation == "RMDir" and target.is_dir():
            try: target.rmdir()
            except OSError: pass
    if not (install / "keep-user-file.txt").exists() or not (install / "runtime" / "user-data.bin").exists():
        raise SystemExit("uninstaller manifest removed unrelated custom-directory files")
    if (install / "GridShift.exe").exists() or (install / "runtime" / "runtime.dll").exists():
        raise SystemExit("uninstaller manifest failed to remove owned payload")
    if not install.exists():
        raise SystemExit("uninstaller removed non-empty custom install directory")
publish = root / "artifacts" / "publish"
manifest_path = root / "artifacts" / "uninstall-manifest.nsh"
if not publish.is_dir() or not manifest_path.is_file():
    raise SystemExit("exact release payload/manifest must exist before package checks")
actual_files = {path.relative_to(publish).as_posix() for path in publish.rglob("*") if path.is_file()}
manifest_files = set()
for line in manifest_path.read_text(encoding="utf-8").splitlines():
    if line.startswith('Delete "$INSTDIR\\') and not line.endswith('\\Uninstall.exe"'):
        manifest_files.add(line[len('Delete "$INSTDIR\\'):-1].replace("\\", "/"))
if manifest_files != actual_files:
    raise SystemExit(f"exact-file uninstall manifest does not match actual installed payload: missing={sorted(actual_files-manifest_files)}, extra={sorted(manifest_files-actual_files)}")
installer = (root / "Installer.nsi").read_text(encoding="utf-8")
if 'File /r "artifacts\\publish\\*"' not in installer:
    raise SystemExit("NSIS installer does not package the audited publish payload")
if any(path.suffix.lower() in {".pdb", ".dbg", ".ilk"} for path in publish.rglob("*") if path.is_file()):
    raise SystemExit("debug symbols would leak into the NSIS install payload")
print(f"PASS exact-file uninstall manifest covers all {len(actual_files)} actual install payload files and no unrelated files")
