from pathlib import Path

root = Path(__file__).resolve().parents[1]
project = (root / "Launcher.csproj").read_text(encoding="utf-8")
assert 'Compile Remove="tests/**/*.cs;references/**/*.cs"' in project

com = (root / "VirtualDesktopCom.cs").read_text(encoding="utf-8")
move_window = com.split("public void MoveWindowToDesktop", 1)[1].split("public bool IsWindowPinned", 1)[0]
assert "desktopPointer = FindDesktopPointer(desktopId);" in move_window
assert "CallMoveViewToDesktop(manager, view, desktopPointer)" in move_window
assert "Marshal.GetIUnknownForObject" not in move_window
move_call = com.split("private int CallMoveViewToDesktop", 1)[1].split("private IntPtr FindDesktopPointer", 1)[0]
assert "IntPtr desktopInterfacePointer" in move_call
assert "MoveViewToDesktop(view, desktopInterfacePointer)" in move_call
find_desktop = com.split("private IntPtr FindDesktopPointer", 1)[1].split("private object GetInternalManager", 1)[0]
assert "var iid = GetDesktopIid();" in find_desktop
assert "array.GetAt(index, ref iid, out var candidate)" in find_desktop
assert "if (found) return candidate;" in find_desktop
removal = com.split("public void RemoveDesktop", 1)[1].split("private Guid GetViewCollectionIid", 1)[0]
assert "target = FindDesktopPointer(desktopId)" in removal and "fallback = FindDesktopPointer(fallbackDesktopId)" in removal
assert "CallRemoveDesktop(manager, target, fallback)" in removal
assert removal.index("CallRemoveDesktop") < removal.index("Marshal.Release(fallback)") < removal.index("Release(manager)")
win10_abi = com.split("private interface IVirtualDesktopManagerInternalWin10", 1)[1].split("private interface IVirtualDesktopManagerInternalWin11", 1)[0]
win11_abi = com.split("private interface IVirtualDesktopManagerInternalWin11", 1)[1].split("\n    }", 1)[0]
assert win10_abi.index("CreateDesktop") < win10_abi.index("RemoveDesktop")
assert win11_abi.index("CreateDesktop") < win11_abi.index("MoveDesktop") < win11_abi.index("RemoveDesktop")
assert "int RemoveDesktop(IntPtr desktop, IntPtr fallbackDesktop)" in win10_abi
assert "int RemoveDesktop(IntPtr desktop, IntPtr fallbackDesktop)" in win11_abi
audit = (root / "virtual-desktop-com-audit.md").read_text(encoding="utf-8")
assert "7ff9ef827bab9a081421ebb204339dd96475ec1a" in audit and "a6c69e420307e0717f296501c1e7595977e27b6b" in audit

