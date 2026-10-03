using GridShift;

var now = DateTime.UtcNow;
var owned = new OwnedProcess(new ProcessIdentity(1234, now.ToFileTimeUtc()), @"C:\Apps\companion.exe");
var checks = new (string Name, bool Expected, bool Actual)[]
{
    ("explicitly opted-in owned process may be stopped", true, ProcessSafety.MayTerminate(owned, true, false, false, true, 1)),
    ("default policy preserves owned process", false, ProcessSafety.MayTerminate(owned, false, false, false, true, 1)),
    ("pre-existing process is not started", false, ProcessSafety.ShouldLaunchCompanion(1)),
    ("active profiles protect a shared companion from desktop placement", true, ProcessSafety.IsSharedWithActiveProfile("first", @"C:\Apps\companion.exe", [("second", @"c:\apps\COMPANION.EXE")])),
    ("same-profile companion is not considered shared", false, ProcessSafety.IsSharedWithActiveProfile("first", @"C:\Apps\companion.exe", [("first", @"C:\Apps\companion.exe")])),
    ("only same process identity may move", true, ProcessSafety.MayMoveCompanionWindow(owned, owned.Identity, owned.Executable)),
    ("pre-existing companion windows are not moved", false, ProcessSafety.MayMoveCompanionWindow(null, owned.Identity, owned.Executable)),
    ("reused process ID is not treated as owned", false, ProcessSafety.MayMoveCompanionWindow(owned, new ProcessIdentity(1234, owned.Identity.CreationFileTime + 1), owned.Executable)),
    ("changed executable is not treated as owned", false, ProcessSafety.MayMoveCompanionWindow(owned, owned.Identity, @"C:\Other\companion.exe")),
    ("ambiguous/shared companion is not terminated", false, ProcessSafety.MayTerminate(owned, true, false, false, true, 2)),
    ("uncertain same-name process prevents termination", false, ProcessSafety.MayTerminate(owned, true, false, false, true, 2)),
    ("other active game protects shared companion", false, ProcessSafety.MayTerminate(owned, true, false, true, true, 1)),
    ("active game protects companion", false, ProcessSafety.MayTerminate(owned, true, true, false, true, 1)),
    ("changed or reused PID is not terminated", false, ProcessSafety.MayTerminate(owned, true, false, false, false, 1)),
    ("delayed child discovery remains inside short startup stability window", false, ProcessSafety.IsStableGame(now, now.AddSeconds(1), TimeSpan.FromSeconds(2))),
    ("game session can start companions after short stability window", true, ProcessSafety.IsStableGame(now, now.AddSeconds(2), TimeSpan.FromSeconds(2))),
    ("short transient process gap does not end session", false, ProcessSafety.HasGameEnded(0, now, now.AddSeconds(2), TimeSpan.FromSeconds(3))),
    ("session ends after configured short debounce", true, ProcessSafety.HasGameEnded(0, now, now.AddSeconds(3), TimeSpan.FromSeconds(3))),
};
foreach (var check in checks)
{
    if (check.Expected != check.Actual) throw new Exception($"{check.Name}: expected {check.Expected}, got {check.Actual}");
    Console.WriteLine($"PASS {check.Name}");
}

var closeAt = now;
var cleanupProgress = new CompanionCleanupProgress(closeAt);
var cleanupGrace = TimeSpan.FromSeconds(10);
Check(ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(2), cleanupGrace, true, false, false, true, false) == CleanupStep.Complete,
    "normal close completes cleanly before timeout without force consent");
Check(ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace, true, false, false, false, false) == CleanupStep.Cancel,
    "timed-out process remains alive/visible when force consent is absent");
Check(ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace, true, false, false, false, true) == CleanupStep.ForceTerminate,
    "force timeout is a distinct step enabled only by explicit consent");
Check(ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace, false, false, false, false, true) == CleanupStep.Cancel,
    "changed identity cancels force termination");
Check(ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace, true, true, false, false, true) == CleanupStep.Cancel,
    "shared/configured-game protections cancel force termination");
Check(ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace, true, false, true, false, true) == CleanupStep.Cancel,
    "game restart cancels pending cleanup before force termination");
var cleanupFixture = new OwnedProcess(new ProcessIdentity(702, 70_200), @"C:\Apps\companion.exe");
var cleanupFamily = new HashSet<ProcessIdentity> { cleanupFixture.Identity };
var noGamePathsForCleanup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var forceCommitted = new Companion { Id = "consent-original", Executable = cleanupFixture.Executable, StopWhenGameEnds = true, ForceTerminateAfterGrace = true };
var pendingConfiguration = CompanionCleanupConfiguration.Capture(forceCommitted);
var snapshotActionCalls = 0;
var noActiveProfilesAfterSessionRemoval = new HashSet<string>();
var snapshotPermission = ProfileOrchestration.MayCleanupOwnedCompanion(cleanupFixture, [], cleanupFamily,
    new HashSet<ProcessIdentity>(), noGamePathsForCleanup, false, true, 1, snapshotAvailable: false,
    configurationMatches: pendingConfiguration.Matches(forceCommitted));
var snapshotCloseStep = ProcessSafety.NextCleanupStep(cleanupProgress, closeAt, cleanupGrace,
    snapshotPermission, false, false, false, forceCommitted.ForceTerminateAfterGrace);
if (snapshotCloseStep == CleanupStep.RequestGracefulClose && snapshotPermission) snapshotActionCalls++;
var snapshotTimeoutStep = ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace,
    snapshotPermission, false, noActiveProfilesAfterSessionRemoval.Contains("origin"), false, forceCommitted.ForceTerminateAfterGrace);
if (snapshotTimeoutStep == CleanupStep.ForceTerminate) snapshotActionCalls++;
Check(noActiveProfilesAfterSessionRemoval.Count == 0 && snapshotCloseStep == CleanupStep.Cancel
    && snapshotTimeoutStep == CleanupStep.Cancel && snapshotActionCalls == 0,
    "snapshot failure after session removal blocks graceful-close and timeout-force callbacks through shared cleanup policy");
var readdedNormalOnly = new Companion { Id = "consent-readded", Executable = cleanupFixture.Executable, StopWhenGameEnds = true, ForceTerminateAfterGrace = false };
Check(!pendingConfiguration.Matches(readdedNormalOnly)
    && ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace,
        pendingConfiguration.Matches(readdedNormalOnly), false, false, false, readdedNormalOnly.ForceTerminateAfterGrace) == CleanupStep.Cancel,
    "re-added same EXE with normal-close-only consent cannot inherit pending force consent");
