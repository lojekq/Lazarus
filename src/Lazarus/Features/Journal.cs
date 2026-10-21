using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lazarus.Shell;

namespace Lazarus.Features;

public enum EntryKind { App, Packaged, Explorer, Terminal, Console, Group }

/// <summary>A Windows Terminal tab: its shell (file name), WT profile GUID and folder; null folder = the profile's own.</summary>
public sealed record Tab(string Shell, string? Profile, string? Cwd);

/// <summary>
/// GetWindowPlacement: ShowCmd and Normal [left, top, right, bottom] in workspace coordinates, for SetWindowPlacement.
/// Rect is GetWindowRect (screen) at the close, for the "already in place" check.
/// </summary>
public sealed record Place(int ShowCmd, int[] Normal, int[] Rect);

/// <summary>
/// One closed window. Fields by kind (spec 03 §5): App and Console → CommandLine, WorkDir; Packaged → Aumid, CommandLine;
/// Explorer → Paths, Active, and TabOf for a tab closed in a window that stayed open; Terminal → Tabs;
/// Group → Items (three or more windows closed within 2 s: an Explorer restart, a crash).
/// </summary>
public sealed record Entry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset ClosedAt { get; init; }
    public EntryKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Exe { get; init; } = "";
    public Place? Placement { get; init; }
    public string? Monitor { get; init; }       // "DISPLAY2"
    public bool Elevated { get; init; }
    public string? CommandLine { get; init; }
    public string? WorkDir { get; init; }
    public string? Aumid { get; init; }
    public List<string>? Paths { get; init; }
    public string? Active { get; init; }
    public List<Tab>? Tabs { get; init; }
    public long? TabOf { get; init; }           // the Explorer window (HWND) the tab was closed in
    public List<Entry>? Items { get; init; }    // Group: newest first
    public string? Error { get; init; }         // the last restore failed: "unavailable", "failed"

    [JsonIgnore] public string ExeName => System.IO.Path.GetFileName(Exe);
}

/// <summary>
/// Closed windows, newest first: a stack with a size limit and a retention period. Saved to journal.json after every change
/// (atomic write); a corrupt file is renamed to journal.bad-&lt;time&gt;.json and the .bak copy or an empty journal is used.
/// Thread-safe: the tracker pushes, the UI reads and removes.
/// </summary>
public sealed class Journal
{
    public static string FilePath => Path.Combine(AppInfo.DataDir, "journal.json");

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() },
    };

    readonly Lock gate = new();
    List<Entry> entries = [];

    public int Limit { get; set; } = 30;
    public int RetentionDays { get; set; } = 7;

    /// <summary>null: memory only (--demo, "keep the journal between restarts" off).</summary>
    public string? File { get; set; }

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    /// <summary>Any thread. The UI re-reads <see cref="Entries"/>.</summary>
    public event Action? Changed;

    public IReadOnlyList<Entry> Entries { get { lock (gate) { Prune(); return entries.ToArray(); } } }

    /// <summary>Three or more entries closed within 2 s of each other become one Group entry ("restore all").</summary>
    public static readonly TimeSpan GroupSpan = TimeSpan.FromSeconds(2);

    public void Push(Entry e) => Change(() =>
    {
        if (e.Kind != EntryKind.Group && entries.FirstOrDefault() is { Kind: EntryKind.Group } g && Near(g.Items![0], e))
        {
            entries[0] = g with { ClosedAt = Max(g.ClosedAt, e.ClosedAt), Items = [.. g.Items!.Append(e).OrderByDescending(x => x.ClosedAt)] };
            return;
        }
        var near = e.Kind == EntryKind.Group ? [] : entries.TakeWhile(x => x.Kind != EntryKind.Group && Near(x, e)).ToList();
        if (near.Count < 2) { entries.Insert(0, e); return; }
        entries.RemoveRange(0, near.Count);
        var items = near.Append(e).OrderByDescending(x => x.ClosedAt).ToList();
        entries.Insert(0, new Entry { Kind = EntryKind.Group, ClosedAt = items[0].ClosedAt, Items = items });
    });

    static bool Near(Entry a, Entry b) => (a.ClosedAt - b.ClosedAt).Duration() <= GroupSpan;

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>The newest entry, removed; null if empty.</summary>
    public Entry? Pop()
    {
        Entry? top = null;
        Change(() =>
        {
            Prune();
            if (entries.Count == 0) return;
            top = entries[0];
            entries.RemoveAt(0);
        });
        return top;
    }

    /// <summary>Removes and returns the entry; null if it is gone already.</summary>
    public Entry? Take(string id)
    {
        Entry? e = null;
        Change(() =>
        {
            e = entries.FirstOrDefault(x => x.Id == id);
            if (e is not null) entries.Remove(e);
        });
        return e;
    }

    public void RemoveWhere(Func<Entry, bool> match) => Change(() => entries.RemoveAll(e => match(e)));

    public void Clear() => Change(entries.Clear);

    /// <summary>At start: the file's entries, pruned. Missing file → empty.</summary>
    public void Load()
    {
        if (File is null) return;
        try { lock (gate) entries = AtomicFile.Read(File, Parse) ?? []; }
        catch (Exception e) { Log.Error("journal not loaded", e); } // locked file: start empty, the next save overwrites nothing worse
        Change(() => { });
    }

    internal static List<Entry>? Parse(string json) => JsonSerializer.Deserialize<Saved>(json, Json)?.Entries;

    internal static string Serialize(IEnumerable<Entry> entries) => JsonSerializer.Serialize(new Saved(1, entries.ToList()), Json);

    sealed record Saved(int Version, List<Entry> Entries);

    void Change(Action change)
    {
        lock (gate) // the write too: two writers would race on journal.json.tmp, and an older list could land last
        {
            change();
            Prune();
            if (File is not null)
                try { AtomicFile.WriteAllText(File, Serialize(entries)); }
                catch (Exception e) { Log.Error("journal not saved", e); }
        }
        Changed?.Invoke();
    }

    void Prune()
    {
        var oldest = Now() - TimeSpan.FromDays(RetentionDays);
        entries.RemoveAll(e => e.ClosedAt < oldest);
        if (entries.Count > Limit) entries.RemoveRange(Limit, entries.Count - Limit);
    }
}
