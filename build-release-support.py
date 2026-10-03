import hashlib
import json
import os
from pathlib import Path
import shutil
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent / "tests"))
from pe_metadata import codeview_paths

root = Path(__file__).resolve().parent
publish = root / "artifacts" / "publish"
licenses = publish / "LICENSES"
licenses.mkdir(parents=True, exist_ok=True)

with (publish / "GridShift.deps.json").open(encoding="utf-8") as stream:
    deps = json.load(stream)
package_names = []
for library, metadata in deps["libraries"].items():
    if metadata.get("type") == "runtimepack":
        name, version = library.split("/", 1)
        package_names.append([name.removeprefix("runtimepack."), version])
package_map = {name.lower(): version for name, version in package_names}
expected_packages = {"microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64"}
if set(package_map) != expected_packages:
    raise SystemExit(f"unexpected publish dependency set: {sorted(package_map)}")
other_packages = [name for name, metadata in deps["libraries"].items() if metadata.get("type") == "package"]
if other_packages:
    raise SystemExit(f"unexpected NuGet runtime dependencies: {other_packages}")
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))

for relative in ("GridShift.exe", "GridShift.dll"):
    path = publish / relative
    data = bytearray(path.read_bytes())
    for start, end, current in codeview_paths(data):
        if not current.startswith(b"/_/GridShift/"):
            stable = b"/_/GridShift/apphost.pdb"
            if len(stable) > len(current):
                raise SystemExit(f"cannot safely normalize CodeView path in {relative}")
            data[start:end] = stable + b"\0" * (len(current) - len(stable))
    path.write_bytes(data)

components = [
    ("microsoft.netcore.app.runtime.win-x64", "LICENSE.TXT", "Microsoft.NETCoreApp-LICENSE.txt"),
    ("microsoft.netcore.app.runtime.win-x64", "THIRD-PARTY-NOTICES.TXT", "Microsoft.NETCoreApp-THIRD-PARTY-NOTICES.txt"),
    ("microsoft.windowsdesktop.app.runtime.win-x64", "LICENSE", "Microsoft.WindowsDesktop.App-LICENSE.txt"),
]
for package, source_name, destination_name in components:
    version = package_map.get(package)
    if version is None:
        raise SystemExit(f"runtime inventory missing package: {package}")
    source = cache / package / version / source_name
    if not source.is_file():
        raise SystemExit(f"missing original license file: {source}")
    shutil.copyfile(source, licenses / destination_name)

for name in ("THIRD-PARTY-NOTICES.md", "VirtualDesktop.MIT.txt", "Ciantic.MIT.txt", "NSIS-COPYING.txt"):
    shutil.copyfile(root / "LICENSES" / name, licenses / name)

lines = ["GridShift release payload inventory", "", f"Runtime packages: {', '.join(f'{name} {version}' for name, version in sorted(package_names))}", "", "Files (relative path, bytes, SHA-256):"]
for path in sorted(publish.rglob("*")):
    if path.is_file() and path.name != "runtime-inventory.txt":
        data = path.read_bytes()
        lines.append(f"{path.relative_to(publish).as_posix()}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}")
(publish / "runtime-inventory.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