var revokedForce = new Companion { Id = forceCommitted.Id, Executable = forceCommitted.Executable, StopWhenGameEnds = true, ForceTerminateAfterGrace = false };
Check(!pendingConfiguration.Matches(revokedForce)
    && ProcessSafety.NextCleanupStep(cleanupProgress, closeAt.AddSeconds(11), cleanupGrace,
        pendingConfiguration.Matches(revokedForce), false, false, false, revokedForce.ForceTerminateAfterGrace) == CleanupStep.Cancel,
    "force consent revocation invalidates pending cleanup configuration");
var consentProfile = new GameProfile { Companions = [new Companion { Id = "editor-item", Executable = cleanupFixture.Executable, StopWhenGameEnds = true }] };
var cancelledDraft = new CompanionEditorDraft(consentProfile.Companions);
Check(cancelledDraft.ToggleForceConsent("editor-item")?.ForceTerminateAfterGrace == true
    && !consentProfile.Companions[0].ForceTerminateAfterGrace
    && !consentProfile.Companions[0].ForceTerminateAfterGrace,
    "force consent toggled in detached editor draft stays invisible to polling when dialog is cancelled");
var savedDraft = new CompanionEditorDraft(consentProfile.Companions);
Check(savedDraft.ToggleForceConsent("editor-item")?.ForceTerminateAfterGrace == true
    && !consentProfile.Companions[0].ForceTerminateAfterGrace,
    "modal polling before Save sees only committed normal-close configuration");
consentProfile.Companions = savedDraft.Commit([(savedDraft.Items[0], true)]);
Check(consentProfile.Companions[0].ForceTerminateAfterGrace,
    "explicit Save atomically commits force consent for subsequent polling");
var uncheckedStopProfile = new GameProfile { Companions = [new Companion { Id = "default-stop", Executable = cleanupFixture.Executable, StopWhenGameEnds = false }] };
var cancelledDefaultDraft = new CompanionEditorDraft(uncheckedStopProfile.Companions);
var cancelledDefaultStop = cancelledDefaultDraft.SetStopWhenGameEnds("default-stop", true);
var cancelledDefaultForce = cancelledDefaultDraft.ToggleForceConsent("default-stop");
var cancelPollConfiguration = CompanionCleanupConfiguration.Capture(uncheckedStopProfile.Companions[0]);
Check(cancelledDefaultStop?.StopWhenGameEnds == true && cancelledDefaultForce?.ForceTerminateAfterGrace == true
    && cancelPollConfiguration.Matches(uncheckedStopProfile.Companions[0])
    && !uncheckedStopProfile.Companions[0].StopWhenGameEnds && !uncheckedStopProfile.Companions[0].ForceTerminateAfterGrace,
    "default-off checkbox then force toggle works in one detached edit session; Cancel and modal polling leave committed consent unchanged");
var savedDefaultDraft = new CompanionEditorDraft(uncheckedStopProfile.Companions);
savedDefaultDraft.SetStopWhenGameEnds("default-stop", true);
var savedDefaultForce = savedDefaultDraft.ToggleForceConsent("default-stop");
var preSaveConfiguration = CompanionCleanupConfiguration.Capture(uncheckedStopProfile.Companions[0]);
Check(savedDefaultForce?.ForceTerminateAfterGrace == true && preSaveConfiguration.Matches(uncheckedStopProfile.Companions[0])
    && !uncheckedStopProfile.Companions[0].StopWhenGameEnds && !uncheckedStopProfile.Companions[0].ForceTerminateAfterGrace,
    "default-off checkbox then force toggle enables explicit Save while pre-Save polling sees only committed false consents");
uncheckedStopProfile.Companions = savedDefaultDraft.Commit([(savedDefaultDraft.Items.Single(), true)]);
Check(uncheckedStopProfile.Companions.Single().StopWhenGameEnds && uncheckedStopProfile.Companions.Single().ForceTerminateAfterGrace,
    "Save commits normal-close and explicit force consent toggled in the same dialog session");
var conservativeDeselect = new CompanionEditorDraft(uncheckedStopProfile.Companions);
var deselectedStop = conservativeDeselect.SetStopWhenGameEnds("default-stop", false);
Check(deselectedStop?.StopWhenGameEnds == false && !deselectedStop.ForceTerminateAfterGrace
    && conservativeDeselect.ToggleForceConsent("default-stop") is null,
    "deselecting normal-close conservatively clears force consent and keeps force unavailable");
var revokedDraft = new CompanionEditorDraft(consentProfile.Companions);
consentProfile.Companions = revokedDraft.Commit([(revokedDraft.Items[0], false)]);
Check(!consentProfile.Companions[0].ForceTerminateAfterGrace,
    "saving normal-close unchecked revokes force consent in committed profile");
Check(!System.Text.Json.JsonSerializer.Deserialize<Companion>("{\"Executable\":\"a.exe\",\"StopWhenGameEnds\":true}")!.ForceTerminateAfterGrace,
    "legacy companion config does not acquire force consent");
var newCompanion = new Companion();
var executableFixture = Path.Combine(Path.GetTempPath(), $"GridShift-{Guid.NewGuid():N}.exe");
try
{
    File.WriteAllText(executableFixture, "fixture");
    Check(AppPathSelectionPolicy.TryValidateExecutable(executableFixture, out var selectedExecutable, out _)
        && selectedExecutable == Path.GetFullPath(executableFixture), "EXE selector validates and returns the exact full path");
    Check(!AppPathSelectionPolicy.TryValidateExecutable(Path.ChangeExtension(executableFixture, ".txt"), out _, out _)
        && !AppPathSelectionPolicy.TryValidateExecutable(null, out _, out _), "EXE selector rejects missing and wrong-extension paths");
    Check(!AppPathSelectionPolicy.TryResolveShortcut(executableFixture + ".lnk", out _, out _), "shortcut selector fails closed when link or target is unavailable");
}
finally { if (File.Exists(executableFixture)) File.Delete(executableFixture); }
Check(newCompanion.Autostart && newCompanion.StopWhenGameEnds && !newCompanion.ForceTerminateAfterGrace,
    "new companion defaults to autostart and normal close with force termination disabled");
