using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GridShift;

public sealed class MainForm : Form
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr extraData);

    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private static readonly TimeSpan CompanionCloseGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartStability = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(20);
    private readonly ProfileStore store = new();
    private readonly VirtualDesktopCoordinator desktops = new();
    private readonly List<GameProfile> profiles;
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 56, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly Dictionary<string, ProfileRuntimeState> sessions = new();
    private readonly Dictionary<string, List<OwnedProcess>> ownedCompanions = new();
    private readonly Dictionary<ProcessIdentity, HashSet<ProcessIdentity>> ownedCompanionFamilies = new();
    private readonly Dictionary<ProcessIdentity, PendingCompanionCleanup> pendingCompanionCleanups = new();
    private readonly NotifyIcon tray;
    private bool quitting;
    private string statusMessage = "";
    private string? persistentDesktopWarning;
    private string? persistentManualActivationWarning;

    public MainForm()
    {
        Text = "GridShift — 게임·관련 앱 런처";
        Width = 1000; Height = 650; MinimumSize = new Size(720, 420);
        Font = new Font("맑은 고딕", 9F);
        profiles = store.Load();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6), WrapContents = false, AutoScroll = true };
        AddButton(bar, "게임 추가", AddProfile);
        AddButton(bar, "프로필 수정", EditProfile);
        AddButton(bar, "실행", LaunchSelected);
        AddButton(bar, "자동 동반 설정", EditCompanions);
        AddButton(bar, "수동 관련 앱", EditRelatedApps);
        AddButton(bar, "관련 앱 실행", LaunchSelectedRelatedApp);
        AddButton(bar, "관련 앱 활성화", ActivateSelectedRelatedApp);
        AddButton(bar, "설치 앱 검색", SearchApps);
        AddButton(bar, "그룹 창 활성화", ActivateSelectedGroup);
        AddButton(bar, "삭제", DeleteSelected);
        Controls.Add(tree); Controls.Add(bar); Controls.Add(status);
        RefreshTree();

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => PopulateTrayMenu(menu);
        menu.Items.Add("종료", null, (_, _) => { quitting = true; tray!.Visible = false; Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "GridShift", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => RestoreWindow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } else { timer.Stop(); tray.Visible = false; } };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    private void PopulateTrayMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add("GridShift 관리 창 열기", null, (_, _) => RestoreWindow());
        if (profiles.Count > 0) menu.Items.Add(new ToolStripSeparator());
        foreach (var profile in profiles)
        {
            var profileMenu = new ToolStripMenuItem(profile.Name);
            profileMenu.DropDownItems.Add("게임 실행", null, (_, _) => LaunchProfile(profile));
            if (profile.RelatedApps is { Count: > 0 })
            {
                var relatedMenu = new ToolStripMenuItem("수동 관련 앱 — 게임과 독립 실행");
                foreach (var app in profile.RelatedApps)
                {
                    var appMenu = new ToolStripMenuItem(app.Name);
                    appMenu.DropDownItems.Add("실행", null, (_, _) => LaunchRelatedApp(app));
                    appMenu.DropDownItems.Add("실행 중인 창 활성화", null, (_, _) => ActivateRelatedApp(app));
                    relatedMenu.DropDownItems.Add(appMenu);
                }
                profileMenu.DropDownItems.Add(relatedMenu);
            }
            menu.Items.Add(profileMenu);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("GridShift 종료", null, (_, _) => { quitting = true; tray.Visible = false; Close(); });
    }

    private static void AddButton(Control parent, string title, Action action)
    {
        var button = new Button { Text = title, AutoSize = true, Height = 29, Margin = new Padding(3) };
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    private void RestoreWindow()
    {
        if (IsHandleCreated && desktops.MoveWindowToCurrentDesktop(Handle) is string warning)
            SetDesktopWarning(warning);
        Show(); WindowState = FormWindowState.Normal; Activate();
    }

    private GameProfile? SelectedProfile()
    {
        var node = tree.SelectedNode;
        while (node is not null && node.Tag is not GameProfile) node = node.Parent;
        return node?.Tag as GameProfile;
    }

    private RelatedApp? SelectedRelatedApp() => tree.SelectedNode?.Tag as RelatedApp;

    private void RefreshTree()
    {
        var selectedProfileKey = SelectedProfile() is GameProfile selectedProfile ? ProfileNodeKey(selectedProfile.Id) : null;
        var snapshot = StableTreeSnapshot.Capture(
            EnumerateTreeNodes(tree.Nodes).Select(node => new TreeNodeSnapshot(node.Name, node.IsExpanded)),
            tree.SelectedNode?.Name);
        tree.BeginUpdate();
        tree.Nodes.Clear();
        foreach (var profile in profiles)
        {
            var matches = FindProcesses(profile.Executable);
            var state = sessions.TryGetValue(profile.Id, out var liveSession) && liveSession.ActiveFamilyProcessCount > 0
                ? $"실행 중 ({liveSession.ActiveFamilyProcessCount}개 프로세스)"
                : matches.Any(x => x.Path is not null) ? "실행 중" : matches.Count > 0 ? "확인 필요" : sessions.ContainsKey(profile.Id) ? "종료 확인 중" : "대기";
            foreach (var process in matches) process.Dispose();
            var root = new TreeNode($"{profile.Name}  ·  게임 {state}") { Name = ProfileNodeKey(profile.Id), Tag = profile };
            var automatic = new TreeNode("자동 동반 앱 (게임 실행 시)") { Name = AutomaticCategoryNodeKey(profile.Id) };
            for (var index = 0; index < profile.Companions.Count; index++)
            {
                var companion = profile.Companions[index];
                var processes = FindProcesses(companion.Executable);
                var ownership = IsOwned(profile.Id, companion.Executable) ? "GridShift 시작" : processes.Count > 0 ? "기존/공유" : "대기";
                var key = CompanionNodeKey(profile.Id, index, companion.Executable);
                automatic.Nodes.Add(new TreeNode($"{Path.GetFileName(companion.Executable)} · {ownership} · {(companion.StopWhenGameEnds ? "명시적 종료 선택" : "유지")}") { Name = key, Tag = companion });
                foreach (var process in processes) process.Dispose();
            }
            root.Nodes.Add(automatic);
            var related = new TreeNode("수동 관련 앱 (자동 실행/정리 안 함)") { Name = RelatedCategoryNodeKey(profile.Id) };
            foreach (var app in profile.RelatedApps ?? [])
            {
                var processes = FindProcesses(app.Executable);
                var appState = processes.Count > 0 ? "실행 중" : "대기";
                foreach (var process in processes) process.Dispose();
                related.Nodes.Add(new TreeNode($"{app.Name} · {appState}") { Name = RelatedAppNodeKey(profile.Id, app.Id), Tag = app });
            }
            root.Nodes.Add(related);
            tree.Nodes.Add(root);
        }
        var nodes = EnumerateTreeNodes(tree.Nodes).ToArray();
        foreach (var node in nodes)
            if (snapshot.IsExpanded(node.Name)) node.Expand();
        var selectedKey = snapshot.ResolveSelection(nodes.Select(node => node.Name), selectedProfileKey);
        tree.SelectedNode = selectedKey is null ? null : nodes.FirstOrDefault(node => node.Name == selectedKey);
        tree.EndUpdate();
    }

    private static IEnumerable<TreeNode> EnumerateTreeNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var descendant in EnumerateTreeNodes(node.Nodes)) yield return descendant;
        }
    }

    private static string ProfileNodeKey(string profileId) => $"profile:{profileId}";
    private static string AutomaticCategoryNodeKey(string profileId) => $"category:auto:{profileId}";
    private static string RelatedCategoryNodeKey(string profileId) => $"category:related:{profileId}";
    private static string CompanionNodeKey(string profileId, int index, string executable)
        => $"companion:{profileId}:{index}:{Path.GetFullPath(executable).ToUpperInvariant()}";
    private static string RelatedAppNodeKey(string profileId, string relatedAppId) => $"related:{profileId}:{relatedAppId}";

    private static List<ProcessSnapshot> FindProcesses(string executable, bool includeOtherPaths = false)
    {
        var result = new List<ProcessSnapshot>();
        if (string.IsNullOrWhiteSpace(executable)) return result;
        string expectedPath;
        try { expectedPath = Path.GetFullPath(executable); }
        catch (ArgumentException) { return result; }
        catch (NotSupportedException) { return result; }
        var expectedName = Path.GetFileNameWithoutExtension(expectedPath);
        foreach (var process in Process.GetProcessesByName(expectedName))
        {
            try
            {
                string? path = null;
                ProcessIdentity? identity = ProcessIdentityProbe.TryGetIdentity(process.Id, out var foundIdentity) ? foundIdentity : null;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                if (path is null || includeOtherPaths || string.Equals(Path.GetFullPath(path), expectedPath, StringComparison.OrdinalIgnoreCase))
                    result.Add(new ProcessSnapshot(process.Id, path, identity, process.MainWindowHandle));
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return result;
    }

    private void AddProfile()
    {
        using var dialog = new ProfileEditorDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Profile is null) return;
        profiles.Add(dialog.Profile);
        Save();
    }

    private void EditProfile()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        using var dialog = new ProfileEditorDialog(profile);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Profile is not null)
        {
            profile.Name = dialog.Profile.Name;
            profile.Executable = dialog.Profile.Executable;
            profile.Arguments = dialog.Profile.Arguments;
            profile.SteamUri = dialog.Profile.SteamUri;
            profile.UseVirtualDesktop = dialog.Profile.UseVirtualDesktop;
            profile.SwitchToVirtualDesktop = dialog.Profile.SwitchToVirtualDesktop;
            profile.RelatedApps = dialog.Profile.RelatedApps;
            Save();
        }
    }

    private void EditCompanions()
    {
        var profile = SelectedProfile();
        if (profile is null) { SetStatus("자동 동반 앱을 관리할 게임 프로필을 먼저 선택하세요."); return; }
        using var dialog = new CompanionEditorDialog(profile);
        if (dialog.ShowDialog(this) == DialogResult.OK) Save();
    }

    private void EditRelatedApps()
    {
        var profile = SelectedProfile();
        if (profile is null) { SetStatus("수동 관련 앱을 관리할 게임 프로필을 먼저 선택하세요."); return; }
        using var dialog = new RelatedAppEditorDialog(profile);
        if (dialog.ShowDialog(this) == DialogResult.OK) Save();
    }

    private void LaunchSelectedRelatedApp()
    {
        var app = SelectedRelatedApp();
        if (app is null) { SetStatus("수동 관련 앱 목록에서 실행할 앱을 선택하세요. 게임은 실행할 필요가 없습니다."); return; }
        LaunchRelatedApp(app);
    }

    private void LaunchRelatedApp(RelatedApp app)
    {
        if (!File.Exists(app.Executable)) { SetStatus("관련 앱 실행 파일 경로를 확인하세요."); return; }
        try
        {
            _ = Process.Start(new ProcessStartInfo(app.Executable, app.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(app.Executable)!
            });
            SetStatus($"'{app.Name}' 수동 실행을 요청했습니다. 이 앱은 자동 동반 앱으로 등록하거나 종료하지 않습니다.");
        }
        catch (Exception ex) { SetStatus($"관련 앱 실행 실패: {ex.Message}"); }
    }

    private void ActivateSelectedRelatedApp()
    {
        var app = SelectedRelatedApp();
        if (app is null) { SetStatus("수동 관련 앱 목록에서 활성화할 앱을 선택하세요. 게임은 실행할 필요가 없습니다."); return; }
        ActivateRelatedApp(app);
    }

    private void ActivateRelatedApp(RelatedApp app)
    {
        var processes = FindProcesses(app.Executable, includeOtherPaths: true);
        var skippedUncertain = false;
        var activated = false;
        try
        {
            foreach (var process in processes)
            {
                if (!ManualRelatedAppPolicy.TryCreateCandidate(process.ProcessId, process.Path, process.Identity,
                    process.MainWindowHandle, app.Executable, out var candidate, out var reason))
                {
                    skippedUncertain = true;
                    SetManualActivationWarning($"'{app.Name}' 활성화를 건너뛴 프로세스: {reason}");
                    continue;
                }
                if (candidate is null) continue;

                string? moveWarning = null;
                if (!InvokeManualWindowAction(candidate, app.Executable,
                    hwnd => moveWarning = desktops.MoveWindowToCurrentDesktop(hwnd), out reason))
                {
                    skippedUncertain = true;
                    SetManualActivationWarning($"'{app.Name}' 활성화를 중단했습니다: {reason}");
                    continue;
                }
                if (moveWarning is not null) SetDesktopWarning(moveWarning);

                if (!InvokeManualWindowAction(candidate, app.Executable,
                    hwnd => ShowWindow(hwnd, SwRestore), out reason))
                {
                    skippedUncertain = true;
                    SetManualActivationWarning($"'{app.Name}' 창 표시를 중단했습니다: {reason}");
                    continue;
                }
                var foreground = false;
                if (!InvokeManualWindowAction(candidate, app.Executable,
                    hwnd => foreground = SetForegroundWindow(hwnd), out reason))
                {
                    skippedUncertain = true;
                    SetManualActivationWarning($"'{app.Name}' 창 활성화를 중단했습니다: {reason}");
                    continue;
                }
                if (!foreground)
                {
                    SetManualActivationWarning($"'{app.Name}' 창 소유권은 확인했지만 Windows가 foreground 활성화를 허용하지 않았습니다.");
                    continue;
                }
                activated = true;
                break;
            }
            if (activated)
            {
                if (!skippedUncertain) persistentManualActivationWarning = null;
                SetStatus($"'{app.Name}' 창을 활성화했습니다.");
            }
            else if (processes.Count == 0)
                SetStatus($"'{app.Name}'의 실행 중인 창을 찾지 못했습니다. 먼저 수동 실행을 시도하세요.");
            else if (!skippedUncertain)
                SetManualActivationWarning($"'{app.Name}'에서 확인된 foreground 창이 없습니다.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private bool InvokeManualWindowAction(ManualWindowCandidate candidate, string configuredExecutable, Action<IntPtr> action, out string reason)
        => ManualRelatedAppPolicy.TryInvokeAction(candidate, configuredExecutable, ObserveManualWindowOwner, action, out reason);

    private static ManualWindowObservation? ObserveManualWindowOwner(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId > int.MaxValue) return null;
        var pid = checked((int)processId);
        return ProcessIdentityProbe.TryRead(pid, out var identity, out var executable)
            ? new ManualWindowObservation(hwnd, pid, identity, executable)
            : null;
    }

    private void SearchApps()
    {
        var apps = AppCatalog.Discover().ToList();
        var targetProfile = SelectedProfile();
        using var dialog = new Form { Text = "설치된 앱 검색 및 선택", Width = 720, Height = 500, StartPosition = FormStartPosition.CenterParent };
        var query = new TextBox { Dock = DockStyle.Top, PlaceholderText = "앱 이름 검색" };
        var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(5) };
        var addGame = new Button { Text = "새 게임 프로필로 추가", AutoSize = true };
        var addRelated = new Button { Text = "선택 프로필의 수동 관련 앱으로 추가", AutoSize = true, Enabled = targetProfile is not null };
        actions.Controls.Add(addGame); actions.Controls.Add(addRelated);
        void Populate()
        {
            list.Items.Clear();
            foreach (var app in apps.Where(x => x.Name.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)).Take(1500))
                list.Items.Add(new CatalogItem(app.Name, app.Path));
        }
        query.TextChanged += (_, _) => Populate();
        addGame.Click += (_, _) =>
        {
            if (list.SelectedItem is not CatalogItem item) return;
            profiles.Add(new GameProfile { Name = item.Name, Executable = item.Path });
            Save(); dialog.Close();
        };
        addRelated.Click += (_, _) =>
        {
            if (list.SelectedItem is not CatalogItem item || targetProfile is null) return;
            CatalogSelectionPolicy.AddToRelatedApps(targetProfile, item.Name, item.Path);
            Save(); dialog.Close();
        };
        list.DoubleClick += (_, _) => { if (addRelated.Enabled) addRelated.PerformClick(); else addGame.PerformClick(); };
        dialog.Controls.Add(list); dialog.Controls.Add(query); dialog.Controls.Add(actions); Populate(); dialog.ShowDialog(this);
    }

    private void DeleteSelected()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        if (MessageBox.Show(this, $"'{profile.Name}' 프로필을 삭제할까요? 실행 중인 게임이나 동반 앱은 종료하지 않습니다.", "프로필 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        profiles.Remove(profile);
        Save();
    }

    private void LaunchSelected()
    {
        var profile = SelectedProfile();
        if (profile is null) { SetStatus("실행할 게임 프로필을 선택하세요."); return; }
        LaunchProfile(profile);
    }

    private void LaunchProfile(GameProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Executable) || !File.Exists(profile.Executable))
        { SetStatus("게임 실행 파일 경로를 확인하세요."); return; }
        if (!string.IsNullOrWhiteSpace(profile.SteamUri) && !IsAllowedSteamUri(profile.SteamUri))
        { SetStatus("Steam URI는 steam://run/... 또는 steam://rungameid/... 형식이어야 합니다."); return; }

        if (profile.UseVirtualDesktop)
        {
            var desktop = desktops.Ensure(profile);
            if (desktop.Warning is not null) SetDesktopWarning(desktop.Warning);
            Save();
        }
        try
        {
            var startInfo = string.IsNullOrWhiteSpace(profile.SteamUri)
                ? new ProcessStartInfo(profile.Executable, profile.Arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(profile.Executable)! }
                : new ProcessStartInfo(profile.SteamUri) { UseShellExecute = true };
            _ = Process.Start(startInfo);
            SetStatus($"'{profile.Name}' 실행 요청을 보냈습니다. 게임 프로세스가 확인되면 동반 앱을 점검합니다.");
        }
        catch (Exception ex) { SetStatus($"게임 실행 실패: {ex.Message}"); return; }
        Poll();
    }

    private static bool IsAllowedSteamUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Host.Equals("run", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("rungameid", StringComparison.OrdinalIgnoreCase);
    }

    private void ActivateSelectedGroup()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        var handles = GetGroupWindows(profile).ToList();
        foreach (var window in handles)
        {
            ShowWindow(window, SwRestore);
            if (SetForegroundWindow(window)) return;
        }
        SetStatus("활성화할 게임/동반 앱 창이 없습니다. 게임이 실행 중인지 확인하세요.");
    }

    private IEnumerable<IntPtr> GetGroupWindows(GameProfile profile)
    {
        var knownRoots = new HashSet<ProcessIdentity>();
        var permitted = new Dictionary<int, ProcessIdentity>();
        foreach (var snapshot in FindProcesses(profile.Executable))
        {
            if (snapshot.Path is not null && snapshot.Identity is ProcessIdentity identity)
            {
                knownRoots.Add(identity);
                permitted[identity.ProcessId] = identity;
            }
            snapshot.Dispose();
        }
        if (sessions.TryGetValue(profile.Id, out var session))
        {
            knownRoots.UnionWith(session.ConfirmedGameRoots);
            knownRoots.UnionWith(session.KnownGameProcesses);
        }
        IReadOnlyList<ProcessTreeEntry> processTree;
        try { processTree = ProcessTreeProbe.ReadSnapshot(); }
        catch { processTree = []; }
        var family = ProcessTreePolicy.Expand(processTree, knownRoots);
        if (sessions.TryGetValue(profile.Id, out session)) session.KnownGameProcesses.UnionWith(family);
        foreach (var entry in processTree)
            if (entry.Identity is ProcessIdentity identity && family.Contains(identity)) permitted[identity.ProcessId] = identity;
        foreach (var companion in profile.Companions)
            foreach (var snapshot in FindProcesses(companion.Executable))
            {
                if (snapshot.Path is not null && snapshot.Identity is ProcessIdentity identity) permitted.TryAdd(identity.ProcessId, identity);
                snapshot.Dispose();
            }
        var ids = permitted.Keys.Select(x => (uint)x).ToHashSet();
        return EnumerateWindows(ids).Where(hwnd =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return permitted.TryGetValue(checked((int)pid), out var expected)
                && ProcessIdentityProbe.TryGetIdentity(checked((int)pid), out var current)
                && current == expected;
        });
    }

    private static IEnumerable<IntPtr> EnumerateWindows(HashSet<uint> processIds)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (processIds.Contains(pid)) windows.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private void Poll()
    {
        var now = DateTime.UtcNow;
        IReadOnlyList<ProcessTreeEntry> processTree;
        var processTreeAvailable = true;
        try { processTree = ProcessTreeProbe.ReadSnapshot(); }
        catch (Exception ex)
        {
            processTree = [];
            processTreeAvailable = false;
            SetStatus($"프로세스 트리를 확인하지 못했습니다. 안전을 위해 companion 자동 종료를 보류합니다: {ex.Message}");
        }
        var ticks = profiles.ToArray().Select(profile => new ProfileTick(profile, FindProcesses(profile.Executable))).ToList();
        foreach (var tick in ticks)
        {
            tick.RootActive = tick.Games.Count > 0;
            if (!tick.RootActive || sessions.ContainsKey(tick.Profile.Id)) continue;
            var newSession = new ProfileRuntimeState(now);
            sessions.Add(tick.Profile.Id, newSession);
            if (tick.Profile.UseVirtualDesktop)
            {
                var desktop = desktops.Ensure(tick.Profile);
                newSession.DesktopId = desktop.DesktopId;
                if (desktop.Warning is not null) SetDesktopWarning($"{tick.Profile.Name}: {desktop.Warning}");
                else Save();
            }
            if (tick.Games.All(x => x.Path is null || x.Identity is null))
                SetStatus($"'{tick.Profile.Name}'과 같은 이름의 프로세스가 있지만 실행 경로/ID를 확인할 수 없습니다. 안전을 위해 동반 앱/종료 처리를 보수적으로 합니다.");
            else SetStatus($"게임 실행 감지: {tick.Profile.Name}");
        }

        var observations = ProfileOrchestration.ObserveAll(
            ticks.Where(tick => sessions.ContainsKey(tick.Profile.Id)).Select(tick => new ProfilePollInput(
                tick.Profile.Id, sessions[tick.Profile.Id],
                tick.Games.Where(game => game.Path is not null && game.Identity is not null).Select(game => game.Identity!.Value).ToArray(),
                tick.RootActive)),
            processTree, processTreeAvailable, now, StartStability, ExitGrace);
        ObserveOwnedCompanionFamilies(processTree);
        var activeGameProfiles = observations.Where(entry => entry.Value.IsActive).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        AdvancePendingCompanionCleanups(processTree, processTreeAvailable, activeGameProfiles, now);

        foreach (var tick in ticks)
        {
            if (observations.TryGetValue(tick.Profile.Id, out var observation)
                && sessions.TryGetValue(tick.Profile.Id, out var session))
            {
                if (observation.IsActive)
                {
                    if (observation.StartCompanions) StartMissingCompanions(tick.Profile);
                    PlaceGroupWindows(tick.Profile, session, processTree);
                }
                else if (observation.EndSession)
                {
                    CleanupOwnedCompanions(tick.Profile, session, processTree, processTreeAvailable);
                    sessions.Remove(tick.Profile.Id);
                    SetStatus($"게임 프로세스 그룹 종료 감지: {tick.Profile.Name}. 기존/공유 동반 앱은 유지했습니다.");
                }
            }
            foreach (var game in tick.Games) game.Dispose();
        }
        RefreshTree();
    }

    private void StartMissingCompanions(GameProfile profile)
    {
        foreach (var companion in profile.Companions)
        {
            if (string.IsNullOrWhiteSpace(companion.Executable)) continue;
            var existing = FindProcesses(companion.Executable);
            var count = existing.Count;
            foreach (var match in existing) match.Dispose();
            if (!ProcessSafety.ShouldLaunchCompanion(count)) continue;
            try
            {
                var start = new ProcessStartInfo(companion.Executable, companion.Arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(companion.Executable)!
                };
                var child = Process.Start(start);
                if (child is null) continue;
                try
                {
                    var executable = Path.GetFullPath(companion.Executable);
                    if (ProcessIdentityProbe.TryRead(child.Id, out var identity, out var actualPath)
                        && PathsEqual(actualPath, executable))
                    {
                        var owned = new OwnedProcess(identity, executable);
                        if (!ownedCompanions.TryGetValue(profile.Id, out var list)) ownedCompanions[profile.Id] = list = [];
                        list.Add(owned);
                    }
                }
                finally { child.Dispose(); }
            }
            catch (Exception ex) { SetStatus($"동반 앱 시작 실패 ({Path.GetFileName(companion.Executable)}): {ex.Message}"); }
        }
    }

    private void PlaceGroupWindows(GameProfile profile, ProfileRuntimeState session, IReadOnlyList<ProcessTreeEntry> processTree)
    {
        if (!profile.UseVirtualDesktop || session.DesktopId is not Guid desktopId) return;
        var gameFamily = ProfileOrchestration.PlacementFamily(session, processTree);
        var activeGameProcesses = gameFamily.Where(identity => processTree.Any(entry => entry.Identity == identity));
        var handles = GetDesktopGroupWindows(profile, activeGameProcesses).ToList();
        if (handles.Count == 0) return;
        var warning = desktops.PlaceWindows(desktopId, handles);
        if (warning is not null)
        {
            SetDesktopWarning($"{profile.Name}: {warning}");
            return;
        }
        if (ProfileOrchestration.ShouldSwitchDesktop(profile.SwitchToVirtualDesktop, session.SwitchAttempted, handles.Count > 0, warning is null))
        {
            session.SwitchAttempted = true;
            warning = desktops.SwitchTo(desktopId);
            if (warning is not null) SetDesktopWarning($"{profile.Name}: {warning}");
        }
    }

    private IEnumerable<IntPtr> GetDesktopGroupWindows(GameProfile profile, IEnumerable<ProcessIdentity> gameProcesses)
    {
        var permitted = gameProcesses.ToDictionary(x => x.ProcessId);
        if (ownedCompanions.TryGetValue(profile.Id, out var owned))
        {
            foreach (var identity in owned)
            {
                if (ProcessIdentityProbe.TryRead(identity.ProcessId, out var current, out var path)
                    && ProfileOrchestration.MayPlaceOwnedCompanionWindow(identity, current, Path.GetFullPath(path),
                        sessions.Values.SelectMany(x => x.KnownGameProcesses).ToHashSet(),
                        GetConfiguredGameExecutables(), IsSharedWithActiveProfile(profile.Id, identity.Executable)))
                    permitted.TryAdd(identity.ProcessId, identity.Identity);
            }
        }
        var ids = permitted.Keys.Select(x => (uint)x).ToHashSet();
        return EnumerateWindows(ids).Where(hwnd =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!permitted.TryGetValue(checked((int)pid), out var expected)
                || !ProcessIdentityProbe.TryGetIdentity(checked((int)pid), out var current)
                || current != expected
                || !ProcessIdentityProbe.TryRead(checked((int)pid), out var verified, out var path)
                || verified != expected)
                return false;
            return !IsProtectedGameForAnotherProfile(profile.Id, current, Path.GetFullPath(path));
        });
    }

    private bool IsSharedWithActiveProfile(string profileId, string executable)
        => ProcessSafety.IsSharedWithActiveProfile(profileId, executable,
            profiles.Where(other => sessions.ContainsKey(other.Id))
                .SelectMany(other => other.Companions.Select(companion => (other.Id, companion.Executable))));

    private HashSet<string> GetConfiguredGameExecutables()
        => profiles.Select(profile => profile.Executable)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private bool IsProtectedGameForAnotherProfile(string profileId, ProcessIdentity identity, string executable)
        => profiles.Where(profile => profile.Id != profileId).Any(profile =>
            PathsEqual(profile.Executable, executable)
            || sessions.TryGetValue(profile.Id, out var session) && session.KnownGameProcesses.Contains(identity));

    private void ObserveOwnedCompanionFamilies(IReadOnlyList<ProcessTreeEntry> snapshot)
    {
        foreach (var owned in ownedCompanions.Values.SelectMany(x => x))
        {
            if (!ownedCompanionFamilies.TryGetValue(owned.Identity, out var family))
                ownedCompanionFamilies[owned.Identity] = family = [owned.Identity];
            family.UnionWith(ProcessTreePolicy.Expand(snapshot, family));
        }
    }

    private void CleanupOwnedCompanions(GameProfile endedProfile, ProfileRuntimeState session, IReadOnlyList<ProcessTreeEntry> snapshot, bool snapshotAvailable)
    {
        if (!snapshotAvailable)
        {
            SetStatus($"프로세스 트리 확인 실패로 동반 앱 정리를 취소했습니다 ({endedProfile.Name}). 실행 중인 앱은 보존합니다.");
            return;
        }
        if (!ownedCompanions.Remove(endedProfile.Id, out var started)) return;
        foreach (var owned in started)
        {
            var configured = endedProfile.Companions.FirstOrDefault(x => PathsEqual(x.Executable, owned.Executable));
            if (configured?.StopWhenGameEnds != true) { ownedCompanionFamilies.Remove(owned.Identity); continue; }
            var processes = FindProcesses(owned.Executable);
            var match = processes.FirstOrDefault(x => x.ProcessId == owned.ProcessId);
            var valid = match?.Path is not null && match.Identity == owned.Identity && PathsEqual(match.Path, owned.Executable);
            foreach (var process in processes) process.Dispose();
            var handle = valid ? ProcessIdentityProbe.OpenCleanupHandle(owned) : null;
            var pending = handle is null ? null : new PendingCompanionCleanup(owned, endedProfile.Id,
                CompanionCleanupConfiguration.Capture(configured), handle, new CompanionCleanupProgress(DateTime.UtcNow));
            if (pending is null || !CleanupStillPermitted(pending, snapshot, snapshotAvailable, new HashSet<string>(StringComparer.Ordinal)))
            {
                pending?.Handle.Dispose();
                SetStatus($"동반 앱 정리를 보류했습니다 ({Path.GetFileName(owned.Executable)}): 소유권·identity·공유/게임 보호 조건을 확인할 수 없습니다.");
                ownedCompanionFamilies.Remove(owned.Identity);
                continue;
            }
            if (!RequestGracefulClose(pending, snapshot, snapshotAvailable, new HashSet<string>(StringComparer.Ordinal)))
            {
                pending.Handle.Dispose();
                SetStatus($"동반 앱 정상 종료 요청 중 안전 조건이 달라져 정리를 취소했습니다: {Path.GetFileName(owned.Executable)}");
                ownedCompanionFamilies.Remove(owned.Identity);
                continue;
            }
            pendingCompanionCleanups[owned.Identity] = pending;
            SetStatus($"동반 앱에 정상 종료를 요청했습니다 ({Path.GetFileName(owned.Executable)}). {CompanionCloseGrace.TotalSeconds:0}초 동안 비차단 방식으로 기다립니다.");
        }
    }

    private bool CleanupStillPermitted(PendingCompanionCleanup pending, IReadOnlyList<ProcessTreeEntry> snapshot, bool snapshotAvailable, IReadOnlySet<string> activeGameProfiles)
    {
        var currentProfile = profiles.FirstOrDefault(profile => profile.Id == pending.ProfileId);
        var currentConfiguration = currentProfile?.Companions.FirstOrDefault(companion => companion.Id == pending.Configuration.Id);
        var configurationMatches = pending.Configuration.Matches(currentConfiguration);
        if (!snapshotAvailable || !ProcessIdentityProbe.Matches(pending.Handle, pending.Owned) || activeGameProfiles.Contains(pending.ProfileId)
            || !configurationMatches)
            return false;
        var processes = FindProcesses(pending.Owned.Executable);
        var match = processes.FirstOrDefault(x => x.ProcessId == pending.Owned.ProcessId);
        var identityMatches = match?.Path is not null && match.Identity == pending.Owned.Identity && PathsEqual(match.Path, pending.Owned.Executable);
        var shared = profiles.Any(p => p.Id != pending.ProfileId && p.Companions.Any(c => PathsEqual(c.Executable, pending.Owned.Executable)));
        var activeFamily = sessions.Values.SelectMany(x => x.KnownGameProcesses).ToHashSet();
        var protectedExecutables = profiles.Select(p => p.Executable).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var family = ownedCompanionFamilies.GetValueOrDefault(pending.Owned.Identity) ?? new HashSet<ProcessIdentity> { pending.Owned.Identity };
        var permitted = identityMatches && ProfileOrchestration.MayCleanupOwnedCompanion(pending.Owned, snapshot, family,
            activeFamily, protectedExecutables, shared, identityMatches, processes.Count, snapshotAvailable, configurationMatches);
        foreach (var process in processes) process.Dispose();
        return permitted;
    }

    private bool RequestGracefulClose(PendingCompanionCleanup pending, IReadOnlyList<ProcessTreeEntry> snapshot, bool snapshotAvailable, IReadOnlySet<string> activeGameProfiles)
    {
        if (!CleanupStillPermitted(pending, snapshot, snapshotAvailable, activeGameProfiles)) return false;
        foreach (var hwnd in EnumerateWindows([checked((uint)pending.Owned.ProcessId)]))
        {
            if (!CleanupStillPermitted(pending, snapshot, snapshotAvailable, activeGameProfiles)) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == pending.Owned.ProcessId) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        return true;
    }

    private void AdvancePendingCompanionCleanups(IReadOnlyList<ProcessTreeEntry> snapshot, bool snapshotAvailable, IReadOnlySet<string> activeGameProfiles, DateTime now)
    {
        foreach (var pending in pendingCompanionCleanups.Values.ToArray())
        {
            var step = ProcessSafety.NextCleanupStep(pending.Progress, now, CompanionCloseGrace,
                CleanupStillPermitted(pending, snapshot, snapshotAvailable, activeGameProfiles), false,
                activeGameProfiles.Contains(pending.ProfileId), !ProcessIdentityProbe.Matches(pending.Handle, pending.Owned),
                pending.Configuration.ForceTerminateAfterGrace);
            if (step == CleanupStep.ForceTerminate)
            {
                if (CleanupStillPermitted(pending, snapshot, snapshotAvailable, activeGameProfiles)
                    && ProcessIdentityProbe.TryTerminate(pending.Handle, pending.Owned))
                    SetStatus($"정상 종료 대기 시간 초과 후 명시적 동의에 따라 동반 앱을 강제 종료했습니다: {Path.GetFileName(pending.Owned.Executable)}");
                else SetStatus($"동반 앱 강제 종료 직전 안전 조건이 달라져 취소했습니다: {Path.GetFileName(pending.Owned.Executable)}");
                FinishPendingCleanup(pending);
            }
            else if (step is CleanupStep.Cancel or CleanupStep.Complete)
            {
                if (step == CleanupStep.Cancel) SetStatus($"동반 앱 종료 대기/정리를 취소하고 프로세스를 보존했습니다: {Path.GetFileName(pending.Owned.Executable)}");
                FinishPendingCleanup(pending);
            }
        }
    }

    private void FinishPendingCleanup(PendingCompanionCleanup pending)
    {
        pendingCompanionCleanups.Remove(pending.Owned.Identity);
        pending.Handle.Dispose();
        ownedCompanionFamilies.Remove(pending.Owned.Identity);
    }

    private sealed class PendingCompanionCleanup(OwnedProcess owned, string profileId, CompanionCleanupConfiguration configuration, Microsoft.Win32.SafeHandles.SafeProcessHandle handle, CompanionCleanupProgress progress)
    {
        public OwnedProcess Owned { get; } = owned;
        public string ProfileId { get; } = profileId;
        public CompanionCleanupConfiguration Configuration { get; } = configuration;
        public Microsoft.Win32.SafeHandles.SafeProcessHandle Handle { get; } = handle;
        public CompanionCleanupProgress Progress { get; } = progress;
    }

    private bool IsOwned(string profileId, string executable)
    {
        if (!ownedCompanions.TryGetValue(profileId, out var list)) return false;
        return list.Any(x => PathsEqual(x.Executable, executable));
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private void Save()
    {
        try { store.Save(profiles); RefreshTree(); }
        catch (Exception ex) { SetStatus($"프로필 저장 실패: {ex.Message}"); }
    }

    private void SetStatus(string message)
    {
        statusMessage = message;
        RenderStatus();
    }

    private void SetDesktopWarning(string message)
    {
        persistentDesktopWarning = message;
        RenderStatus();
    }

    private void SetManualActivationWarning(string message)
    {
        persistentManualActivationWarning = message;
        RenderStatus();
    }

    private void RenderStatus()
    {
        var warnings = new[]
        {
            persistentDesktopWarning is null ? null : $"가상 데스크톱 경고: {persistentDesktopWarning}",
            persistentManualActivationWarning is null ? null : $"관련 앱 안전 경고: {persistentManualActivationWarning}"
        }.Where(x => x is not null);
        var warningText = string.Join(Environment.NewLine, warnings);
        status.Text = warningText.Length == 0 ? statusMessage : $"{warningText}{Environment.NewLine}{statusMessage}";
        var trayMessage = persistentManualActivationWarning ?? persistentDesktopWarning ?? statusMessage;
        tray.Text = trayMessage.Length > 63 ? trayMessage[..60] + "..." : trayMessage;
    }

    private sealed record CatalogItem(string Name, string Path)
    { public override string ToString() => $"{Name}  |  {Path}"; }

    private sealed record ProcessSnapshot(int ProcessId, string? Path, ProcessIdentity? Identity, IntPtr MainWindowHandle) : IDisposable
    { public void Dispose() { } }

    private sealed class ProfileTick(GameProfile profile, List<ProcessSnapshot> games)
    {
        public GameProfile Profile { get; } = profile;
        public List<ProcessSnapshot> Games { get; } = games;
        public bool RootActive { get; set; }
    }

    private sealed class ProfileEditorDialog : Form
    {
        private readonly TextBox name = new();
        private readonly TextBox executable = new();
        private readonly TextBox arguments = new();
        private readonly TextBox steamUri = new();
        private readonly CheckBox useDesktop = new() { Text = "프로필 전용 가상 데스크톱을 만들거나 재사용" };
        private readonly CheckBox switchDesktop = new() { Text = "게임 창을 배치한 뒤 해당 데스크톱으로 전환" };
        public GameProfile? Profile { get; private set; }

        public ProfileEditorDialog(GameProfile? source)
        {
            Text = source is null ? "게임 프로필 추가" : "게임 프로필 수정";
            Width = 700; Height = 390; MinimumSize = new Size(600, 330); StartPosition = FormStartPosition.CenterParent;
            name.Text = source?.Name ?? ""; executable.Text = source?.Executable ?? ""; arguments.Text = source?.Arguments ?? ""; steamUri.Text = source?.SteamUri ?? "";
            useDesktop.Checked = source?.UseVirtualDesktop ?? false; switchDesktop.Checked = source?.SwitchToVirtualDesktop ?? false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, Padding = new Padding(12), AutoSize = false };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            AddRow(layout, 0, "프로필 이름", name);
            AddRow(layout, 1, "게임 실행 파일", executable);
            var browse = new Button { Text = "찾아보기...", AutoSize = true };
            browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "실행 파일 (*.exe)|*.exe", FileName = executable.Text }; if (d.ShowDialog(this) == DialogResult.OK) executable.Text = d.FileName; };
            layout.Controls.Add(browse, 2, 1);
            AddRow(layout, 2, "실행 인수", arguments);
            AddRow(layout, 3, "선택적 Steam URI", steamUri);
            var steamHelp = new Label { Text = "steam://run/ID 또는 steam://rungameid/ID · 실행 감지에는 exe 경로 필요", AutoSize = true, ForeColor = SystemColors.GrayText };
            layout.Controls.Add(steamHelp, 1, 4); layout.SetColumnSpan(steamHelp, 2);
            layout.Controls.Add(useDesktop, 1, 5); layout.SetColumnSpan(useDesktop, 2);
            layout.Controls.Add(switchDesktop, 1, 6); layout.SetColumnSpan(switchDesktop, 2);
            useDesktop.CheckedChanged += (_, _) => switchDesktop.Enabled = useDesktop.Checked;
            switchDesktop.Enabled = useDesktop.Checked;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
            ok.Click += (_, _) => { if (!ValidateAndBuild(source)) DialogResult = DialogResult.None; };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel); Controls.Add(layout); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
        }

        private static void AddRow(TableLayoutPanel layout, int row, string title, Control input)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            input.Dock = DockStyle.Fill; layout.Controls.Add(input, 1, row);
        }

        private bool ValidateAndBuild(GameProfile? source)
        {
            var path = executable.Text.Trim();
            if (string.IsNullOrWhiteSpace(name.Text) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "프로필 이름과 존재하는 게임 EXE 경로를 입력하세요.", "입력 확인"); return false; }
            var uri = steamUri.Text.Trim();
            if (uri.Length > 0 && !IsAllowedSteamUri(uri))
            { MessageBox.Show(this, "Steam URI는 steam://run/... 또는 steam://rungameid/... 형식만 허용합니다.", "입력 확인"); return false; }
            Profile = new GameProfile
            {
                Id = source?.Id ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim(), Executable = Path.GetFullPath(path),
                Arguments = arguments.Text, SteamUri = uri, UseVirtualDesktop = useDesktop.Checked,
                SwitchToVirtualDesktop = switchDesktop.Checked, DesktopId = source?.DesktopId ?? "", Companions = source?.Companions ?? [],
                RelatedApps = source?.RelatedApps ?? []
            };
            return true;
        }
    }

    private sealed class CompanionEditorDialog : Form
    {
        private readonly GameProfile profile;
        private readonly CompanionEditorDraft draft;
        private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true };
        public CompanionEditorDialog(GameProfile profile)
        {
            this.profile = profile;
            draft = new CompanionEditorDraft(profile.Companions);
            Text = $"동반 앱 관리 - {profile.Name}"; Width = 760; Height = 460; StartPosition = FormStartPosition.CenterParent;
            list.Columns.Add("실행 파일", 330); list.Columns.Add("인수", 150); list.Columns.Add("정상 종료 / 강제 종료", 240);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(5) };
            AddButton(bar, "동반 앱 추가", AddCompanion); AddButton(bar, "강제 종료 동의 전환", ToggleForceConsent); AddButton(bar, "선택 제거", RemoveSelected);
            var explanation = new Label { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8), Text = "행 체크는 게임 종료 시 WM_CLOSE 정상 종료 요청 및 10초 비차단 대기를 선택합니다. 대기 후에도 실행 중인 앱의 강제 종료는 별도 명시 동의가 있어야만 시도합니다. 기본/기존 설정은 강제 종료 동의가 꺼져 있습니다. 매 단계에서 같은 프로세스 handle의 PID/경로/생성 시각과 공유·게임 보호를 재확인하며, 기존/공유/불확실한 앱은 보존합니다." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5) };
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            Controls.Add(list); Controls.Add(bar); Controls.Add(explanation); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
            foreach (var c in draft.Items) AddItem(c);
            list.ItemCheck += (_, e) =>
            {
                var row = list.Items[e.Index];
                var companion = draft.SetStopWhenGameEnds(((Companion)row.Tag!).Id, e.NewValue == CheckState.Checked);
                if (companion is not null) row.SubItems[2].Text = ConsentLabel(companion);
            };
            FormClosing += (_, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                profile.Companions = draft.Commit(list.Items.Cast<ListViewItem>()
                    .Select(item => ((Companion)item.Tag!, item.Checked)));
            };
        }
        private void AddItem(Companion companion)
        {
            var item = new ListViewItem(companion.Executable) { Tag = companion, Checked = companion.StopWhenGameEnds };
            item.SubItems.Add(companion.Arguments); item.SubItems.Add(ConsentLabel(companion)); list.Items.Add(item);
        }
        private void AddCompanion()
        {
            using var picker = new OpenFileDialog { Filter = "실행 파일 (*.exe)|*.exe", Title = "동반 앱 실행 파일 선택" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var args = Microsoft.VisualBasic.Interaction.InputBox("동반 앱 실행 인수 (선택)", "동반 앱", "");
            var companion = new Companion { Executable = Path.GetFullPath(picker.FileName), Arguments = args };
            if (MessageBox.Show(this, "게임 종료 후 정상 종료 요청을 보낼까요? 런처 소유·안전 조건 확인 후 WM_CLOSE를 보내고 10초 기다립니다. 강제 종료는 별도 동의가 필요합니다.", "정상 종료 요청 동의", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                companion.StopWhenGameEnds = true;
                companion.ForceTerminateAfterGrace = MessageBox.Show(this, "정상 종료 대기 후에도 앱이 계속 실행 중이면 강제 종료에 별도로 동의합니까?", "강제 종료 별도 동의", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            AddItem(draft.Add(companion));
        }
        private void ToggleForceConsent()
        {
            foreach (ListViewItem item in list.SelectedItems)
            {
                if (!item.Checked) { MessageBox.Show(this, "먼저 정상 종료 요청을 선택하세요."); continue; }
                var companion = draft.ToggleForceConsent(((Companion)item.Tag!).Id);
                if (companion is not null) item.SubItems[2].Text = ConsentLabel(companion);
            }
        }
        private static string ConsentLabel(Companion companion)
            => companion.StopWhenGameEnds ? companion.ForceTerminateAfterGrace ? "정상 요청 → 강제 동의" : "정상 요청만 · 유지" : "기본: 유지";

        private void RemoveSelected()
        { foreach (ListViewItem item in list.SelectedItems) list.Items.Remove(item); }
    }

    private sealed class RelatedAppEditorDialog : Form
    {
        private readonly GameProfile profile;
        private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };

        public RelatedAppEditorDialog(GameProfile profile)
        {
            this.profile = profile;
            Text = $"수동 관련 앱 - {profile.Name}"; Width = 720; Height = 420; StartPosition = FormStartPosition.CenterParent;
            list.Columns.Add("이름", 180); list.Columns.Add("실행 파일", 330); list.Columns.Add("인수", 150);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(5) };
            AddButton(bar, "관련 앱 추가", AddRelatedApp); AddButton(bar, "선택 제거", RemoveSelected);
            var note = new Label { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8), Text = "수동 관련 앱은 자동 실행·게임 종료 정리·동반 앱 소유권에 포함되지 않습니다. 게임 실행 여부와 관계없이 목록에서 직접 실행하거나 활성화하세요." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5) };
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            Controls.Add(list); Controls.Add(bar); Controls.Add(note); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
            foreach (var app in profile.RelatedApps ?? []) AddItem(app);
            FormClosing += (_, _) =>
            {
                if (DialogResult == DialogResult.OK)
                    profile.RelatedApps = list.Items.Cast<ListViewItem>().Select(item => (RelatedApp)item.Tag!).ToList();
            };
        }

        private void AddItem(RelatedApp app)
        {
            var item = new ListViewItem(app.Name) { Tag = app };
            item.SubItems.Add(app.Executable); item.SubItems.Add(app.Arguments); list.Items.Add(item);
        }

        private void AddRelatedApp()
        {
            using var picker = new OpenFileDialog { Filter = "실행 파일 (*.exe)|*.exe", Title = "수동 관련 앱 실행 파일 선택" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var name = Microsoft.VisualBasic.Interaction.InputBox("목록에 표시할 이름", "수동 관련 앱", Path.GetFileNameWithoutExtension(picker.FileName));
            if (string.IsNullOrWhiteSpace(name)) return;
            var args = Microsoft.VisualBasic.Interaction.InputBox("실행 인수 (선택)", "수동 관련 앱", "");
            AddItem(new RelatedApp { Name = name.Trim(), Executable = Path.GetFullPath(picker.FileName), Arguments = args });
        }

        private void RemoveSelected()
        { foreach (ListViewItem item in list.SelectedItems) list.Items.Remove(item); }
    }
}
