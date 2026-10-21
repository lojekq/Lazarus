using Lazarus.Features;

namespace Lazarus.Tests;

[TestClass]
public class PackageIconTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void LogoFromTheManifestPrefersTargetsize24Unplated()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lazarus.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "Images"));
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications>
                <Application Id="Other"><uap:VisualElements Square44x44Logo="Images\Other.png" /></Application>
                <Application Id="App"><uap:VisualElements Square44x44Logo="Images\Square44x44Logo.png" /></Application>
              </Applications>
            </Package>
            """);
        foreach (var f in new[] { "Square44x44Logo.scale-100.png", "Square44x44Logo.targetsize-24.png", "Square44x44Logo.targetsize-24_altform-unplated.png", "Other.scale-100.png" })
            File.WriteAllBytes(Path.Combine(root, "Images", f), []);
        var exe = Path.Combine(root, "App", "Tool.exe");

        Assert.AreEqual(Path.Combine(root, "Images", "Square44x44Logo.targetsize-24_altform-unplated.png"), PackageIcon.Logo(exe, "App"));
        Assert.AreEqual(Path.Combine(root, "Images", "Other.scale-100.png"), PackageIcon.Logo(exe, "Other"));
        Assert.AreEqual(Path.Combine(root, "Images", "Other.scale-100.png"), PackageIcon.Logo(exe, null), "no app id: the first application");
        Assert.IsNull(PackageIcon.Logo(@"C:\Windows\System32\cmd.exe", null), "not packaged");
    }
}
