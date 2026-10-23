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

        ConnectButton.Click += async (_, _) => await ChangeHook(connect: true);
        DisconnectButton.Click += async (_, _) => await ChangeHook(connect: false);
        Loaded += async (_, _) =>
        {
            await RefreshHook();
            // The README screenshot shows Lazarus' own block, not the shell's language and theme
            if (Args.Current.Screenshot is not null)
                Dispatcher.BeginInvoke(() =>
                {
                    // The page's own ScrollViewer may not scroll (NavigationView gives it all the height): take the one that does
                    if (FindScroll(this) is { } sv)
                        sv.ScrollToVerticalOffset(sv.VerticalOffset + TransformToAncestor(sv).Transform(new Point()).Y - 12);
                }, System.Windows.Threading.DispatcherPriority.ContextIdle); // after layout, or there is nothing to scroll yet
        };
    }

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer { ScrollableHeight: > 0 } sv) return sv;
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

    /// <summary>"Windows PowerShell 5.1: connected · PowerShell 7: not connected · open windows with it: 2", and the policy note.</summary>
    async Task RefreshHook()
    {
        if (Args.Current.Demo)
        {
            PwshStatus.Text = $"Windows PowerShell 5.1: {Loc.T("Set.Pwsh.On")} · PowerShell 7: {Loc.T("Set.Pwsh.On")} · {string.Format(Loc.T("Set.Pwsh.Running"), 2)}";
            return;
        }
        PwshStatus.Text = Loc.T("Set.Pwsh.Checking");
        ConnectButton.IsEnabled = DisconnectButton.IsEnabled = false;
        var hosts = await PowerShellHook.FindAsync();
        var dev = AppInfo.IsDev; // a Debug build or a --data-dir check run: the profiles are the owner's real ones, read only
        var (connected, running) = await Task.Run(() => (
            hosts.Select(h => PowerShellHook.IsConnected(h.Profile)).ToList(),
            // Shells running the hook: LAZARUS_HOOK (or the 1.0 one-liner's LAZARUS_CWD) in their environment
            new[] { "powershell", "pwsh" }.SelectMany(Process.GetProcessesByName).Count(p => { using (p) return Peb.Read(p.Id)?.Hooked == true; })));
        if (hosts.Count == 0) { PwshStatus.Text = Loc.T("Set.Pwsh.None"); return; }
        PwshStatus.Text = string.Join(" · ", hosts.Select((h, i) =>
            $"{h.Name}: {(h.PolicyBlocks ? string.Format(Loc.T("Set.Pwsh.Blocked"), h.Policy) : Loc.T(connected[i] ? "Set.Pwsh.On" : "Set.Pwsh.Off"))}")
            .Append(string.Format(Loc.T("Set.Pwsh.Running"), running)));
        PwshPolicy.Text = Loc.T("Set.Pwsh.PolicyNote");
        PwshPolicy.Visibility = hosts.Any(h => h.PolicyBlocks) ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = !dev && hosts.Where((h, i) => !h.PolicyBlocks && !connected[i]).Any();
        DisconnectButton.IsEnabled = !dev && connected.Any(c => c);
    }

    /// <summary>Connect writes the block into each PowerShell whose policy loads profiles; Disconnect takes it out of all of them.</summary>
    async Task ChangeHook(bool connect)
    {
        if (Args.Current.Demo || AppInfo.IsDev) return;
        ConnectButton.IsEnabled = DisconnectButton.IsEnabled = false;
        var hosts = await PowerShellHook.FindAsync();
        var error = await Task.Run(() =>
        {
            foreach (var h in hosts.Where(h => !connect || !h.PolicyBlocks))
                try { PowerShellHook.Edit(h.Profile, connect); }
                catch (Exception e) { Log.Warn("PowerShell profile not changed", e); return e.Message; }
            return null;
        });
        await RefreshHook();
        if (error is not null) { PwshPolicy.Text = error; PwshPolicy.Visibility = Visibility.Visible; }
    }
}
