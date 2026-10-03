using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GridShift;

public sealed class MainForm : Form
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr extraData);

    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private static readonly TimeSpan CompanionCloseGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartStability = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
    private readonly ProfileStore store = new();
    private readonly DesktopOwnershipStore desktopOwnership = new();
    private readonly VirtualDesktopCoordinator desktops = new();
    private readonly PendingDesktopCleanupQueue pendingDesktopCleanups = new();
    private readonly ProfileDesktopLifecycle desktopLifecycle;
    private readonly List<GameProfile> profiles;
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 76, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly Dictionary<string, ProfileRuntimeState> sessions = new();
    private readonly Dictionary<string, List<OwnedProcess>> ownedCompanions = new();
    private readonly Dictionary<ProcessIdentity, HashSet<ProcessIdentity>> ownedCompanionFamilies = new();
    private readonly Dictionary<ProcessIdentity, PendingCompanionCleanup> pendingCompanionCleanups = new();
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly Stream trayIconResource;
    private bool quitting;
    private string statusMessage = "";
    private string? persistentDesktopWarning;
    private string? persistentManualActivationWarning;

    public MainForm()
    {
        Text = "GridShift — 게임 시작";
        Width = 1000; Height = 650; MinimumSize = new Size(720, 420);
        Font = new Font("맑은 고딕", 9F);
        profiles = store.Load();
        desktopLifecycle = new ProfileDesktopLifecycle(desktops, desktopOwnership);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(6), WrapContents = true, AutoScroll = false };
        AddButton(bar, "게임 시작", LaunchSelected);
        AddButton(bar, "게임 등록", AddProfile);
        AddButton(bar, "게임 설정", EditProfile);
        AddButton(bar, "게임 창 보기", ActivateSelectedGroup);
        AddButton(bar, "앱 찾기", SearchApps);
        AddButton(bar, "함께 실행할 앱", EditCompanions);
        AddButton(bar, "필요할 때 앱", EditRelatedApps);
        AddButton(bar, "선택 앱 열기", LaunchSelectedRelatedApp);
        AddButton(bar, "선택 창으로 이동", ActivateSelectedRelatedApp);
        AddButton(bar, "게임 제거", DeleteSelected);
        Controls.Add(tree); Controls.Add(bar); Controls.Add(status);
        RefreshTree();

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => PopulateTrayMenu(menu);
        menu.Items.Add("종료", null, (_, _) => { quitting = true; tray!.Visible = false; Close(); });
        (trayIcon, trayIconResource) = GridShiftIcon.Load();
        tray = new NotifyIcon { Icon = trayIcon, Text = "GridShift — 게임 시작", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => RestoreWindow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, e) =>
        {
            if (!quitting) { e.Cancel = true; Hide(); }
            else { timer.Stop(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); trayIconResource.Dispose(); }
        };
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
                var relatedMenu = new ToolStripMenuItem("필요할 때 켜는 앱 — 게임과 독립 실행");
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
                ? "실행 중"
                : matches.Any(x => x.Path is not null) ? "실행 중" : matches.Count > 0 ? "확인 필요" : sessions.ContainsKey(profile.Id) ? "종료 확인 중" : "시작 준비됨";
            foreach (var process in matches) process.Dispose();
            var root = new TreeNode($"{profile.Name}  ·  게임 {state}") { Name = ProfileNodeKey(profile.Id), Tag = profile };
            var automatic = new TreeNode("함께 실행할 앱") { Name = AutomaticCategoryNodeKey(profile.Id) };
            for (var index = 0; index < profile.Companions.Count; index++)
            {
                var companion = profile.Companions[index];
                var processes = FindProcesses(companion.Executable);
                var companionState = !companion.Autostart ? "직접 열기" : IsOwned(profile.Id, companion.Executable) ? "게임과 함께 열림" : processes.Count > 0 ? "이미 열려 있어 유지" : "게임 시작 시 열기";
                var key = CompanionNodeKey(profile.Id, index, companion.Executable);
                automatic.Nodes.Add(new TreeNode($"{Path.GetFileName(companion.Executable)} · {companionState}") { Name = key, Tag = companion });
                foreach (var process in processes) process.Dispose();
            }
            root.Nodes.Add(automatic);
            var related = new TreeNode("필요할 때 켜는 앱") { Name = RelatedCategoryNodeKey(profile.Id) };
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
            profile.CleanupCreatedDesktop = dialog.Profile.CleanupCreatedDesktop;
            profile.ExitDebounceSeconds = dialog.Profile.ExitDebounceSeconds;
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
                    SetManualActivationWarning($"'{app.Name}' 창은 찾았지만 Windows가 앞으로 가져오지 못하게 했습니다.");
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
        var apps = AppCatalog.Discover().Select(app => new CatalogItem(app.Name, app.Path)).ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && AppPathSelectionPolicy.TryValidateExecutable(path, out var fullPath, out _))
                    apps.TryAdd(fullPath, new CatalogItem(Path.GetFileNameWithoutExtension(fullPath), fullPath));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            finally { process.Dispose(); }
        }
        var targetProfile = SelectedProfile();
        using var dialog = new Form { Text = "게임과 앱 선택", Width = 780, Height = 560, MinimumSize = new Size(650, 420), StartPosition = FormStartPosition.CenterParent };
        var query = new TextBox { Dock = DockStyle.Top, PlaceholderText = "설치 앱/실행 중인 앱 이름 또는 경로 검색" };
        var purpose = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        purpose.Items.AddRange(["새 게임으로 등록", "선택 게임과 함께 열기", "필요할 때 직접 열기"]);
        purpose.SelectedIndex = 0;
        var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        var selectedPath = new Label { Dock = DockStyle.Bottom, Height = 38, AutoEllipsis = true, Padding = new Padding(8), Text = "실행 파일을 선택하면 전체 경로와 확인 결과가 표시됩니다." };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(5), WrapContents = false, AutoScroll = true };
        var add = new Button { Text = "선택한 앱 사용", AutoSize = true };
        var browse = new Button { Text = "EXE 또는 바로가기 찾기…", AutoSize = true };
        actions.Controls.Add(add); actions.Controls.Add(browse);
        void Populate()
        {
            list.Items.Clear();
            foreach (var item in apps.Values.Where(item => item.Name.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)
                || item.Path.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Take(1500))
                list.Items.Add(item);
        }
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItem is CatalogItem item && AppPathSelectionPolicy.TryValidateExecutable(item.Path, out var path, out _))
                selectedPath.Text = $"사용 가능: {path}";
            else selectedPath.Text = "실행 파일 경로를 확인할 수 없습니다.";
        };
        query.TextChanged += (_, _) => Populate();
        purpose.SelectedIndexChanged += (_, _) => { add.Text = purpose.SelectedIndex switch { 0 => "새 게임으로 등록", 1 => "함께 실행할 앱에 추가", _ => "필요할 때 켜는 앱에 추가" }; };
        void UsePath(string name, string path)
        {
            if (!AppPathSelectionPolicy.TryValidateExecutable(path, out var validPath, out var reason))
            { MessageBox.Show(dialog, reason, "실행 파일 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            switch (purpose.SelectedIndex)
            {
                case 0: profiles.Add(new GameProfile { Name = name, Executable = validPath }); break;
                case 1:
                    if (targetProfile is null) { MessageBox.Show(dialog, "먼저 게임을 등록하고 선택하세요."); return; }
                    targetProfile.Companions.Add(new Companion { Executable = validPath, Autostart = true, StopWhenGameEnds = true }); break;
                default:
                    if (targetProfile is null) { MessageBox.Show(dialog, "먼저 게임을 등록하고 선택하세요."); return; }
                    CatalogSelectionPolicy.AddToRelatedApps(targetProfile, name, validPath); break;
            }
            Save(); dialog.Close();
        }
        add.Click += (_, _) =>
        {
            if (list.SelectedItem is not CatalogItem item) { SetStatus("앱 목록에서 항목을 선택하거나 실행 파일을 찾아보세요."); return; }
            UsePath(item.Name, item.Path);
        };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "실행 파일 또는 바로가기 (*.exe;*.lnk)|*.exe;*.lnk|실행 파일 (*.exe)|*.exe|바로가기 (*.lnk)|*.lnk", Title = "게임 또는 앱 실행 방법 선택" };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            var path = picker.FileName;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && !AppPathSelectionPolicy.TryResolveShortcut(path, out path, out var reason))
            { MessageBox.Show(dialog, reason, "바로가기 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            UsePath(Path.GetFileNameWithoutExtension(path), path);
        };
        list.DoubleClick += (_, _) => add.PerformClick();
        dialog.Controls.Add(list); dialog.Controls.Add(query); dialog.Controls.Add(purpose); dialog.Controls.Add(selectedPath); dialog.Controls.Add(actions);
        Populate(); dialog.ShowDialog(this);
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
            var desktop = desktopLifecycle.Ensure(profile);
            if (desktop.Warning is not null) SetDesktopWarning(desktop.Warning);
            Save();
        }
        try
        {
            var startInfo = string.IsNullOrWhiteSpace(profile.SteamUri)
                ? new ProcessStartInfo(profile.Executable, profile.Arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(profile.Executable)! }
                : new ProcessStartInfo(profile.SteamUri) { UseShellExecute = true };
            _ = Process.Start(startInfo);
            SetStatus($"'{profile.Name}'을 시작했습니다. 게임이 켜지는지 확인하고 있습니다.");
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

    private static IReadOnlyList<IntPtr> EnumerateAllWindows()
    {
        var windows = new List<IntPtr>();
        if (!EnumWindows((hwnd, _) => { windows.Add(hwnd); return true; }, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "All-window enumeration was incomplete.");
        return windows;
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
            SetStatus($"실행 중인 앱 상태를 모두 확인하지 못했습니다. 안전을 위해 자동 종료를 보류합니다: {ex.Message}");
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
                var desktop = desktopLifecycle.Ensure(tick.Profile);
                newSession.DesktopId = desktop.DesktopId;
                if (desktop.Warning is not null) SetDesktopWarning($"{tick.Profile.Name}: {desktop.Warning}");
                Save();
            }
            if (tick.Games.All(x => x.Path is null || x.Identity is null))
                SetStatus($"{tick.Profile.Name}: 이름이 같은 앱은 찾았지만 게임 실행 파일인지 확인할 수 없습니다. 안전을 위해 자동 앱 시작/정리는 보류합니다.");
            else SetStatus($"{tick.Profile.Name}: 게임 실행을 확인했습니다. {StartStability.TotalSeconds:0}초 동안 안정적인지 살펴본 뒤 함께 열 앱을 시작합니다.");
        }

        var observations = ProfileOrchestration.ObserveAll(
            ticks.Where(tick => sessions.ContainsKey(tick.Profile.Id)).Select(tick => new ProfilePollInput(
                tick.Profile.Id, sessions[tick.Profile.Id],
                tick.Games.Where(game => game.Path is not null && game.Identity is not null).Select(game => game.Identity!.Value).ToArray(),
                tick.RootActive, TimeSpan.FromSeconds(Math.Clamp(tick.Profile.ExitDebounceSeconds, 1, 10)))),
            processTree, processTreeAvailable, now, StartStability, ExitGrace);
        ObserveOwnedCompanionFamilies(processTree);
        var activeGameProfiles = observations.Where(entry => entry.Value.IsActive).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        AdvancePendingCompanionCleanups(processTree, processTreeAvailable, activeGameProfiles, now);
        AdvancePendingDesktopCleanups(processTree, processTreeAvailable, activeGameProfiles);

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
                    var ownedFamily = GetOwnedCompanionFamily(tick.Profile.Id);
                    CleanupOwnedCompanions(tick.Profile, session, processTree, processTreeAvailable);
                    if (tick.Profile.CleanupCreatedDesktop && session.DesktopId is Guid desktopId)
                        pendingDesktopCleanups.Enqueue(tick.Profile.Id, desktopId, ownedFamily);
                    sessions.Remove(tick.Profile.Id);
                    SetStatus($"{tick.Profile.Name}: 게임이 끝났습니다. 함께 연 앱을 안전하게 정리하고 있습니다.");
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
            if (!companion.Autostart || string.IsNullOrWhiteSpace(companion.Executable)) continue;
            var existing = FindProcesses(companion.Executable);
            var count = existing.Count;
            foreach (var match in existing) match.Dispose();
            if (!ProcessSafety.ShouldLaunchCompanion(count))
            {
                if (count > 0) SetStatus($"{Path.GetFileName(companion.Executable)}은 이미 실행 중이어서 그대로 두었습니다.");
                continue;
            }
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
                        SetStatus($"{Path.GetFileName(companion.Executable)}을 게임과 함께 열었습니다.");
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
            SetStatus($"{endedProfile.Name}: 앱 상태를 확인하지 못해 닫기 요청을 취소했습니다. 실행 중인 앱은 그대로 둡니다.");
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
                SetStatus($"{Path.GetFileName(owned.Executable)}은 실행 상태나 사용 중인 게임을 확인할 수 없어 그대로 두었습니다.");
                ownedCompanionFamilies.Remove(owned.Identity);
                continue;
            }
            if (!RequestGracefulClose(pending, snapshot, snapshotAvailable, new HashSet<string>(StringComparer.Ordinal)))
            {
                pending.Handle.Dispose();
                SetStatus($"{Path.GetFileName(owned.Executable)}의 실행 상태가 바뀌어 닫기 요청을 취소하고 앱을 보존했습니다.");
                ownedCompanionFamilies.Remove(owned.Identity);
                continue;
            }
            pendingCompanionCleanups[owned.Identity] = pending;
            SetStatus($"동반 앱에 정상 종료를 요청했습니다 ({Path.GetFileName(owned.Executable)}). {CompanionCloseGrace.TotalSeconds:0}초 동안 비차단 방식으로 기다립니다.");
        }
    }

    private IReadOnlyList<ProcessIdentity> GetOwnedCompanionFamily(string profileId)
    {
        if (!ownedCompanions.TryGetValue(profileId, out var owned)) return [];
        return owned.SelectMany(process => ownedCompanionFamilies.TryGetValue(process.Identity, out var family)
            ? family : [process.Identity]).Distinct().ToArray();
    }

    private void AdvancePendingDesktopCleanups(IReadOnlyList<ProcessTreeEntry> processSnapshot, bool processSnapshotAvailable, IReadOnlySet<string> activeGameProfiles)
    {
        foreach (var pending in pendingDesktopCleanups.Items.ToArray())
        {
            var profile = profiles.FirstOrDefault(candidate => candidate.Id == pending.ProfileId);
            var sameDesktop = profile is not null && Guid.TryParse(profile.DesktopId, out var configuredDesktop) && configuredDesktop == pending.DesktopId;
            var shared = sessions.Any(entry => entry.Key != pending.ProfileId && entry.Value.DesktopId == pending.DesktopId);
            var companionClosePending = pendingCompanionCleanups.Values.Any(cleanup => cleanup.ProfileId == pending.ProfileId);
            var ownedFamilyComplete = processSnapshotAvailable
                && PendingDesktopCleanupPolicy.IsOwnedCompanionFamilyComplete(pending.OwnedCompanionFamily, processSnapshot, out _);
            var readiness = PendingDesktopCleanupPolicy.Evaluate(profile is not null, profile is { UseVirtualDesktop: true, CleanupCreatedDesktop: true },
                sameDesktop, desktopLifecycle.IsCreatedByLauncher(pending.ProfileId, pending.DesktopId),
                activeGameProfiles.Contains(pending.ProfileId), processSnapshotAvailable, companionClosePending, ownedFamilyComplete, shared);
            if (readiness == PendingDesktopCleanupReadiness.Cancel)
            {
                pendingDesktopCleanups.Remove(pending.ProfileId);
                if (profile is not null && profile.CleanupCreatedDesktop && !activeGameProfiles.Contains(pending.ProfileId))
                    SetDesktopWarning($"{profile.Name}: 설정이나 생성 기록이 바뀌어 데스크톱 정리를 취소했습니다.");
                continue;
            }
            if (readiness == PendingDesktopCleanupReadiness.Wait)
            {
                if (!processSnapshotAvailable) SetDesktopWarning($"{profile!.Name}: 앱 상태를 확인할 수 없어 데스크톱 정리를 기다립니다.");
                else if (companionClosePending || !ownedFamilyComplete) SetStatus($"{profile!.Name}: 함께 연 앱이 모두 닫히기를 기다린 뒤 데스크톱을 확인합니다.");
                else if (shared) SetStatus($"{profile!.Name}: 다른 실행 중인 게임도 데스크톱을 사용 중이라 정리를 기다립니다.");
                continue;
            }

            SetStatus($"{profile!.Name}: 데스크톱의 모든 창을 확인하고 있습니다.");
            var warning = TryCleanupCreatedDesktop(profile, pending.DesktopId);
            if (warning is null)
            {
                pendingDesktopCleanups.Remove(pending.ProfileId);
                SetStatus($"{profile.Name}: 비어 있는 GridShift 생성 데스크톱을 정리했습니다.");
            }
            else SetDesktopWarning($"{profile.Name}: {warning}");
        }
    }

    private string? TryCleanupCreatedDesktop(GameProfile profile, Guid desktopId)
    {
        var launcherWindow = IntPtr.Zero;
        if (IsHandleCreated && IsWindow(Handle))
        {
            GetWindowThreadProcessId(Handle, out var ownerPid);
            if (ownerPid == checked((uint)Environment.ProcessId)) launcherWindow = Handle;
        }
        string? warning;
        try { warning = desktops.RemoveCreatedDesktop(desktopId, desktopLifecycle.IsCreatedByLauncher(profile.Id, desktopId), false, EnumerateAllWindows, launcherWindow); }
        catch (Exception ex) { warning = $"열린 창을 모두 확인하지 못해 유지합니다 ({ex.GetType().Name})."; }
        if (warning is not null) return warning;

        profile.DesktopId = "";
        try { desktopLifecycle.Forget(profile.Id, desktopId); }
        catch (Exception ex) { SetDesktopWarning($"{profile.Name}: 데스크톱은 정리했지만 생성 기록을 갱신하지 못했습니다 ({ex.GetType().Name})."); }
        Save();
        return null;
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
            else if (step == CleanupStep.Wait)
            {
                var remaining = Math.Max(0, (int)Math.Ceiling((CompanionCloseGrace - (now - pending.Progress.CloseRequestedUtc)).TotalSeconds));
                SetStatus($"{Path.GetFileName(pending.Owned.Executable)}의 닫기 응답을 기다립니다 ({remaining}초 남음). 기다리는 동안에도 GridShift는 사용할 수 있습니다.");
            }
            else if (step is CleanupStep.Cancel or CleanupStep.Complete)
            {
                if (step == CleanupStep.Cancel) SetStatus($"{Path.GetFileName(pending.Owned.Executable)}을 안전하게 확인할 수 없어 그대로 두었습니다.");
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
        private readonly CheckBox cleanupDesktop = new()
        {
            Text = "GridShift가 만든 빈 데스크톱만 게임 종료 후 정리 (선택 사항)",
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        private readonly NumericUpDown exitDebounce = new() { Minimum = 1, Maximum = 10, Width = 72 };
        public GameProfile? Profile { get; private set; }

        public ProfileEditorDialog(GameProfile? source)
        {
            Text = source is null ? "게임 프로필 추가" : "게임 프로필 수정";
            Width = 900; Height = 520; MinimumSize = new Size(820, 480); AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
            name.Text = source?.Name ?? ""; executable.Text = source?.Executable ?? ""; arguments.Text = source?.Arguments ?? ""; steamUri.Text = source?.SteamUri ?? "";
            useDesktop.Checked = source?.UseVirtualDesktop ?? false; switchDesktop.Checked = source?.SwitchToVirtualDesktop ?? false;
            cleanupDesktop.Checked = source?.CleanupCreatedDesktop ?? false; exitDebounce.Value = Math.Clamp(source?.ExitDebounceSeconds ?? 3, 1, 10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 9, Padding = new Padding(12), AutoSize = false };
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
            layout.Controls.Add(cleanupDesktop, 1, 7); layout.SetColumnSpan(cleanupDesktop, 2);
            layout.Controls.Add(new Label { Text = "게임 종료 확인 대기(초)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 8);
            layout.Controls.Add(exitDebounce, 1, 8);
            var debounceHelp = new Label { Text = "게임 업데이트/잠깐 멈춤으로 인한 오감지를 줄이는 짧은 대기", AutoSize = true, ForeColor = SystemColors.GrayText };
            layout.Controls.Add(debounceHelp, 2, 8);
            useDesktop.CheckedChanged += (_, _) => { switchDesktop.Enabled = useDesktop.Checked; cleanupDesktop.Enabled = useDesktop.Checked; };
            switchDesktop.Enabled = useDesktop.Checked; cleanupDesktop.Enabled = useDesktop.Checked;
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
            var validPath = AppPathSelectionPolicy.TryValidateExecutable(path, out var fullPath, out var reason);
            if (string.IsNullOrWhiteSpace(name.Text) || !validPath)
            { MessageBox.Show(this, string.IsNullOrWhiteSpace(name.Text) ? "게임 이름을 입력하세요." : reason, "실행 파일 확인"); return false; }
            var uri = steamUri.Text.Trim();
            if (uri.Length > 0 && !IsAllowedSteamUri(uri))
            { MessageBox.Show(this, "Steam URI는 steam://run/... 또는 steam://rungameid/... 형식만 허용합니다.", "입력 확인"); return false; }
            Profile = new GameProfile
            {
                Id = source?.Id ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim(), Executable = fullPath,
                Arguments = arguments.Text, SteamUri = uri, UseVirtualDesktop = useDesktop.Checked,
                SwitchToVirtualDesktop = switchDesktop.Checked, CleanupCreatedDesktop = cleanupDesktop.Checked,
                ExitDebounceSeconds = decimal.ToInt32(exitDebounce.Value), DesktopId = source?.DesktopId ?? "",
                Companions = source?.Companions ?? [],
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
            Text = $"게임과 함께 열 앱 - {profile.Name}"; Width = 820; Height = 500; StartPosition = FormStartPosition.CenterParent;
            list.Columns.Add("게임 시작 시 열기", 125); list.Columns.Add("실행 파일", 320); list.Columns.Add("게임 종료 시", 220);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(5), WrapContents = false, AutoScroll = true };
            AddButton(bar, "앱 추가", AddCompanion); AddButton(bar, "닫기 요청 켜기/끄기", ToggleNormalStop); AddButton(bar, "기존 앱 설정 옮기기", EnableLegacyStop); AddButton(bar, "강제 종료 별도 허용", ToggleForceConsent); AddButton(bar, "선택 제거", RemoveSelected);
            var explanation = new Label { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(8), Text = "새 앱은 게임과 함께 열리고 게임 종료 때 닫기 요청을 보냅니다. 이미 켜져 있던 앱은 건드리지 않습니다. 기존 앱 설정은 이 목록에서 직접 허용하고 저장할 때만 바뀝니다. 강제 종료는 별도 동의가 없으면 하지 않습니다." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5) };
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            Controls.Add(list); Controls.Add(bar); Controls.Add(explanation); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
            foreach (var c in draft.Items) AddItem(c);
            list.ItemCheck += (_, e) => draft.SetAutostart(((Companion)list.Items[e.Index].Tag!).Id, e.NewValue == CheckState.Checked);
            FormClosing += (_, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                profile.Companions = draft.CommitFull(list.Items.Cast<ListViewItem>()
                    .Select(item => ((Companion)item.Tag!, item.Checked, draft.Items.Single(c => c.Id == ((Companion)item.Tag!).Id).StopWhenGameEnds)));
            };
        }
        private void AddItem(Companion companion)
        {
            var item = new ListViewItem("") { Tag = companion, Checked = companion.Autostart };
            item.SubItems.Add(companion.Executable); item.SubItems.Add(ConsentLabel(companion)); list.Items.Add(item);
        }
        private void AddCompanion()
        {
            using var picker = new OpenFileDialog { Filter = "실행 파일 (*.exe)|*.exe", Title = "함께 열 앱 선택" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            if (!AppPathSelectionPolicy.TryValidateExecutable(picker.FileName, out var path, out var reason))
            { MessageBox.Show(this, reason, "실행 파일 확인"); return; }
            var args = Microsoft.VisualBasic.Interaction.InputBox("실행 인수 (선택)", "함께 열 앱", "");
            AddItem(draft.Add(new Companion { Executable = path, Arguments = args, Autostart = true, StopWhenGameEnds = true }));
        }
        private void ToggleNormalStop()
        {
            foreach (ListViewItem row in list.SelectedItems)
            {
                var companion = (Companion)row.Tag!;
                var current = draft.Items.Single(c => c.Id == companion.Id);
                var enable = !current.StopWhenGameEnds;
                if (enable && MessageBox.Show(this, "이 앱에 게임 종료 시 정상 닫기 요청을 보내도록 허용합니까? 강제 종료는 별도 동의가 필요합니다.", "닫기 요청 설정", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) continue;
                draft.SetStopWhenGameEnds(companion.Id, enable);
                row.SubItems[2].Text = ConsentLabel(current);
            }
        }
        private void EnableLegacyStop()
        {
            var rows = list.SelectedItems.Cast<ListViewItem>().ToArray();
            if (rows.Length == 0) { MessageBox.Show(this, "변경할 앱을 먼저 선택하세요."); return; }
            if (MessageBox.Show(this, "선택한 앱에 게임 종료 시 정상 닫기 요청을 보내도록 바꿀까요? 이 설정은 저장을 눌렀을 때만 적용됩니다. 강제 종료는 허용되지 않습니다.", "기존 앱 설정 변경", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            draft.EnableLegacyNormalStopWithConsent(rows.Select(row => ((Companion)row.Tag!).Id));
            foreach (var row in rows) row.SubItems[2].Text = ConsentLabel((Companion)row.Tag!);
        }
        private void ToggleForceConsent()
        {
            foreach (ListViewItem item in list.SelectedItems)
            {
                var companion = (Companion)item.Tag!;
                var current = draft.Items.Single(c => c.Id == companion.Id);
                if (!current.StopWhenGameEnds) { MessageBox.Show(this, "먼저 게임 종료 시 닫기 요청을 허용하세요."); continue; }
                var prompt = current.ForceTerminateAfterGrace
                    ? $"'{Path.GetFileName(companion.Executable)}'에 대한 강제 종료 허용을 취소할까요?"
                    : $"'{Path.GetFileName(companion.Executable)}'가 10초 후에도 닫히지 않으면 강제로 종료하는 데 동의합니까?";
                if (MessageBox.Show(this, prompt, "강제 종료 별도 동의", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) continue;
                var changed = draft.ToggleForceConsent(companion.Id);
                if (changed is not null) rowRefresh(item, changed);
            }
        }
        private static void rowRefresh(ListViewItem item, Companion companion) => item.SubItems[2].Text = ConsentLabel(companion);
        private static string ConsentLabel(Companion companion)
            => companion.StopWhenGameEnds ? companion.ForceTerminateAfterGrace ? "닫기 요청 · 강제 종료 동의" : "닫기 요청 · 앱 종료 시도" : "게임 종료 후에도 유지";

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
            var note = new Label { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8), Text = "필요할 때 직접 여는 앱입니다. 게임과 함께 자동으로 열리지 않고 게임이 끝나도 닫히지 않습니다. 게임이 실행 중이지 않아도 열거나 창으로 이동할 수 있습니다." };
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
