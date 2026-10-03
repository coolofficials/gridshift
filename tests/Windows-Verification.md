# Windows x64 verification gate

`global.json` locks .NET SDK 8.0.408, `Launcher.csproj` locks self-contained runtime packs to 8.0.15, and release inventory/audit checks the actual packages. `build-release.ps1` runs the real `tests/UiChecks` WinForms dialogs. The authorized public test-branch workflow runs it on a hosted Windows 2022 runner and then runs `tests/InstallerSmoke.ps1`, which performs actual NSIS silent install/uninstall, verifies installed file hashes against the audited publish payload, and confirms unrelated files/profile/desktop-ownership sentinels survive. It records separate native process stdout/stderr and exit status, and preserves full completed-build Actions logs as an artifact. A non-Windows cross-build is not UI evidence.

The live UI harness logs the exact run commit, OS/machine/process architecture/session/interactivity, `DeviceDpi`, screen device/bounds/working area, and consent rendered bounds. The hosted runner is not the user's interactive desktop. These are only the runner's current context; they do not establish configured 100/150/200% visual behavior, real Explorer/tray rendering, game behavior, or undocumented COM desktop runtime behavior. Preserve the native stdout/stderr, full Actions log artifact/run URL/hashes and report those limitations precisely.

## Automated UI checks

The harness verifies the live companion editor's new-item check states and consent labels, `ListView.ItemCheck` plus Save/Cancel for autostart, actual normal-close and force-consent Yes prompts plus Save/Cancel, and legacy migration Yes→parent Cancel, No→parent Save, and Yes→parent Save. It also measures the visible desktop-cleanup checkbox preferred/rendered bounds in the live profile editor and records active `DeviceDpi`.

## Per-monitor visual/layout matrix

On Windows 10 and 11 x64, inspect the profile editor at 100%, 150%, and 200% scaling, including moving it between monitors with different scales. Verify the entire desktop cleanup consent is visible and reachable, control text and buttons do not overlap, and the dialog minimum size remains usable. Also inspect tray icon clarity at each scale, light/dark system themes, after Explorer restart, and after restoring GridShift from the tray.

## Runtime/install gate

Using a clean disposable user profile, install the exact candidate and verify the app starts, the Korean guide/labels display, settings/profile data round-trip, and the installed file list matches `artifacts/uninstall-manifest.nsh`. Add an unrelated file under the install directory, uninstall, and verify that file plus `%APPDATA%\GridShift` user data remain. Separately check real Steam/game launch and activation, companion startup/restart/normal-close grace, force-off behavior, and desktop API unsupported/failure fallback. For desktop cleanup test: launcher-created empty desktop; a companion window still open after close request; window/process close later followed by retry; active-game restart cancellation; shared-profile block; unknown/foreign window, ownership, or desktop API state block; and safe fallback return immediately before removal.

Record Windows version/build/UBR, architecture, .NET SDK, display scales, installer SHA-256, and complete test output. Do not convert compile/policy checks or this checklist into a claim of Windows runtime success.
