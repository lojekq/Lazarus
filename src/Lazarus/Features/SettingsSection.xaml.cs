using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lazarus.Shell;

namespace Lazarus.Features;

/// <summary>Lazarus' own block on the settings page (spec 03 §7): hotkeys, journal, exclusions, trusted programs, the PowerShell hook.</summary>
public partial class SettingsSection
{
    public SettingsSection()
    {
        InitializeComponent();
        var s = Settings.Current;

        HotkeyBox.Hotkey = s.Hotkey;
        HotkeyBox.Status = Engine.HotkeyStatus;
        HotkeyBox.Changed += (_, _) =>
        {
            s.Hotkey = HotkeyBox.Hotkey;
            Settings.Save();
            HotkeyBox.Status = Engine.ApplyHotkey();
        };

        ContextSwitch.IsChecked = s.ContextCtrlShiftT;
        ContextSwitch.Click += (_, _) => { s.ContextCtrlShiftT = ContextSwitch.IsChecked == true; Settings.Save(); };

        SizeBox.Value = s.JournalSize;
        DaysBox.Value = s.RetentionDays;
        PersistSwitch.IsChecked = s.PersistJournal;
        SizeBox.ValueChanged += (_, _) => { if (SizeBox.Value is { } v) { s.JournalSize = (int)v; Settings.Save(); Engine.ApplyJournalSettings(); } };
        DaysBox.ValueChanged += (_, _) => { if (DaysBox.Value is { } v) { s.RetentionDays = (int)v; Settings.Save(); Engine.ApplyJournalSettings(); } };
        PersistSwitch.Click += (_, _) => { s.PersistJournal = PersistSwitch.IsChecked == true; Settings.Save(); Engine.ApplyJournalSettings(); };
        ClearButton.Click += (_, _) => Engine.Journal.Clear();

        AddButton.Click += (_, _) => AddExclusion(ExclusionBox.Text);
        ExclusionBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddExclusion(ExclusionBox.Text); };
        FromWindowsButton.Click += (_, _) =>
        {
            WindowsMenu.Items.Clear();
            // Names from the process table: no handle to any process (games stay untouched, spec 03 §8.2)
            var table = Process.GetProcesses().ToDictionary(p => p.Id, p => { using (p) return p.ProcessName + ".exe"; });
            var names = TopWindows.List().Select(w => table.GetValueOrDefault(w.Pid)).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names) WindowsMenu.Items.Add(Tray.Item(n, () => AddExclusion(n)));
            WindowsMenu.PlacementTarget = FromWindowsButton;
            WindowsMenu.IsOpen = true;
        };
        RefreshLists();

        HookBox.Text = PowerShellHook.Line;
        CopyButton.Click += (_, _) => Clipboard.SetText(PowerShellHook.Line);
        Loaded += (_, _) =>
        {
            PwshStatus.Text = PwshHookStatus();
            // The README screenshot shows Lazarus' own block, not the shell's language and theme
            if (Args.Current.Screenshot is not null && FindScroll(this) is { } sv)
                sv.ScrollToVerticalOffset(TransformToAncestor((Visual)sv.Content).Transform(new Point()).Y - 12);
        };
    }

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
    }

    void AddExclusion(string? pattern)
    {
        pattern = pattern?.Trim();
        if (string.IsNullOrEmpty(pattern)) return;
        var s = Settings.Current;
        if (!s.Exclusions.Contains(pattern, StringComparer.OrdinalIgnoreCase)) s.Exclusions.Add(pattern);
        Settings.Save();
        Engine.Tracker?.Configure(s.Exclusions);
        ExclusionBox.Text = "";
        RefreshLists();
    }

    void RefreshLists()
    {
        var s = Settings.Current;
        ExclusionList.ItemsSource = s.Exclusions.Select(p => Row(p, () => { s.Exclusions.Remove(p); Settings.Save(); Engine.Tracker?.Configure(s.Exclusions); RefreshLists(); })).ToList();
        TrustedList.ItemsSource = s.TrustedExes.Select(p => Row(p, () => { s.TrustedExes.Remove(p); Settings.Save(); RefreshLists(); })).ToList();
        TrustedEmpty.Visibility = s.TrustedExes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static Grid Row(string text, Action remove)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Wpf.Ui.Controls.TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Wpf.Ui.Controls.Button { Content = Loc.T("Set.Remove"), Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Dismiss16) };
        b.Click += (_, _) => remove();
        Grid.SetColumn(b, 1);
        g.Children.Add(b);
        return g;
    }

    /// <summary>"Hook found in 2 of 3 open pwsh": LAZARUS_CWD in each pwsh's environment.</summary>
    static string PwshHookStatus()
    {
        if (Args.Current.Demo) return string.Format(Loc.T("Set.Pwsh.Status"), 2, 3);
        var pids = Process.GetProcessesByName("pwsh").Select(p => { using (p) return p.Id; }).ToList();
        if (pids.Count == 0) return Loc.T("Set.Pwsh.None");
        return string.Format(Loc.T("Set.Pwsh.Status"), pids.Count(pid => Peb.Read(pid)?.LazarusCwd is not null), pids.Count);
    }
}
