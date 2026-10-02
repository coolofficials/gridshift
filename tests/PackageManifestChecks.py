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
print("PASS exact-file uninstall manifest preserves unrelated custom-directory files and non-empty folders")
