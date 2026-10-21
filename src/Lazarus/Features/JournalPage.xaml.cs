using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Lazarus.Shell;

namespace Lazarus.Features;

/// <summary>The main page: closed windows, newest first, with search, restore, delete and "don't remember".</summary>
public partial class JournalPage
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int ticks;

    /// <summary>The page on screen, for the --autoplay scenario.</summary>
    public static JournalPage? Current { get; private set; }

    public JournalPage()
    {
        InitializeComponent();
        HotkeyHint.Text = Engine.HotkeyText;
        EmptyText.Text = string.Format(Loc.T("Journal.Empty"), Engine.HotkeyText);

        void Changed() => Dispatcher.BeginInvoke(Refresh);
        Loaded += (_, _) => { Current = this; Engine.Journal.Changed += Changed; timer.Start(); Refresh(); };
        Unloaded += (_, _) => { if (Current == this) Current = null; Engine.Journal.Changed -= Changed; timer.Stop(); };
        timer.Tick += (_, _) => { UpdateStatus(); if (++ticks % 30 == 0) Refresh(); }; // "2 min ago" moves on

        Search.TextChanged += (_, _) => Refresh();
        RestoreButton.Click += (_, _) => Engine.RestoreLast();
        List.MouseDoubleClick += (_, e) => { if (Selected is { } v && e.OriginalSource is FrameworkElement { DataContext: EntryView }) Engine.Restore(v.Entry.Id); };
        List.KeyDown += (_, e) =>
        {
            if (Selected is not { } v) return;
            if (e.Key == Key.Enter) Engine.Restore(v.Entry.Id);
            else if (e.Key == Key.Delete) Engine.Journal.Take(v.Entry.Id);
            else return;
            e.Handled = true;
        };

        Menu.Opened += (_, _) =>
        {
            var e = Selected?.Entry;
            foreach (var item in new[] { MenuRestore, MenuCopy, MenuFolder, MenuExclude, MenuDelete }) item.IsEnabled = e is not null;
            MenuOpenAll.Visibility = e is { Kind: EntryKind.Explorer, Paths.Count: > 1 } ? Visibility.Visible : Visibility.Collapsed;
            MenuExclude.Visibility = e is { Kind: EntryKind.Explorer or EntryKind.Terminal or EntryKind.Group } ? Visibility.Collapsed : Visibility.Visible;
            MenuCopy.Visibility = MenuFolder.Visibility = e is { Kind: EntryKind.Group } ? Visibility.Collapsed : Visibility.Visible;
        };
        MenuRestore.Click += (_, _) => { if (Selected is { } v) Engine.Restore(v.Entry.Id); };
        MenuOpenAll.Click += (_, _) => { if (Selected is { } v) Engine.OpenAll(v.Entry); };
        MenuCopy.Click += (_, _) => { if (Selected is { } v) Clipboard.SetText(Restorer.CommandText(v.Entry)); };
        MenuFolder.Click += (_, _) =>
        {
            if (Selected is not { } v || Args.Current.Demo) return;
            var dir = Path.GetDirectoryName(v.Entry.Exe);
            if (File.Exists(v.Entry.Exe)) Process.Start("explorer.exe", $"/select,\"{v.Entry.Exe}\"");
            else if (Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        };
        MenuExclude.Click += (_, _) => { if (Selected is { } v) Exclude(v.Entry.ExeName); };
        MenuDelete.Click += (_, _) => { if (Selected is { } v) Engine.Journal.Take(v.Entry.Id); };
    }

    EntryView? Selected => List.SelectedItem as EntryView;

    // For --autoplay
    public void Type(string text) => Search.Text = text;
    public void Select(int index) { List.SelectedIndex = index; List.ScrollIntoView(List.SelectedItem); }
    public void ShowMenu(bool open)
    {
        if (open && List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) is FrameworkElement row)
        {
            Menu.PlacementTarget = row;
            Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            Menu.HorizontalOffset = 120;
        }
        Menu.IsOpen = open;
    }

    /// <summary>"Don't remember this program": the exe name joins the exclusions and its entries leave the journal.</summary>
    static void Exclude(string exe)
    {
        if (!Settings.Current.Exclusions.Contains(exe, StringComparer.OrdinalIgnoreCase)) Settings.Current.Exclusions.Add(exe);
        Settings.Save();
        Engine.Tracker?.Configure(Settings.Current.Exclusions);
        Engine.Journal.RemoveWhere(e => e.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase));
    }

    void Refresh()
    {
        var now = DateTimeOffset.Now;
        var q = Search.Text?.Trim() ?? "";
        var downloads = Engine.DownloadsFolder;
        var selected = Selected?.Entry.Id;
        var items = Engine.Journal.Entries
            .Select(e => new EntryView(e, now, RiskClassifier.Reasons(e, Settings.Current.TrustedExes, WindowFilter.TempDirs, downloads)))
            .Where(v => v.Matches(q)).ToList();
        List.ItemsSource = items;
        List.SelectedItem = items.FirstOrDefault(v => v.Entry.Id == selected);
        EmptyText.Visibility = items.Count == 0 && q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var t = Engine.Tracker;
        Status.Text =
            t is { IsPaused: true, PausedUntil: { } until } ? View.PauseText(until)
            : !Args.Current.Demo && ExplorerTracker.Instance.Age > TimeSpan.FromSeconds(10) ? Loc.T("Journal.ExplorerHung")
            : View.Plural("Journal.Watching", Args.Current.Demo ? 23 : t?.Watched ?? 0);
    }
}
