#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then echo "dotnet 8 SDK is required" >&2; exit 1; fi
if ! command -v makensis >/dev/null 2>&1; then echo "NSIS 3.13 makensis is required." >&2; exit 1; fi
if [[ "$(makensis -VERSION)" != "v3.13" ]]; then echo "NSIS 3.13 is required; found $(makensis -VERSION)." >&2; exit 1; fi
if [[ -z "${NSISDIR:-}" ]]; then
  if command -v brew >/dev/null 2>&1; then NSISDIR="$(brew --prefix nsis)/share/nsis"; fi
fi
if [[ ! -f "${NSISDIR:-}/Include/MUI2.nsh" ]]; then echo "Set NSISDIR to the NSIS distribution folder containing Include/MUI2.nsh, Stubs/, and Plugins/." >&2; exit 1; fi
export NSISDIR

rm -rf artifacts/publish artifacts/uninstall-manifest.nsh artifacts/GridShift-0.2.0-x64-setup.exe
mkdir -p artifacts

dotnet clean Launcher.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --nologo
dotnet clean tests/SafetyChecks/SafetyChecks.csproj -c Release --nologo
dotnet clean tests/UiChecks/UiChecks.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --nologo

dotnet restore Launcher.csproj -r win-x64 -p:EnableWindowsTargeting=true --locked-mode
dotnet restore tests/SafetyChecks/SafetyChecks.csproj --locked-mode
dotnet restore tests/UiChecks/UiChecks.csproj -r win-x64 -p:EnableWindowsTargeting=true --locked-mode
dotnet publish Launcher.csproj -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true --no-restore -o artifacts/publish
python3 build-release-support.py
python3 tests/ReleasePayloadAudit.py
python3 build-uninstall-manifest.py artifacts/publish artifacts/uninstall-manifest.nsh
dotnet run --project tests/SafetyChecks/SafetyChecks.csproj -c Release --no-restore
dotnet build tests/UiChecks/UiChecks.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --no-restore
echo "NOT RUN: actual WinForms UI checks require Windows x64; cross-build is not UI execution evidence."
python3 tests/SourceGuardChecks.py
python3 tests/WorkflowSecurityChecks.py
python3 tests/ReleaseAudit.py
python3 tests/PackageManifestChecks.py
python3 tests/IconChecks.py
makensis -V3 -DOUTFILE=artifacts/GridShift-0.2.0-x64-setup.exe Installer.nsi
shasum -a 256 artifacts/GridShift-0.2.0-x64-setup.exe
printf 'bytes: '; wc -c < artifacts/GridShift-0.2.0-x64-setup.exe
shasum -a 256 artifacts/GridShift-0.2.0-x64-setup.exe | awk '{print "sha256: " $1}'
