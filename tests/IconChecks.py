from pathlib import Path
import struct
import zlib

root = Path(__file__).resolve().parents[1]
icon_path = root / "Assets" / "gridshift.ico"
data = icon_path.read_bytes()
reserved, kind, count = struct.unpack_from("<HHH", data)
if (reserved, kind, count) != (0, 1, 7):
    raise SystemExit("GridShift ICO must contain seven Windows icon frames")
expected = {16, 24, 32, 48, 64, 128, 256}
actual = set()
for index in range(count):
    width, height, colors, _, planes, bpp, length, offset = struct.unpack_from("<BBBBHHII", data, 6 + index * 16)
    width, height = width or 256, height or 256
    actual.add(width)
    frame = data[offset:offset + length]
    if width != height or frame[:8] != b"\x89PNG\r\n\x1a\n":
        raise SystemExit(f"invalid PNG icon frame: {width}x{height}")
    png_width, png_height = struct.unpack_from(">II", frame, 16)
    if (png_width, png_height) != (width, height) or planes != 1 or bpp != 32:
        raise SystemExit(f"invalid ICO entry metadata: {width}x{height}")
    cursor, compressed = 8, bytearray()
    while cursor < len(frame):
        length_chunk = struct.unpack_from(">I", frame, cursor)[0]
        chunk_type = frame[cursor + 4:cursor + 8]
        chunk_data = frame[cursor + 8:cursor + 8 + length_chunk]
        if chunk_type == b"IDAT": compressed.extend(chunk_data)
        cursor += 12 + length_chunk
    raw = zlib.decompress(compressed)
    if not any(raw[y * (width * 4 + 1) + 1 + x * 4 + 3] for y in range(height) for x in range(width)):
        raise SystemExit(f"icon frame has no visible alpha pixels: {width}x{height}")
if actual != expected:
    raise SystemExit(f"unexpected icon sizes: {sorted(actual)}")

project = (root / "Launcher.csproj").read_text(encoding="utf-8")
installer = (root / "Installer.nsi").read_text(encoding="utf-8")
form = (root / "MainForm.cs").read_text(encoding="utf-8")
icon_loader = (root / "GridShiftIcon.cs").read_text(encoding="utf-8")
readme = (root / "README.md").read_text(encoding="utf-8")
for marker, content in [
    ("application icon", project), ("embedded icon resource", project),
    ("installer icon", installer), ("tray icon uses embedded resource", form),
    ("resource and Icon are lifetime-owned", icon_loader),
    ("DPI/theme/Explorer restart manual visual checklist", readme),
]:
    if marker == "application icon" and "<ApplicationIcon>Assets/gridshift.ico</ApplicationIcon>" not in content:
        raise SystemExit("EXE application icon resource is not wired")
    if marker == "embedded icon resource" and "GridShift.Assets.gridshift.ico" not in content:
        raise SystemExit("tray icon is not embedded in the executable")
    if marker == "installer icon" and ('Icon "Assets\\gridshift.ico"' not in content or "MUI_UNICON" not in content):
        raise SystemExit("installer/uninstaller icon is not wired")
    if marker == "tray icon uses embedded resource" and "GridShiftIcon.Load()" not in content:
        raise SystemExit("NotifyIcon does not load the designed icon")
    if marker == "resource and Icon are lifetime-owned" and "new Icon(stream)" not in content:
        raise SystemExit("icon resource lifetime loader is missing")
    if marker == "DPI/theme/Explorer restart manual visual checklist" and not all(x in content for x in ("100–250% DPI", "어두운 테마", "Explorer 재시작")):
        raise SystemExit("Windows icon visual verification checklist is incomplete")
print(f"PASS ICO has {count} 32-bit alpha PNG frames at {', '.join(map(str, sorted(actual)))} px")
print("PASS EXE, NotifyIcon, installer and uninstaller share the same embedded multi-size icon")
print("PASS Windows DPI/theme/Explorer-restart visual checklist is documented")
