using Lazarus.Shell;

namespace Lazarus.Features;

/// <summary>
/// "Start this?" before a risky restore (spec 03 §7): the exe, the full command in a monospace box, the folder and why it asks.
/// "Don't ask again" trusts the exe from then on; entries "as administrator" have no such box.
/// </summary>
public partial class ConfirmDialog
{
    public ConfirmDialog(Entry e, IReadOnlyList<string> reasons)
    {
        InitializeComponent();
        Theme.Apply(this, Settings.Current.Theme);
        ExeText.Text = e.Exe;
        CommandBox.Text = Restorer.CommandText(e);
        FolderText.Text = e.WorkDir ?? "";
        if (e.WorkDir is null) FolderPanel.Visibility = System.Windows.Visibility.Collapsed;
        WhyText.Text = string.Join(Environment.NewLine, reasons.Select(r => Loc.T("Reason." + r)));
        TrustBox.Content = string.Format(Loc.T("Confirm.Trust"), e.ExeName);
        if (e.Elevated) TrustBox.Visibility = System.Windows.Visibility.Collapsed;
        RunButton.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => { Activate(); RunButton.Focus(); };
    }

    /// <summary>UI thread. True = run; "don't ask again" is saved here.</summary>
    public static bool Ask(Entry e, IReadOnlyList<string> reasons)
    {
        var d = new ConfirmDialog(e, reasons);
        if (d.ShowDialog() != true) return false;
        if (d.TrustBox.IsChecked == true && !Settings.Current.TrustedExes.Contains(e.Exe, StringComparer.OrdinalIgnoreCase))
        {
            Settings.Current.TrustedExes.Add(e.Exe);
            Settings.Save();
        }
        return true;
    }
}
