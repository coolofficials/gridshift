from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2])
files = sorted((path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file()))
directories = sorted((path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_dir()), key=lambda item: (item.count("/"), item), reverse=True)

def nsis(relative: str) -> str:
    path = Path(relative)
    if path.is_absolute() or ".." in path.parts or '"' in relative:
        raise ValueError(f"unsafe install path: {relative}")
    return relative.replace("/", "\\").replace("$", "$$")

lines = [f'Delete "$INSTDIR\\{nsis(path)}"' for path in files]
lines += [f'RMDir "$INSTDIR\\{nsis(path)}"' for path in directories]
lines += ['Delete "$INSTDIR\\Uninstall.exe"', 'RMDir "$INSTDIR"', '']
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text("\n".join(lines), encoding="utf-8")
