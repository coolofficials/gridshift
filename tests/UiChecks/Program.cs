using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GridShift;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WinForms dialog-path checks must execute on Windows x64.");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CheckNewCompanionDefaultsAndSavedItemCheck();
        CheckActualStopAndForceConsentSaveAndCancel();
        CheckLegacyMigrationYesCancelNoSaveAndYesSave();
        CheckCleanupConsentRenderedBounds();
        Console.WriteLine("PASS Windows actual companion/profile dialog defaults, ItemCheck, Save, Cancel, consent migration, and rendered bounds");
    }

    private static void CheckNewCompanionDefaultsAndSavedItemCheck()
    {
        var fresh = new Companion { Id = "fresh", Executable = @"C:\Apps\fresh.exe" };
        var profile = new GameProfile { Companions = [fresh] };
        using var dialog = CreateEditor(profile);
        var list = CompanionList(dialog);
        Check(fresh.Autostart && fresh.StopWhenGameEnds && !fresh.ForceTerminateAfterGrace,
            "new companion model defaults to autostart and normal close, force off");
        Drive(dialog, () =>
        {
            Check(list.Items[0].Checked, "actual new-companion dialog renders autostart checked by default");
            Check(list.Items[0].SubItems[2].Text == "닫기 요청 · 앱 종료 시도",
                "actual new-companion dialog renders normal-stop consent and no force consent");
            list.Items[0].Checked = false;
            ButtonByText(dialog, "저장").PerformClick();
        });
        var saved = profile.Companions.Single();
        Check(!saved.Autostart && saved.StopWhenGameEnds && !saved.ForceTerminateAfterGrace,
            "actual ListView ItemCheck and Save commit autostart without changing normal-stop or force consent");
    }

    private static void CheckActualStopAndForceConsentSaveAndCancel()
    {
        var profile = new GameProfile { Companions = [new Companion
        {
            Id = "consent", Executable = @"C:\Apps\consent.exe", Autostart = true,
            StopWhenGameEnds = false, ForceTerminateAfterGrace = false
        }] };
        using (var dialog = CreateEditor(profile))
        {
            var list = CompanionList(dialog);
            Drive(dialog, () =>
            {
                list.Items[0].Selected = true;
                list.Items[0].Checked = false;
                ButtonByText(dialog, "닫기 요청 켜기/끄기").PerformClick();
                ButtonByText(dialog, "강제 종료 별도 허용").PerformClick();
                ButtonByText(dialog, "취소").PerformClick();
            }, ("닫기 요청 설정", 0), ("강제 종료 별도 동의", 0));
        }
        var cancelled = profile.Companions.Single();
        Check(cancelled.Autostart && !cancelled.StopWhenGameEnds && !cancelled.ForceTerminateAfterGrace,
            "actual autostart ItemCheck plus Yes to normal-stop/force prompts then Cancel preserves all committed settings");

        var savedProfile = new GameProfile { Companions = [new Companion
        {
            Id = "commit", Executable = @"C:\Apps\commit.exe", Autostart = true,
            StopWhenGameEnds = false, ForceTerminateAfterGrace = false
        }] };
        using (var dialog = CreateEditor(savedProfile))
        {
            var list = CompanionList(dialog);
            Drive(dialog, () =>
            {
                list.Items[0].Selected = true;
                list.Items[0].Checked = false;
                ButtonByText(dialog, "닫기 요청 켜기/끄기").PerformClick();
                ButtonByText(dialog, "강제 종료 별도 허용").PerformClick();
                ButtonByText(dialog, "저장").PerformClick();
            }, ("닫기 요청 설정", 0), ("강제 종료 별도 동의", 0));
        }
        var committed = savedProfile.Companions.Single();
        Check(!committed.Autostart && committed.StopWhenGameEnds && committed.ForceTerminateAfterGrace,
            "actual ItemCheck, normal-stop Yes, force-consent Yes, and Save commit only the explicitly changed values");
    }

    private static void CheckLegacyMigrationYesCancelNoSaveAndYesSave()
    {
        var yesCancelProfile = LegacyProfile("yes-cancel");
        using (var dialog = CreateEditor(yesCancelProfile))
        {
            var list = CompanionList(dialog);
            Drive(dialog, () =>
            {
                list.Items[0].Selected = true;
                ButtonByText(dialog, "기존 앱 설정 옮기기").PerformClick();
                ButtonByText(dialog, "취소").PerformClick();
            }, ("기존 앱 설정 변경", 0));
        }
        AssertLegacyUnchanged(yesCancelProfile, "migration Yes then parent Cancel keeps legacy consent unchanged");

        var noSaveProfile = LegacyProfile("no-save");
        using (var dialog = CreateEditor(noSaveProfile))
        {
            var list = CompanionList(dialog);
            Drive(dialog, () =>
            {
                list.Items[0].Selected = true;
                ButtonByText(dialog, "기존 앱 설정 옮기기").PerformClick();
                ButtonByText(dialog, "저장").PerformClick();
            }, ("기존 앱 설정 변경", 1));
        }
        AssertLegacyUnchanged(noSaveProfile, "migration No then parent Save does not escalate legacy normal-stop or force consent");

        var yesSaveProfile = LegacyProfile("yes-save");
        using (var dialog = CreateEditor(yesSaveProfile))
        {
            var list = CompanionList(dialog);
            Drive(dialog, () =>
            {
                list.Items[0].Selected = true;
                ButtonByText(dialog, "기존 앱 설정 옮기기").PerformClick();
                ButtonByText(dialog, "저장").PerformClick();
            }, ("기존 앱 설정 변경", 0));
        }
        var migrated = yesSaveProfile.Companions.Single();
        Check(migrated.StopWhenGameEnds && !migrated.ForceTerminateAfterGrace,
            "migration Yes then parent Save enables normal close only and keeps force consent off");
    }

    private static void CheckCleanupConsentRenderedBounds()
    {
        using var dialog = CreateProfileEditor();
        var cleanup = Field<CheckBox>(dialog, "cleanupDesktop");
        var layout = cleanup.Parent as TableLayoutPanel ?? throw new InvalidOperationException("Cleanup option is not in its table layout.");
        Drive(dialog, () =>
        {
            dialog.PerformLayout();
            var screen = Screen.FromControl(dialog);
            using var process = Process.GetCurrentProcess();
            Console.WriteLine($"UI_ENVIRONMENT OS={Environment.OSVersion.Version} Machine={Environment.MachineName} Architecture={RuntimeInformation.ProcessArchitecture} SessionId={process.SessionId} UserInteractive={Environment.UserInteractive}");
            Console.WriteLine($"UI_DISPLAY DeviceDpi={dialog.DeviceDpi} FormBounds={FormatBounds(dialog.Bounds)} Screen={screen.DeviceName} ScreenBounds={FormatBounds(screen.Bounds)} WorkingArea={FormatBounds(screen.WorkingArea)} Primary={screen.Primary} ScreenCount={Screen.AllScreens.Length}");
            var preferred = cleanup.GetPreferredSize(Size.Empty);
            var columns = layout.GetColumnWidths();
            var cell = layout.GetCellPosition(cleanup);
            var span = layout.GetColumnSpan(cleanup);
            var available = columns.Skip(cell.Column).Take(span).Sum() - cleanup.Margin.Horizontal;
            Check(cleanup.AutoSize && cleanup.Width >= preferred.Width && preferred.Width <= available,
                $"actual desktop-cleanup consent text fits its rendered cell at {dialog.DeviceDpi} DPI (preferred {preferred.Width}px, available {available}px)");
            Check(dialog.MinimumSize.Width >= 820 && dialog.ClientSize.Width >= layout.MinimumSize.Width,
                "profile dialog keeps a usable minimum width for long consent text at the active DPI");
            ButtonByText(dialog, "취소").PerformClick();
        });
    }

    private static GameProfile LegacyProfile(string id) => new()
    {
        Companions = [new Companion { Id = id, Executable = $@"C:\Apps\{id}.exe", Autostart = true,
            StopWhenGameEnds = false, ForceTerminateAfterGrace = false }]
    };

    private static void AssertLegacyUnchanged(GameProfile profile, string name)
    {
        var app = profile.Companions.Single();
        Check(app.Autostart && !app.StopWhenGameEnds && !app.ForceTerminateAfterGrace, name);
    }

    private static Form CreateEditor(GameProfile profile)
    {
        var type = typeof(MainForm).GetNestedType("CompanionEditorDialog", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Companion editor form was not found.");
        return (Form)(Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [profile], culture: null) ?? throw new InvalidOperationException("Companion editor could not be created."));
    }

    private static Form CreateProfileEditor()
    {
        var type = typeof(MainForm).GetNestedType("ProfileEditorDialog", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Profile editor form was not found.");
        return (Form)(Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [null], culture: null) ?? throw new InvalidOperationException("Profile editor could not be created."));
    }

    private static ListView CompanionList(Form form) => Field<ListView>(form, "list");

    private static T Field<T>(object instance, string name) where T : class
        => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field '{name}' was not found."));

    private static void Drive(Form form, Action action, params (string Title, int ButtonIndex)[] prompts)
    {
        var pendingPrompts = new Queue<(string Title, int ButtonIndex)>(prompts);
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        var driven = false;
        var elapsed = 0;
        timer.Tick += (_, _) =>
        {
            elapsed += timer.Interval;
            if (!driven)
            {
                driven = true;
                action();
            }
            else if (pendingPrompts.TryPeek(out var prompt))
            {
                var promptWindow = FindWindow(null, prompt.Title);
                if (promptWindow != IntPtr.Zero)
                {
                    var button = FindChildButton(promptWindow, prompt.ButtonIndex);
                    if (button == IntPtr.Zero) throw new InvalidOperationException($"Prompt button {prompt.ButtonIndex} was not found for '{prompt.Title}'.");
                    SendMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                    pendingPrompts.Dequeue();
                }
            }
            if (elapsed > 20000) throw new TimeoutException($"UI dialog flow timed out; pending prompts: {pendingPrompts.Count}.");
        };
        timer.Start();
        form.ShowDialog();
        timer.Stop();
        Check(pendingPrompts.Count == 0, "all expected consent prompts were exercised in the actual dialog");
    }

    private static IntPtr FindChildButton(IntPtr parent, int index)
    {
        var child = FindWindowEx(parent, IntPtr.Zero, "Button", null);
        for (var current = 0; child != IntPtr.Zero && current < index; current++)
            child = FindWindowEx(parent, child, "Button", null);
        return child;
    }

    private static Button ButtonByText(Control parent, string text)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Button button && button.Text == text) return button;
            try { return ButtonByText(control, text); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Button '{text}' was not found.");
    }

    private static string FormatBounds(System.Drawing.Rectangle bounds)
        => $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS {name}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr afterChild, string className, string? windowName);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
