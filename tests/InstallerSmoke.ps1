$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\artifacts\GridShift-0.2.0-x64-setup.exe')).Path
$publish = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\artifacts\publish')).Path
$manifest = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\artifacts\uninstall-manifest.nsh')).Path
$inventoryPath = Join-Path $publish 'runtime-inventory.txt'
$appData = Join-Path $env:APPDATA 'GridShift'
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GridShift'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\GridShift'
if (Test-Path $registryKey) { throw 'Refusing install smoke test: a GridShift uninstall registry key already exists.' }
if (Test-Path $startMenu) { throw 'Refusing install smoke test: a GridShift Start Menu folder already exists.' }
if (Test-Path $appData) { throw 'Refusing install smoke test: existing GridShift user data will not be touched.' }
if (-not (Test-Path $manifest)) { throw 'Exact uninstaller manifest is missing.' }

$installDir = Join-Path $env:TEMP ('GridShiftInstallerSmoke-' + [guid]::NewGuid().ToString('N'))
$userFile = Join-Path $installDir 'keep-user-file.txt'
$profileFile = Join-Path $appData 'profiles.json'
$ownershipFile = Join-Path $appData 'desktop-ownership.json'
$profileSentinel = '{"profile":"smoke-preserve"}'
$ownershipSentinel = '{"smoke-profile":["00000000-0000-0000-0000-000000000001"]}'

$inventory = Get-Content -LiteralPath $inventoryPath -Encoding UTF8
$entries = @{}
foreach ($line in $inventory) {
    if ($line -match '^([^\t]+)\t([0-9]+)\t([0-9a-f]{64})$') {
        $entries[$Matches[1]] = [pscustomobject]@{ Bytes = [int64]$Matches[2]; Sha256 = $Matches[3] }
    }
}
if ($entries.Count -ne 470) { throw "Unexpected runtime inventory file count: $($entries.Count)." }
$expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $entries.Keys) { [void]$expected.Add($relative.Replace('/', '\')) }
[void]$expected.Add('runtime-inventory.txt')

try {
    New-Item -ItemType Directory -Path $appData | Out-Null
    Set-Content -LiteralPath $profileFile -Value $profileSentinel -NoNewline -Encoding UTF8
    Set-Content -LiteralPath $ownershipFile -Value $ownershipSentinel -NoNewline -Encoding UTF8
    $arguments = "/S /D=$installDir"
    $installProcess = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
    if ($installProcess.ExitCode -ne 0) { throw "NSIS install exited with $($installProcess.ExitCode)." }
    if (-not (Test-Path (Join-Path $installDir 'Uninstall.exe'))) { throw 'Silent installer did not create Uninstall.exe.' }

    $installedFiles = @(Get-ChildItem -LiteralPath $installDir -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($installDir.Length).TrimStart('\')
        if ($relative -ne 'Uninstall.exe') { $relative }
    })
    $installedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $installedFiles) { [void]$installedSet.Add($relative) }
    $missing = @($expected | Where-Object { -not $installedSet.Contains($_) })
    $unexpected = @($installedSet | Where-Object { -not $expected.Contains($_) })
    if ($missing.Count -or $unexpected.Count) { throw "Installed payload mismatch; missing=$($missing -join ','); unexpected=$($unexpected -join ',')." }

    foreach ($relative in $entries.Keys) {
        $file = Join-Path $installDir $relative.Replace('/', '\')
        $actual = Get-Item -LiteralPath $file
        if ($actual.Length -ne $entries[$relative].Bytes) { throw "Installed payload byte-count mismatch: $relative." }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entries[$relative].Sha256) {
            throw "Installed payload hash mismatch: $relative."
        }
    }
    $inventoryFile = Join-Path $installDir 'runtime-inventory.txt'
    if ((Get-FileHash -LiteralPath $inventoryFile -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $inventoryPath -Algorithm SHA256).Hash) {
        throw 'Installed runtime inventory differs from the audited publish inventory.'
    }

    Set-Content -LiteralPath $userFile -Value 'preserve-me' -NoNewline -Encoding UTF8
    $uninstallProcess = Start-Process -FilePath (Join-Path $installDir 'Uninstall.exe') -ArgumentList '/S' -Wait -PassThru
    if ($uninstallProcess.ExitCode -ne 0) { throw "NSIS uninstall exited with $($uninstallProcess.ExitCode)." }
    if (-not (Test-Path $userFile)) { throw 'Uninstaller removed an unrelated custom-directory file.' }
    if (-not (Test-Path $profileFile) -or (Get-Content -LiteralPath $profileFile -Raw) -ne $profileSentinel) {
        throw 'Uninstaller did not preserve the user profile sentinel.'
    }
    if (-not (Test-Path $ownershipFile) -or (Get-Content -LiteralPath $ownershipFile -Raw) -ne $ownershipSentinel) {
        throw 'Uninstaller did not preserve the desktop ownership sentinel.'
    }
    if (Test-Path $registryKey) { throw 'Uninstaller left its HKCU uninstall registry key.' }
    if (Test-Path $startMenu) { throw 'Uninstaller left its Start Menu folder.' }
    Write-Output "PASS silent install matched all $($expected.Count) actual payload files and hashes"
    Write-Output 'PASS silent uninstall preserved unrelated install data, profile, and desktop ownership files'
}
finally {
    if (Test-Path $installDir) {
        if (Test-Path $userFile) { Remove-Item -LiteralPath $userFile -Force }
        if (-not (Get-ChildItem -LiteralPath $installDir -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $installDir -Force
        }
    }
    if ((Test-Path $appData) -and (Test-Path $profileFile) -and (Test-Path $ownershipFile)) {
        $remaining = @(Get-ChildItem -LiteralPath $appData -Force | Where-Object { $_.FullName -notin @($profileFile, $ownershipFile) })
        if ($remaining.Count -eq 0) {
            Remove-Item -LiteralPath $profileFile, $ownershipFile -Force
            Remove-Item -LiteralPath $appData -Force
        }
    }
}