var legacyCompanion = new Companion { Id = "legacy-migration", Executable = "a.exe", StopWhenGameEnds = false };
Check(legacyCompanion.Autostart && !legacyCompanion.StopWhenGameEnds && !legacyCompanion.ForceTerminateAfterGrace,
    "legacy companion retains disabled normal-stop and force consent while preserving historical autostart");
var legacyDraft = new CompanionEditorDraft([legacyCompanion]);
var migrated = legacyDraft.EnableLegacyNormalStopWithConsent([legacyCompanion.Id]);
Check(migrated.Count == 1 && legacyCompanion.StopWhenGameEnds == false && !migrated[0].ForceTerminateAfterGrace,
    "explicit legacy consent changes only detached draft and never grants force termination");
var cancelLegacy = new CompanionEditorDraft([legacyCompanion]);
Check(!cancelLegacy.Items.Single().StopWhenGameEnds && !legacyCompanion.StopWhenGameEnds,
    "cancelling legacy migration leaves saved consent unchanged");
var catalogProfile = new GameProfile { Name = "Selected", Executable = @"C:\Games\game.exe" };
CatalogSelectionPolicy.AddToRelatedApps(catalogProfile, "Chosen app", @"C:\Apps\chosen.exe");
Check(catalogProfile.RelatedApps.Count == 1 && catalogProfile.RelatedApps[0].Name == "Chosen app"
    && catalogProfile.RelatedApps[0].Executable == Path.GetFullPath(@"C:\Apps\chosen.exe") && catalogProfile.Executable == @"C:\Games\game.exe",
    "installed-app selection adds to selected profile RelatedApps without replacing game executable");

var root = new ProcessIdentity(100, 10_000);
var child = new ProcessIdentity(101, 10_100);
var grandchild = new ProcessIdentity(102, 10_200);
var tree = new ProcessTreeEntry[]
{
    new(100, 1, "Game.exe", root), new(101, 100, "Worker.exe", child), new(102, 101, "Render.exe", grandchild),
    new(900, 1, "unrelated.exe", new ProcessIdentity(900, 12_000))
};
var family = ProcessTreePolicy.Expand(tree, [root]);
Check(family.SetEquals([root, child, grandchild]), "process family tracks exact identities and all descendants");
var afterRootExit = tree.Where(x => x.ProcessId != root.ProcessId).ToArray();
Check(ProcessTreePolicy.CountActive(afterRootExit, family) == 2, "known worker identities keep family active after root exit");
var reusedParent = new ProcessTreeEntry[]
{
    new(100, 1, "unrelated.exe", new ProcessIdentity(100, 20_000)),
    new(101, 100, "Worker.exe", child)
};
Check(!ProcessTreePolicy.Expand(reusedParent, [root]).Contains(child), "PID reuse cannot attach a child to a different parent identity");
Check(ProcessTreePolicy.HasUncertainChild([new ProcessTreeEntry(101, 100, "Worker.exe")], new HashSet<ProcessIdentity> { root }), "uncertain worker child blocks cleanup");

var clock = now;
var gameState = new ProfileRuntimeState(clock);
var gameRoot = new ProcessIdentity(300, 30_000);
var gameWorker = new ProcessIdentity(301, 30_100);
var startGrace = TimeSpan.FromSeconds(2);
var exitGrace = TimeSpan.FromSeconds(3);
var initial = ProfileOrchestration.Observe(gameState, [new(300, 1, "Game.exe", gameRoot)], [gameRoot], true, true, clock, startGrace, exitGrace);
Check(initial.IsActive && !initial.StartCompanions, "root observation begins game lifecycle without premature companion launch");
clock = clock.AddSeconds(1);
var rootAndWorker = new ProcessTreeEntry[] { new(300, 1, "Game.exe", gameRoot), new(301, 300, "GameWorker.exe", gameWorker) };
ProfileOrchestration.Observe(gameState, rootAndWorker, [gameRoot], true, true, clock, startGrace, exitGrace);
clock = clock.AddMilliseconds(500);
var rootExited = ProfileOrchestration.Observe(gameState, [new(301, 300, "GameWorker.exe", gameWorker)], [], false, true, clock, startGrace, exitGrace);
Check(rootExited.IsActive && !rootExited.StartCompanions, "confirmed worker preserves lifecycle after game root exits");
clock = now.AddSeconds(2);
var stableWorker = ProfileOrchestration.Observe(gameState, [new(301, 300, "GameWorker.exe", gameWorker)], [], false, true, clock, startGrace, exitGrace);
Check(stableWorker.IsActive && stableWorker.StartCompanions, "root-to-worker family starts delayed companions at stability threshold");
var unavailableSnapshotState = new ProfileRuntimeState(now);
ProfileOrchestration.Observe(unavailableSnapshotState, [], [gameRoot], true, false, now, startGrace, exitGrace);
var unavailableSnapshot = ProfileOrchestration.Observe(unavailableSnapshotState, [], [], true, false, now.AddSeconds(30), startGrace, exitGrace);
Check(unavailableSnapshot.IsActive && !unavailableSnapshot.StartCompanions, "unavailable process-tree snapshot conservatively delays companion start");
Check(ProfileOrchestration.PlacementFamily(gameState, [new(301, 300, "GameWorker.exe", gameWorker)]).Contains(gameWorker), "root-exited known worker remains in actual desktop placement family");
var debounceState = new ProfileRuntimeState(now);
var debouncedStart = ProfileOrchestration.Observe(debounceState, [new(300, 1, "Game.exe", gameRoot)], [gameRoot], true, true, now, startGrace, exitGrace);
var debounceBefore = ProfileOrchestration.ObserveAll([new ProfilePollInput("debounce", debounceState, [], false, TimeSpan.FromSeconds(2))], [], true, now.AddSeconds(1), startGrace, exitGrace)["debounce"];
var debounceAfter = ProfileOrchestration.ObserveAll([new ProfilePollInput("debounce", debounceState, [], false, TimeSpan.FromSeconds(2))], [], true, now.AddSeconds(2), startGrace, exitGrace)["debounce"];
Check(debouncedStart.IsActive && !debounceBefore.EndSession && debounceAfter.EndSession,
    "per-profile short configurable game-exit debounce delays cleanup only for its selected interval");

