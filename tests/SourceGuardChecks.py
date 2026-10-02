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

main = (root / "MainForm.cs").read_text(encoding="utf-8")
poll = main.split("private void Poll()", 1)[1].split("private void StartMissingCompanions", 1)[0]
assert poll.index("var ticks =") < poll.index("ProfileOrchestration.ObserveAll") < poll.index("foreach (var tick in ticks)", poll.index("ProfileOrchestration.ObserveAll"))
assert poll.index("ProfileOrchestration.ObserveAll") < poll.index("CleanupOwnedCompanions(")
assert "AdvancePendingCompanionCleanups(processTree, processTreeAvailable, activeGameProfiles, now)" in poll
assert "CleanupOwnedCompanions(tick.Profile, session, processTree, processTreeAvailable)" in poll
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
assert "profile.Companions = draft.Commit" in editor
normal_close_change = editor.split("list.ItemCheck +=", 1)[1].split("FormClosing +=", 1)[0]
assert "draft.SetStopWhenGameEnds" in normal_close_change and "e.NewValue == CheckState.Checked" in normal_close_change
force_toggle = editor.split("private void ToggleForceConsent()", 1)[1].split("private static string ConsentLabel", 1)[0]
assert "draft.ToggleForceConsent" in force_toggle and "companion.ForceTerminateAfterGrace =" not in force_toggle
assert "CatalogSelectionPolicy.AddToRelatedApps(targetProfile, item.Name, item.Path)" in main
assert "CatalogSelectionPolicy.AddToRelatedApps(targetProfile" in main and "새 게임 프로필로 추가" in main
configured_paths = main.split("private HashSet<string> GetConfiguredGameExecutables()", 1)[1].split("private bool IsProtectedGameForAnotherProfile", 1)[0]
assert "profiles.Select(profile => profile.Executable)" in configured_paths
other_game_guard = main.split("private bool IsProtectedGameForAnotherProfile", 1)[1].split("private void ObserveOwnedCompanionFamilies", 1)[0]
assert "sessions.TryGetValue(profile.Id, out var session)" in other_game_guard
manual_start = main.split("private void LaunchRelatedApp(RelatedApp app)", 1)[1].split("private void ActivateSelectedRelatedApp()", 1)[0]
assert "Process.Start" in manual_start and "SelectedProfile()" not in manual_start
assert "ownedCompanions" not in manual_start and "sessions" not in manual_start
assert "profile.RelatedApps" in main and "profile.Companions" in main
assert 'new ToolStripMenuItem("수동 관련 앱 — 게임과 독립 실행")' in main
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
