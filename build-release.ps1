$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 8 SDK first.' }
if (-not (Get-Command makensis -ErrorAction SilentlyContinue)) { throw 'Provide the audited NSIS 3.13 makensis on PATH.' }
$nsisVersion = (& makensis -VERSION).Trim()
if ($nsisVersion -ne 'v3.13') { throw "NSIS 3.13 is required; found $nsisVersion." }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Install Python 3 and add python to PATH for release inventory and uninstall manifest generation.' }
if (-not $env:NSISDIR -or -not (Test-Path (Join-Path $env:NSISDIR 'Include/MUI2.nsh'))) { throw 'Set NSISDIR to the NSIS folder containing Include, Stubs, and Plugins.' }
Remove-Item artifacts/publish, artifacts/uninstall-manifest.nsh, artifacts/GridShift-0.2.0-x64-setup.exe -Recurse -Force -ErrorAction SilentlyContinue
New-Item artifacts -ItemType Directory -Force | Out-Null
dotnet clean Launcher.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Launcher clean failed.' }
dotnet clean tests/SafetyChecks/SafetyChecks.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Safety checks clean failed.' }
dotnet clean tests/UiChecks/UiChecks.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows UI checks clean failed.' }
dotnet restore Launcher.csproj -r win-x64 -p:EnableWindowsTargeting=true --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'NuGet locked restore failed.' }
dotnet restore tests/SafetyChecks/SafetyChecks.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Safety checks restore failed.' }
dotnet restore tests/UiChecks/UiChecks.csproj -r win-x64 -p:EnableWindowsTargeting=true --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Windows UI checks restore failed.' }
dotnet publish Launcher.csproj -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true --no-restore -o artifacts/publish
if ($LASTEXITCODE -ne 0) { throw 'win-x64 publish failed.' }
python build-release-support.py
if ($LASTEXITCODE -ne 0) { throw 'Runtime license inventory generation failed.' }
python tests/ReleasePayloadAudit.py
if ($LASTEXITCODE -ne 0) { throw 'Exact release payload privacy/symbol audit failed.' }
python build-uninstall-manifest.py artifacts/publish artifacts/uninstall-manifest.nsh
if ($LASTEXITCODE -ne 0) { throw 'Uninstall manifest generation failed.' }
dotnet run --project tests/SafetyChecks/SafetyChecks.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Safety checks failed.' }
dotnet run --project tests/UiChecks/UiChecks.csproj -c Release -r win-x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Windows actual UI checks failed.' }
python tests/SourceGuardChecks.py
if ($LASTEXITCODE -ne 0) { throw 'Source guard checks failed.' }
python tests/WorkflowSecurityChecks.py
if ($LASTEXITCODE -ne 0) { throw 'Windows workflow security checks failed.' }
python tests/ReleaseAudit.py
if ($LASTEXITCODE -ne 0) { throw 'Public source audit/fingerprint failed.' }
python tests/PackageManifestChecks.py
if ($LASTEXITCODE -ne 0) { throw 'Installer manifest checks failed.' }
python tests/IconChecks.py
if ($LASTEXITCODE -ne 0) { throw 'Icon/resource regression checks failed.' }
makensis -V3 "-DOUTFILE=artifacts/GridShift-0.2.0-x64-setup.exe" Installer.nsi
if ($LASTEXITCODE -ne 0) { throw 'NSIS installer build failed.' }
Get-FileHash artifacts/GridShift-0.2.0-x64-setup.exe -Algorithm SHA256
(Get-Item artifacts/GridShift-0.2.0-x64-setup.exe).Length