var companionIdentity = new ProcessIdentity(400, 40_000);
var companionOwned = new OwnedProcess(companionIdentity, @"C:\Apps\companion.exe");
var workerIdentity = new ProcessIdentity(401, 40_100);
var companionTree = new ProcessTreeEntry[] { new(400, 1, "companion.exe", companionIdentity), new(401, 400, "worker.exe", workerIdentity) };
var companionFamily = new HashSet<ProcessIdentity> { companionIdentity, workerIdentity };
var emptyGameFamily = new HashSet<ProcessIdentity>();
var noGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var terminationCalls = 0;
Check(!ProfileOrchestration.TryCleanupOwnedCompanion(companionOwned, companionTree, companionFamily, emptyGameFamily, noGamePaths, false, true, 1, () => { terminationCalls++; return true; }) && terminationCalls == 0,
    "different-EXE observed worker blocks actual owned-companion termination orchestration");
Check(!ProfileOrchestration.TryCleanupOwnedCompanion(companionOwned, [], companionFamily, emptyGameFamily, noGamePaths, false, true, 1, () => { terminationCalls++; return true; }) && terminationCalls == 0,
    "previously observed but exited descendant remains attached to companion identity family and blocks cleanup");
var gameExecutable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(companionOwned.Executable) };
Check(!ProfileOrchestration.TryCleanupOwnedCompanion(companionOwned, [], new HashSet<ProcessIdentity> { companionIdentity }, activeGameFamily: new HashSet<ProcessIdentity> { companionIdentity }, protectedGameExecutables: noGamePaths, sharedWithProfile: false, identityStillMatches: true, matchingOrUncertainProcessCount: 1, terminate: () => { terminationCalls++; return true; }) && terminationCalls == 0,
    "another active game's confirmed family identity blocks cleanup of its shared EXE as companion");
Check(!ProfileOrchestration.TryCleanupOwnedCompanion(companionOwned, [], new HashSet<ProcessIdentity> { companionIdentity }, emptyGameFamily, gameExecutable, false, true, 1, () => { terminationCalls++; return true; }) && terminationCalls == 0,
    "configured active game executable shared as companion is protected from termination");
Check(!ProfileOrchestration.MayPlaceOwnedCompanionWindow(companionOwned, companionIdentity, companionOwned.Executable, emptyGameFamily, gameExecutable, false),
    "another profile's configured game EXE cannot be moved as an owned companion window");
Check(!ProfileOrchestration.MayPlaceOwnedCompanionWindow(companionOwned, companionIdentity, companionOwned.Executable, new HashSet<ProcessIdentity> { companionIdentity }, noGamePaths, false),
    "another profile's confirmed game-family identity cannot be moved as a companion window");
Check(!ProfileOrchestration.TryCleanupOwnedCompanion(companionOwned, [], new HashSet<ProcessIdentity> { companionIdentity }, emptyGameFamily, noGamePaths, false, false, 1, () => { terminationCalls++; return true; }) && terminationCalls == 0,
    "reused process identity blocks termination action");
Check(!ProfileOrchestration.TryCleanupOwnedCompanion(companionOwned, [new(401, 400, "unknown-worker.exe")], new HashSet<ProcessIdentity> { companionIdentity }, emptyGameFamily, noGamePaths, false, true, 1, () => { terminationCalls++; return true; }) && terminationCalls == 0,
    "uncertain descendant blocks termination action");
var successfulStop = ProfileOrchestration.TryCleanupOwnedCompanion(companionOwned, [], new HashSet<ProcessIdentity> { companionIdentity }, emptyGameFamily, noGamePaths, false, true, 1, () => { terminationCalls++; return true; });
Check(successfulStop && terminationCalls == 1, "safe explicit cleanup reaches injected termination backend exactly once");
var profileAIdentity = new ProcessIdentity(800, 80_000);
var profileBIdentity = new ProcessIdentity(801, 80_100);
var profileAOwned = new OwnedProcess(profileBIdentity, @"C:\Apps\BGame.exe");
var profileTree = new ProcessTreeEntry[]
{
    new(800, 1, "AGame.exe", profileAIdentity),
    new(801, 1, "BGame.exe", profileBIdentity)
};
foreach (var reverse in new[] { false, true })
{
    var stateA = new ProfileRuntimeState(now);
    var stateB = new ProfileRuntimeState(now);
    var inputs = new List<ProfilePollInput>
    {
        new("A", stateA, [profileAIdentity], true),
        new("B", stateB, [profileBIdentity], true)
    };
    if (reverse) inputs.Reverse();
    var observed = ProfileOrchestration.ObserveAll(inputs, profileTree, true, now, startGrace, exitGrace);
    var allGameIdentities = stateA.KnownGameProcesses.Concat(stateB.KnownGameProcesses).ToHashSet();
    var terminated = false;
    var canTerminate = ProfileOrchestration.TryCleanupOwnedCompanion(profileAOwned, profileTree,
        new HashSet<ProcessIdentity> { profileBIdentity }, allGameIdentities,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, true, 1, () => { terminated = true; return true; });
    Check(observed.Count == 2 && !canTerminate && !terminated,
        $"two-phase observe protects A-owned B-game process independent of profile order (reverse={reverse})");
    Check(!ProfileOrchestration.MayPlaceOwnedCompanionWindow(profileAOwned, profileBIdentity,
        profileAOwned.Executable, allGameIdentities, new HashSet<string>(StringComparer.OrdinalIgnoreCase), false),
        $"two-phase desktop placement protects B-game identity independent of profile order (reverse={reverse})");
}

var configuredBExecutable = Path.GetFullPath(profileAOwned.Executable);
var protectedBeforeBSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { configuredBExecutable };
var earlyTerminateCalls = 0;
Check(!ProfileOrchestration.TryCleanupOwnedCompanion(profileAOwned, [], new HashSet<ProcessIdentity> { profileBIdentity },
    new HashSet<ProcessIdentity>(), protectedBeforeBSession, false, true, 1, () => { earlyTerminateCalls++; return true; }) && earlyTerminateCalls == 0,
    "adding B configured game EXE matching running A-owned companion blocks cleanup before B session exists");
Check(!ProfileOrchestration.MayPlaceOwnedCompanionWindow(profileAOwned, profileBIdentity, profileAOwned.Executable,
    new HashSet<ProcessIdentity>(), protectedBeforeBSession, false),
    "newly configured B game EXE blocks shared companion movement before B session exists");