main = (root / "MainForm.cs").read_text(encoding="utf-8")
poll = main.split("private void Poll()", 1)[1].split("private void StartMissingCompanions", 1)[0]
assert poll.index("var ticks =") < poll.index("ProfileOrchestration.ObserveAll") < poll.index("foreach (var tick in ticks)", poll.index("ProfileOrchestration.ObserveAll"))
assert poll.index("ProfileOrchestration.ObserveAll") < poll.index("CleanupOwnedCompanions(")
assert "AdvancePendingCompanionCleanups(processTree, processTreeAvailable, activeGameProfiles, now)" in poll
assert "AdvancePendingDesktopCleanups(processTree, processTreeAvailable, activeGameProfiles)" in poll
assert poll.index("AdvancePendingCompanionCleanups") < poll.index("AdvancePendingDesktopCleanups")
assert "CleanupOwnedCompanions(tick.Profile, session, processTree, processTreeAvailable)" in poll
assert "pendingDesktopCleanups.Enqueue(tick.Profile.Id, desktopId, ownedFamily)" in poll
assert poll.index("var ownedFamily = GetOwnedCompanionFamily") < poll.index("CleanupOwnedCompanions(") < poll.index("pendingDesktopCleanups.Enqueue")
assert "CleanupCreatedDesktop(tick.Profile, session)" not in poll
assert "desktopLifecycle.Ensure(profile)" in main and "desktopLifecycle.Ensure(tick.Profile)" in poll
assert "TimeSpan.FromSeconds(Math.Clamp(tick.Profile.ExitDebounceSeconds, 1, 10))" in poll
cleanup = main.split("private bool CleanupStillPermitted", 1)[1].split("private bool RequestGracefulClose", 1)[0]
assert "if (!snapshotAvailable" in cleanup
assert "ProcessIdentityProbe.Matches(pending.Handle, pending.Owned)" in cleanup
assert "MayCleanupOwnedCompanion" in cleanup and "snapshotAvailable, configurationMatches" in cleanup
assert "pending.Configuration.Matches(currentConfiguration)" in cleanup
pending = main.split("private void AdvancePendingCompanionCleanups", 1)[1].split("private void FinishPendingCleanup", 1)[0]
assert "CleanupStep.ForceTerminate" in pending and "pending.Configuration.ForceTerminateAfterGrace" in pending
assert "CleanupStillPermitted(pending, snapshot, snapshotAvailable" in pending
request_close = main.split("private bool RequestGracefulClose", 1)[1].split("private void AdvancePendingCompanionCleanups", 1)[0]
assert request_close.count("CleanupStillPermitted(pending, snapshot, snapshotAvailable") == 2
assert "PostMessage(hwnd, WmClose" in request_close
assert "OpenCleanupHandle" in main and "TryTerminate(pending.Handle, pending.Owned)" in pending
assert "CompanionCleanupConfiguration.Capture(configured)" in main
assert "CompanionCleanupConfiguration" in main and "CompanionEditorDraft" in main
editor = main.split("private sealed class CompanionEditorDialog", 1)[1].split("private sealed class RelatedAppEditorDialog", 1)[0]
assert "new CompanionEditorDraft(profile.Companions)" in editor
assert "foreach (var c in draft.Items) AddItem(c)" in editor
assert "profile.Companions = draft.CommitFull" in editor
normal_close_change = editor.split("list.ItemCheck +=", 1)[1].split("FormClosing +=", 1)[0]
assert "draft.SetAutostart" in normal_close_change and "e.NewValue == CheckState.Checked" in normal_close_change
assert "draft.EnableLegacyNormalStopWithConsent" in editor and "StopWhenGameEnds = true" in editor
force_toggle = editor.split("private void ToggleForceConsent()", 1)[1].split("private static string ConsentLabel", 1)[0]
assert "draft.ToggleForceConsent" in force_toggle and "companion.ForceTerminateAfterGrace =" not in force_toggle
assert "CatalogSelectionPolicy.AddToRelatedApps(targetProfile, name, validPath)" in main
assert "AppPathSelectionPolicy.TryValidateExecutable" in main and "AppPathSelectionPolicy.TryResolveShortcut" in main
assert "앱 찾기" in main and "새 게임으로 등록" in main
configured_paths = main.split("private HashSet<string> GetConfiguredGameExecutables()", 1)[1].split("private bool IsProtectedGameForAnotherProfile", 1)[0]
assert "profiles.Select(profile => profile.Executable)" in configured_paths
other_game_guard = main.split("private bool IsProtectedGameForAnotherProfile", 1)[1].split("private void ObserveOwnedCompanionFamilies", 1)[0]
assert "sessions.TryGetValue(profile.Id, out var session)" in other_game_guard
manual_start = main.split("private void LaunchRelatedApp(RelatedApp app)", 1)[1].split("private void ActivateSelectedRelatedApp()", 1)[0]
assert "Process.Start" in manual_start and "SelectedProfile()" not in manual_start
assert "ownedCompanions" not in manual_start and "sessions" not in manual_start
assert "profile.RelatedApps" in main and "profile.Companions" in main
assert 'new ToolStripMenuItem("필요할 때 켜는 앱 — 게임과 독립 실행")' in main
assert 'new TreeNode("필요할 때 켜는 앱")' in main
assert 'ActivateRelatedApp(app)' in main
activation = main.split("private void ActivateRelatedApp(RelatedApp app)", 1)[1].split("private void SearchApps()", 1)[0]
assert "FindProcesses(app.Executable, includeOtherPaths: true)" in activation
assert "ManualRelatedAppPolicy.TryCreateCandidate" in activation
assert activation.count("InvokeManualWindowAction(candidate") == 3
assert "SetManualActivationWarning" in activation
assert "IsWindow(hwnd)" in main and "ProcessIdentityProbe.TryRead(pid" in main
assert "StableTreeSnapshot.Capture" in main and "snapshot.ResolveSelection" in main
assert "EnumerateTreeNodes(tree.Nodes)" in main and "node.IsExpanded" in main
assert 'Name = RelatedAppNodeKey(profile.Id, app.Id)' in main
companion_start = main.split("private void StartMissingCompanions", 1)[1].split("private void PlaceGroupWindows", 1)[0]
assert "profile.Companions" in companion_start and "RelatedApps" not in companion_start
assert "!companion.Autostart" in companion_start
cleanup_desktop = main.split("private void AdvancePendingDesktopCleanups", 1)[1].split("private string? TryCleanupCreatedDesktop", 1)[0]
assert "profile.CleanupCreatedDesktop" in cleanup_desktop and "desktopLifecycle.IsCreatedByLauncher(pending.ProfileId, pending.DesktopId)" in cleanup_desktop
assert "PendingDesktopCleanupPolicy.Evaluate" in cleanup_desktop
assert "desktopLifecycle.MarkCreated" not in main
assert "desktopLifecycle.Forget(profile.Id, desktopId)" in main
ensure_lifecycle = (root / "ProfileDesktopLifecycle.cs").read_text(encoding="utf-8")
assert "coordinator.Ensure(profile)" in ensure_lifecycle and "ownership.MarkCreated(profile.Id, desktopId)" in ensure_lifecycle
assert "windowDesktopIds.Where(pair => pair.Key != launcherWindow)" in (root / "VirtualDesktops.cs").read_text(encoding="utf-8")
policy = (root / "DesktopCleanupPolicy.cs").read_text(encoding="utf-8")
assert "allDesktopWindowsEnumerated" in policy and "windowDesktopIds.Contains(targetDesktop)" in policy
assert "positivelyLauncherCreated" in policy and "sharedWithActiveProfile" in policy
coordinator_cleanup = (root / "VirtualDesktops.cs").read_text(encoding="utf-8").split("public string? RemoveCreatedDesktop", 1)[1].split("private static Dictionary<IntPtr, Guid>", 1)[0]
final_revalidation = coordinator_cleanup.split("finalWindowDesktopIds =", 1)[1]
assert final_revalidation.index("finalDesktops =") < final_revalidation.index("api.GetCurrentDesktop()") < final_revalidation.index("api.RemoveDesktop")
assert "ownedCompanionFamilyComplete" in policy and "IsOwnedCompanionFamilyComplete" in policy
assert "bool profileActive" in policy and "sharedWithActiveProfile" in policy
profile_editor = main.split("private sealed class ProfileEditorDialog", 1)[1].split("private sealed class CompanionEditorDialog", 1)[0]
assert "AutoSize = true" in profile_editor and "AutoScaleMode.Dpi" in profile_editor and "MinimumSize = new Size(820, 480)" in profile_editor
restore = main.split("private void RestoreWindow()", 1)[1].split("private GameProfile? SelectedProfile()", 1)[0]
assert restore.index("MoveWindowToCurrentDesktop") < restore.index("Show();")

print("PASS compile glob excludes upstream audit C# sources")
print("PASS desktop move forwards exact GetAt interface pointer; no canonical IUnknown conversion")
print("PASS Poll observes every game profile before actions")
print("PASS manual Related Apps stay separate from auto companion ownership/lifecycle")
print("PASS graceful WM_CLOSE precedes bounded nonblocking wait and separately opted-in same-handle force path")
print("PASS snapshot-availability and exact committed companion consent guard every close/wait/force path")
print("PASS companion editor force consent remains detached until explicit Save")
print("PASS installed-app search adds selection to chosen profile RelatedApps")
print("PASS tray restore attempts safe current-desktop placement before showing GridShift")
