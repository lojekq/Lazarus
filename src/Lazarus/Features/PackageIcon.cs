using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace Lazarus.Features;

/// <summary>
/// A packaged app's list icon from its AppxManifest.xml (Square44x44Logo), where ExtractIconEx on the exe gives a placeholder.
/// Frozen and cached by exe; null when the manifest or the image can't be read (the journal draws a glyph).
/// </summary>
public static class PackageIcon
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="aumid">"Family!AppId"; null takes the package's first application</param>
    public static ImageSource? Get(string exe, string? aumid) => Cache.GetOrAdd(exe + "|" + aumid, _ => Load(exe, aumid?.Split('!').LastOrDefault()));

    static ImageSource? Load(string exe, string? appId)
    {
        try
        {
            if (Logo(exe, appId) is not { } file) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = 24;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; } // an icon is cosmetic
    }

    /// <summary>The logo file for 24 px: targetsize-24 unplated, then plated, then the nearest scale, then the plain name.</summary>
    internal static string? Logo(string exe, string? appId)
    {
        var root = Path.GetDirectoryName(exe);
        for (var i = 0; root is not null && i < 4 && !File.Exists(Path.Combine(root, "AppxManifest.xml")); i++) root = Path.GetDirectoryName(root);
        if (root is null || !File.Exists(Path.Combine(root, "AppxManifest.xml"))) return null;

        var apps = XDocument.Load(Path.Combine(root, "AppxManifest.xml")).Descendants().Where(e => e.Name.LocalName == "Application").ToList();
        var app = apps.FirstOrDefault(a => (string?)a.Attribute("Id") == appId) ?? apps.FirstOrDefault();
        var logo = (string?)app?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")?.Attribute("Square44x44Logo");
        if (logo is null) return null;

        var dir = Path.Combine(root, Path.GetDirectoryName(logo) ?? "");
        var name = Path.GetFileNameWithoutExtension(logo);
        var ext = Path.GetExtension(logo);
        foreach (var q in new[] { ".targetsize-24_altform-unplated", ".targetsize-24", ".targetsize-32_altform-unplated", ".targetsize-32", ".scale-100", "" })
            if (File.Exists(Path.Combine(dir, name + q + ext))) return Path.Combine(dir, name + q + ext);
        return Directory.EnumerateFiles(dir, name + ".*" + ext).Where(f => !f.Contains("contrast-")).Order().FirstOrDefault();
    }
}