var manualExe = @"C:\Apps\editor.exe";
var manualIdentity = new ProcessIdentity(910, 91_000);
var manualHwnd = new IntPtr(0x910);
var moveCalls = 0;
var showCalls = 0;
var activateCalls = 0;
void RunManualActivationCandidate(string? path, ProcessIdentity? identity, ManualWindowObservation? current, string caseName)
{
    if (ManualRelatedAppPolicy.TryCreateCandidate(910, path, identity, manualHwnd, manualExe, out var candidate, out _)
        && candidate is not null)
    {
        if (ManualRelatedAppPolicy.TryInvokeAction(candidate, manualExe, _ => current, _ => moveCalls++, out _)
            && ManualRelatedAppPolicy.TryInvokeAction(candidate, manualExe, _ => current, _ => showCalls++, out _))
            ManualRelatedAppPolicy.TryInvokeAction(candidate, manualExe, _ => current, _ => activateCalls++, out _);
    }
    Check(moveCalls == 0 && showCalls == 0 && activateCalls == 0, $"manual activation {caseName} performs no move/show/activate callback");
}
RunManualActivationCandidate(null, manualIdentity, new(manualHwnd, 910, manualIdentity, manualExe), "inaccessible executable path");
RunManualActivationCandidate(@"C:\Other\editor.exe", manualIdentity, new(manualHwnd, 910, manualIdentity, manualExe), "same-name wrong executable path");
RunManualActivationCandidate(manualExe, manualIdentity, new(manualHwnd, 911, new ProcessIdentity(911, 91_100), manualExe), "HWND owner PID changed");
RunManualActivationCandidate(manualExe, manualIdentity, new(manualHwnd, 910, new ProcessIdentity(910, manualIdentity.CreationFileTime + 1), manualExe), "reused PID with new process identity");
var validReads = 0;
Check(ManualRelatedAppPolicy.TryCreateCandidate(910, manualExe, manualIdentity, manualHwnd, manualExe, out var validCandidate, out _)
    && validCandidate is not null, "manual activation candidate requires confirmed configured path and process identity");
if (validCandidate is not null)
{
    ManualWindowObservation? ValidOwner(IntPtr hwnd) { validReads++; return new(hwnd, 910, manualIdentity, manualExe); }
    Check(ManualRelatedAppPolicy.TryInvokeAction(validCandidate, manualExe, ValidOwner, _ => moveCalls++, out _)
        && ManualRelatedAppPolicy.TryInvokeAction(validCandidate, manualExe, ValidOwner, _ => showCalls++, out _)
        && ManualRelatedAppPolicy.TryInvokeAction(validCandidate, manualExe, ValidOwner, _ => activateCalls++, out _)
        && moveCalls == 1 && showCalls == 1 && activateCalls == 1 && validReads == 3,
        "confirmed manual activation revalidates HWND owner identity/path immediately before all three callbacks");
}

var treeKeys = new[] { "profile:A", "category:auto:A", "category:related:A", "related:A:notes" };
var firstTreeSnapshot = StableTreeSnapshot.Capture(
    treeKeys.Select(key => new TreeNodeSnapshot(key, key is "profile:A" or "category:auto:A" or "category:related:A")), "category:related:A");
var firstPollExpansion = treeKeys.Where(firstTreeSnapshot.IsExpanded).ToArray();
var firstPollSelection = firstTreeSnapshot.ResolveSelection(treeKeys);
var secondTreeSnapshot = StableTreeSnapshot.Capture(
    treeKeys.Select(key => new TreeNodeSnapshot(key, firstPollExpansion.Contains(key))), firstPollSelection);
Check(secondTreeSnapshot.IsExpanded("category:auto:A") && secondTreeSnapshot.IsExpanded("category:related:A")
    && secondTreeSnapshot.ResolveSelection(treeKeys) == "category:related:A",
    "category expansion and category selection survive consecutive tree rebuild/poll cycles");
var relatedAppSelection = StableTreeSnapshot.Capture(
    treeKeys.Select(key => new TreeNodeSnapshot(key, secondTreeSnapshot.IsExpanded(key))), "related:A:notes");
var thirdTreeSnapshot = StableTreeSnapshot.Capture(
    treeKeys.Select(key => new TreeNodeSnapshot(key, relatedAppSelection.IsExpanded(key))),
    relatedAppSelection.ResolveSelection(treeKeys));
Check(thirdTreeSnapshot.IsExpanded("category:auto:A") && thirdTreeSnapshot.IsExpanded("category:related:A")
    && thirdTreeSnapshot.ResolveSelection(treeKeys) == "related:A:notes",
    "related-app selection and expanded category survive repeated tree rebuild/poll cycles");

Check(!ProfileOrchestration.ShouldSwitchDesktop(true, false, true, false), "failed window placement prevents desktop auto-switch");
Check(ProfileOrchestration.ShouldSwitchDesktop(true, false, true, true), "successful window placement permits requested desktop switch");

var compatibility = new (int Build, int Ubr, VirtualDesktopApiKind Expected)[]
{
    (19041, 0, VirtualDesktopApiKind.Windows10), (19045, 0, VirtualDesktopApiKind.Windows10),
    (19046, 0, VirtualDesktopApiKind.Unsupported), (22631, 0, VirtualDesktopApiKind.Unsupported),
    (26100, 2604, VirtualDesktopApiKind.Unsupported), (26100, 2605, VirtualDesktopApiKind.Windows11_24H2),
    (26200, 8116, VirtualDesktopApiKind.Unsupported), (26200, 8117, VirtualDesktopApiKind.Windows11_25H2),
    (27000, 9999, VirtualDesktopApiKind.Unsupported)
};
foreach (var (build, ubr, expected) in compatibility)
    Check(VirtualDesktopCompatibility.Select(10, 0, build, ubr) == expected, $"build {build}.{ubr} compatibility guard");
Check(VirtualDesktopCompatibility.Select(11, 0, 26100, 9000) == VirtualDesktopApiKind.Unsupported, "unknown OS version is rejected");

