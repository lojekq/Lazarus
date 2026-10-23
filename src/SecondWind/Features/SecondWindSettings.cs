namespace SecondWind.Shell;

// SecondWind's own settings.json fields (spec 03 §5 "Данные")
public sealed partial class Settings
{
    public string? Hotkey { get; set; } = "Ctrl+Shift+Win+T";
    public bool ContextCtrlShiftT { get; set; } = true;   // Ctrl+Shift+T while Explorer, the desktop or the taskbar is active
    public int JournalSize { get; set; } = 30;            // 10–100
    public int RetentionDays { get; set; } = 7;           // 1–30
    public bool PersistJournal { get; set; } = true;
    public List<string> Exclusions { get; set; } = [];    // the user's own: exe names and path masks, on top of WindowFilter.BuiltIn
    public List<string> TrustedExes { get; set; } = [];   // full exe paths that never ask (except "as administrator")
}