var missingRoot = Path.Combine(Path.GetTempPath(), "GridShiftProfileChecks", Guid.NewGuid().ToString("N"));
try
{
    var store = new ProfileStore(Path.Combine(missingRoot, "nested", "profiles.json"));
    Check(store.Load().Count == 0, "missing profile directory and file load as empty store");
    var persistedCompanion = new Companion { Id = "persisted-consent", Executable = @"C:\Apps\companion.exe", StopWhenGameEnds = true };
    store.Save([new GameProfile { Name = "Regression", Companions = [persistedCompanion], RelatedApps = [new RelatedApp { Name = "Notes", Executable = @"C:\Apps\notes.exe", Arguments = "--standalone" }] }]);
    var loaded = store.Load().Single();
    Check(loaded.Name == "Regression" && loaded.RelatedApps.Single().Name == "Notes", "profile store creates directory and round-trips manual related-app configuration");
    var legacyPath = Path.Combine(missingRoot, "legacy.json");
    File.WriteAllText(legacyPath, "[{\"Name\":\"Legacy\",\"Companions\":[{\"Executable\":\"old.exe\"}]}]");
    var loadedLegacy = new ProfileStore(legacyPath).Load().Single().Companions.Single();
    Check(loadedLegacy.Autostart && !loadedLegacy.StopWhenGameEnds && !loadedLegacy.ForceTerminateAfterGrace,
        "profile load preserves legacy missing normal-stop as false and never upgrades force consent");
    Check(loaded.Companions.Single().Id == "persisted-consent" && !loaded.Companions.Single().ForceTerminateAfterGrace,
        "profile store round-trips stable companion consent identity without enabling force");
    Check(loaded.Companions.Single().Id == persistedCompanion.Id && !loaded.RelatedApps.Single().GetType().Equals(typeof(Companion)), "manual related-app model remains separate from automatic companion ownership model");
}
finally { if (Directory.Exists(missingRoot)) Directory.Delete(missingRoot, true); }

var ownershipPath = Path.Combine(Path.GetTempPath(), "GridShiftDesktopOwnership", Guid.NewGuid().ToString("N"), "owned.json");
var ownershipId = Guid.NewGuid();
var ownership = new DesktopOwnershipStore(ownershipPath);
Check(!ownership.IsCreatedByLauncher("game", ownershipId), "desktop ownership ledger begins fail-closed for unknown desktop");
ownership.MarkCreated("game", ownershipId);
Check(new DesktopOwnershipStore(ownershipPath).IsCreatedByLauncher("game", ownershipId), "desktop ownership ledger persists launcher-created provenance by profile and GUID");
ownership.Forget("game", ownershipId);
Check(!new DesktopOwnershipStore(ownershipPath).IsCreatedByLauncher("game", ownershipId), "removed desktop provenance is forgotten safely");
Directory.Delete(Path.GetDirectoryName(ownershipPath)!, true);

var launchPollCleanupRoot = Path.Combine(Path.GetTempPath(), "GridShiftDesktopLifecycle", Guid.NewGuid().ToString("N"));
var launchPollOwnership = new DesktopOwnershipStore(Path.Combine(launchPollCleanupRoot, "owned.json"));
var launchPollFallback = Guid.NewGuid();
var launchPollDesktop = Guid.NewGuid();
var launchPollApi = new FakeDesktopApi { Existing = [launchPollFallback], NewDesktop = launchPollDesktop, CurrentDesktop = launchPollFallback };
var launchPollCoordinator = new VirtualDesktopCoordinator(() => launchPollApi);
var launchPollLifecycle = new ProfileDesktopLifecycle(launchPollCoordinator, launchPollOwnership);
var launchPollProfile = new GameProfile { Id = "launch-poll-cleanup", UseVirtualDesktop = true, CleanupCreatedDesktop = true };
var launchedDesktop = launchPollLifecycle.Ensure(launchPollProfile);
var polledDesktop = launchPollLifecycle.Ensure(launchPollProfile);
Check(launchedDesktop.Created && launchedDesktop.DesktopId == launchPollDesktop
    && !polledDesktop.Created && launchPollLifecycle.IsCreatedByLauncher(launchPollProfile.Id, launchPollDesktop),
    "game-launch Ensure registers created desktop provenance and later Poll Ensure reuses it");
var launchPollQueue = new PendingDesktopCleanupQueue();
launchPollQueue.Enqueue(launchPollProfile.Id, launchPollDesktop);
Check(PendingDesktopCleanupPolicy.Evaluate(true, true, true, launchPollLifecycle.IsCreatedByLauncher(launchPollProfile.Id, launchPollDesktop),
    false, true, false, true, false) == PendingDesktopCleanupReadiness.Attempt
    && launchPollCoordinator.RemoveCreatedDesktop(launchPollDesktop, launchPollLifecycle.IsCreatedByLauncher(launchPollProfile.Id, launchPollDesktop),
        false, () => [], IntPtr.Zero) is null,
    "launch→poll→owned empty desktop cleanup uses the same production lifecycle/ownership gate");
launchPollLifecycle.Forget(launchPollProfile.Id, launchPollDesktop);
launchPollQueue.Remove(launchPollProfile.Id);
Directory.Delete(launchPollCleanupRoot, true);

var desktopId = Guid.NewGuid();
var existingDesktopId = Guid.NewGuid();
var fake = new FakeDesktopApi { Existing = [existingDesktopId], NewDesktop = desktopId };
var coordinator = new VirtualDesktopCoordinator(() => fake);
var profile = new GameProfile { DesktopId = Guid.NewGuid().ToString("D") };
var ensured = coordinator.Ensure(profile);
Check(ensured.DesktopId == desktopId && ensured.Created && profile.DesktopId == desktopId.ToString("D") && fake.CreateCalls == 1,
    "missing saved desktop creates and persists its profile target; coordinator reports newly created ownership");
profile.DesktopId = existingDesktopId.ToString("D");
Check(coordinator.Ensure(profile).DesktopId == existingDesktopId && fake.CreateCalls == 1, "saved desktop is reused without creation");
var unsupportedCoordinator = new VirtualDesktopCoordinator(() => null);
var unsupported = unsupportedCoordinator.Ensure(new GameProfile());
Check(unsupported.DesktopId is null && unsupported.Warning is not null, "unsupported desktop API returns visible fallback warning");
Check(unsupportedCoordinator.MoveWindowToCurrentDesktop(new IntPtr(10)) is not null, "tray window current-desktop move reports unsupported API fallback");
var emptyDesktopDecision = DesktopCleanupPolicy.Evaluate(desktopId, true, false, [existingDesktopId, desktopId], true, [], existingDesktopId);
Check(emptyDesktopDecision.Allowed && emptyDesktopDecision.FallbackDesktop == existingDesktopId && !emptyDesktopDecision.MustSwitchBeforeRemoval,
    "positively launcher-created empty unshared desktop has verified retained fallback");
Check(!DesktopCleanupPolicy.Evaluate(desktopId, false, false, [existingDesktopId, desktopId], true, [], existingDesktopId).Allowed,
    "preexisting or unknown-origin desktop is never removed");
Check(!DesktopCleanupPolicy.Evaluate(desktopId, true, true, [existingDesktopId, desktopId], true, [], existingDesktopId).Allowed,
    "desktop shared by another active profile is retained");
Check(!DesktopCleanupPolicy.Evaluate(desktopId, true, false, [existingDesktopId, desktopId], false, [], existingDesktopId).Allowed,
    "incomplete all-desktop window enumeration blocks removal");
Check(!DesktopCleanupPolicy.Evaluate(desktopId, true, false, [existingDesktopId, desktopId], true, [Guid.NewGuid()], existingDesktopId).Allowed,
    "window that maps to an unknown desktop blocks removal");
Check(!DesktopCleanupPolicy.Evaluate(desktopId, true, false, [existingDesktopId, desktopId], true, [desktopId], existingDesktopId).Allowed,
    "foreign or launcher window on target desktop blocks removal without moving it");
Check(!DesktopCleanupPolicy.Evaluate(desktopId, true, false, [desktopId], true, [], desktopId).Allowed,
    "removing the last desktop without safe return is blocked");
Check(!DesktopCleanupPolicy.Evaluate(desktopId, true, false, [existingDesktopId, desktopId], true, [], Guid.NewGuid()).Allowed,
    "unknown current desktop blocks fallback selection");
var activeRemoval = DesktopCleanupPolicy.Evaluate(desktopId, true, false, [existingDesktopId, desktopId], true, [], desktopId);
Check(activeRemoval.Allowed && activeRemoval.MustSwitchBeforeRemoval && activeRemoval.FallbackDesktop == existingDesktopId,
    "currently active empty desktop requires a verified switch to retained fallback first");
fake.PinnedWindows.Add(new IntPtr(3));
var moveWarning = coordinator.PlaceWindows(desktopId, [new IntPtr(1), new IntPtr(2), new IntPtr(3)], hwnd => hwnd != new IntPtr(2));
Check(moveWarning is null && fake.MovedWindows.SequenceEqual([new IntPtr(1)]), "window placement honors shared-profile and pinned-window protections");
fake.CurrentDesktop = existingDesktopId;
Check(coordinator.MoveWindowToCurrentDesktop(new IntPtr(44)) is null && fake.WindowDesktops.GetValueOrDefault(new IntPtr(44)) == existingDesktopId,
    "tray access moves an existing launcher window onto the current desktop through backend");
fake.PinnedWindows.Add(new IntPtr(45));
Check(coordinator.MoveWindowToCurrentDesktop(new IntPtr(45)) is null && !fake.WindowDesktops.ContainsKey(new IntPtr(45)),
    "tray restoration leaves a launcher window already pinned across desktops unchanged");
Check(coordinator.SwitchTo(desktopId) is null && fake.Switched == desktopId, "existing desktop switches through coordinator");
var launcherWindow = new IntPtr(77);
fake.WindowDesktops[launcherWindow] = desktopId;
Check(coordinator.RemoveCreatedDesktop(desktopId, true, false, () => [launcherWindow], launcherWindow) is null
    && !fake.Existing.Contains(desktopId) && fake.CurrentDesktop == existingDesktopId
    && fake.WindowDesktops[launcherWindow] == existingDesktopId,
    "coordinator moves only its own launcher window, returns safely, then verifies empty desktop removal");
var switchedBackApi = new FakeDesktopApi { Existing = [existingDesktopId, desktopId], CurrentDesktop = desktopId };
switchedBackApi.CurrentReadHook = count => { if (count == 2) switchedBackApi.CurrentDesktop = desktopId; };
var switchedBackCoordinator = new VirtualDesktopCoordinator(() => switchedBackApi);
Check(switchedBackCoordinator.RemoveCreatedDesktop(desktopId, true, false, () => [], IntPtr.Zero) is not null
    && switchedBackApi.Existing.Contains(desktopId) && switchedBackApi.RemoveCalls == 0,
    "fresh final current-desktop check blocks removal if the user switches back during final enumeration");
var failedFinalCurrentApi = new FakeDesktopApi { Existing = [existingDesktopId, desktopId], CurrentDesktop = desktopId };
failedFinalCurrentApi.CurrentReadHook = count => { if (count == 2) throw new InvalidOperationException("current desktop query failed"); };
var failedFinalCurrentCoordinator = new VirtualDesktopCoordinator(() => failedFinalCurrentApi);
Check(failedFinalCurrentCoordinator.RemoveCreatedDesktop(desktopId, true, false, () => [], IntPtr.Zero) is not null
    && failedFinalCurrentApi.Existing.Contains(desktopId) && failedFinalCurrentApi.RemoveCalls == 0,
    "final current-desktop query failure fails closed without deleting");
Check(coordinator.SwitchTo(Guid.NewGuid()) is not null, "missing desktop returns visible warning");
var foreignDesktopFake = new FakeDesktopApi { Existing = [existingDesktopId, desktopId], CurrentDesktop = existingDesktopId };
var foreignWindow = new IntPtr(88);
foreignDesktopFake.WindowDesktops[foreignWindow] = desktopId;
var foreignCoordinator = new VirtualDesktopCoordinator(() => foreignDesktopFake);
Check(foreignCoordinator.RemoveCreatedDesktop(desktopId, true, false, () => [foreignWindow], IntPtr.Zero) is not null
    && foreignDesktopFake.Existing.Contains(desktopId) && foreignDesktopFake.WindowDesktops[foreignWindow] == desktopId,
    "coordinator never moves or removes a desktop containing a foreign window");
var retryDesktop = Guid.NewGuid();
var retryFallback = Guid.NewGuid();
var retryApi = new FakeDesktopApi { Existing = [retryFallback, retryDesktop], CurrentDesktop = retryFallback };
var retryCoordinator = new VirtualDesktopCoordinator(() => retryApi);
var retryQueue = new PendingDesktopCleanupQueue();
var retryIdentity = new ProcessIdentity(5050, 123456);
var retryProfile = new GameProfile { Id = "retry-profile", DesktopId = retryDesktop.ToString("D"), CleanupCreatedDesktop = true };
retryQueue.Enqueue(retryProfile.Id, retryDesktop, [retryIdentity]);
var closePostProgress = new CompanionCleanupProgress(now);
var closePostIsWaiting = closePostProgress.CloseRequested
    && ProcessSafety.NextCleanupStep(closePostProgress, now.AddSeconds(1), TimeSpan.FromSeconds(10), true, false, false, false, false) == CleanupStep.Wait;
Check(closePostIsWaiting, "after the guarded WM_CLOSE post, the owned companion remains in its nonblocking wait state");
var whileClosePending = PendingDesktopCleanupPolicy.Evaluate(true, true, true, true, false, true, true, false, false);
Check(whileClosePending == PendingDesktopCleanupReadiness.Wait && retryApi.RemoveCalls == 0,
    "desktop cleanup waits while the close-posted owned companion remains open");
var stillRunningSnapshot = new ProcessTreeEntry[] { new(retryIdentity.ProcessId, 1, "Companion.exe", retryIdentity) };
Check(!PendingDesktopCleanupPolicy.IsOwnedCompanionFamilyComplete([retryIdentity], stillRunningSnapshot, out var familyUncertain)
    && !familyUncertain, "owned companion process identity remains a retry blocker after its window receives close");
retryApi.WindowDesktops[new IntPtr(505)] = retryDesktop;
var afterProcessExit = PendingDesktopCleanupPolicy.Evaluate(true, true, true, true, false, true, false,
    PendingDesktopCleanupPolicy.IsOwnedCompanionFamilyComplete([retryIdentity], [], out _), false);
Check(afterProcessExit == PendingDesktopCleanupReadiness.Attempt
    && retryCoordinator.RemoveCreatedDesktop(retryDesktop, true, false, () => [new IntPtr(505)], IntPtr.Zero) is not null
    && retryQueue.Items.Count == 1 && retryApi.Existing.Contains(retryDesktop),
    "after companion process exit cleanup retries, but a lingering desktop window keeps the queued desktop");
retryApi.WindowDesktops.Remove(new IntPtr(505));
Check(retryCoordinator.RemoveCreatedDesktop(retryDesktop, true, false, () => [], IntPtr.Zero) is null,
    "after the lingering window closes, queued cleanup can remove the still-owned empty desktop");
retryQueue.Remove(retryProfile.Id);
Check(retryQueue.Items.Count == 0 && retryApi.RemoveCalls == 1,
    "completed desktop retry is removed from pending state exactly once");
Check(PendingDesktopCleanupPolicy.Evaluate(true, true, true, true, true, true, false, true, false)
    == PendingDesktopCleanupReadiness.Cancel,
    "reactivated game session cancels pending desktop cleanup");
Check(PendingDesktopCleanupPolicy.Evaluate(true, false, true, true, false, true, false, true, false)
    == PendingDesktopCleanupReadiness.Cancel,
    "revoked cleanup consent cancels pending desktop cleanup");
Check(PendingDesktopCleanupPolicy.Evaluate(true, true, false, true, false, true, false, true, false)
    == PendingDesktopCleanupReadiness.Cancel
    && PendingDesktopCleanupPolicy.Evaluate(true, true, true, false, false, true, false, true, false) == PendingDesktopCleanupReadiness.Cancel,
    "changed profile desktop target or lost launcher-creation provenance cancels pending desktop cleanup");
Check(PendingDesktopCleanupPolicy.Evaluate(true, true, true, true, false, false, false, false, false)
    == PendingDesktopCleanupReadiness.Wait,
    "unknown process snapshot blocks pending desktop cleanup");
Check(PendingDesktopCleanupPolicy.Evaluate(true, true, true, true, false, true, false, true, true)
    == PendingDesktopCleanupReadiness.Wait,
    "shared active profile defers rather than removes pending desktop cleanup");
Check(!PendingDesktopCleanupPolicy.IsOwnedCompanionFamilyComplete([retryIdentity],
    [new(retryIdentity.ProcessId, 1, "Companion.exe", null)], out familyUncertain) && familyUncertain,
    "uncertain owned companion process identity remains a cleanup blocker");
var failing = new VirtualDesktopCoordinator(() => throw new InvalidOperationException("COM unavailable"));
Check(failing.Ensure(new GameProfile()).Warning?.Contains("COM unavailable", StringComparison.Ordinal) == true, "COM failure is reported instead of silently ignored");

Console.WriteLine($"{checks.Length} process safety policy checks; process-tree, storage, desktop coordinator, and build-guard regressions passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

sealed class FakeDesktopApi : IVirtualDesktopApi
{
    public IReadOnlyList<Guid> Existing { get; set; } = [];
    public Guid NewDesktop { get; set; }
    public Guid CurrentDesktop { get; set; }
    public int CreateCalls { get; private set; }
    public List<IntPtr> MovedWindows { get; } = [];
    public Dictionary<IntPtr, Guid> WindowDesktops { get; } = [];
    public HashSet<IntPtr> PinnedWindows { get; } = [];
    public Guid? Switched { get; private set; }
    public int RemoveCalls { get; private set; }
    public int CurrentReadCalls { get; private set; }
    public Action<int>? CurrentReadHook { get; set; }
    public IReadOnlyList<Guid> GetDesktops() => Existing;
    public Guid CreateDesktop() { CreateCalls++; Existing = Existing.Append(NewDesktop).ToArray(); return NewDesktop; }
    public Guid GetCurrentDesktop() { CurrentReadHook?.Invoke(++CurrentReadCalls); return CurrentDesktop; }
    public void SwitchTo(Guid desktopId) { Switched = desktopId; CurrentDesktop = desktopId; }
    public void MoveWindowToDesktop(IntPtr window, Guid desktopId) { MovedWindows.Add(window); WindowDesktops[window] = desktopId; }
    public Guid GetWindowDesktop(IntPtr window) => WindowDesktops.GetValueOrDefault(window);
    public bool IsWindowPinned(IntPtr window) => PinnedWindows.Contains(window);
    public void RemoveDesktop(Guid desktopId, Guid fallbackDesktopId)
    {
        RemoveCalls++;
        if (!Existing.Contains(fallbackDesktopId) || desktopId == fallbackDesktopId) throw new InvalidOperationException("unsafe fallback");
        Existing = Existing.Where(id => id != desktopId).ToArray();
        if (CurrentDesktop == desktopId) CurrentDesktop = fallbackDesktopId;
    }
}
