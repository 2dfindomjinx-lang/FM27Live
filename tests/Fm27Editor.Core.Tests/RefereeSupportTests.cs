using Fm27Editor.Core.Officials;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// v1.4.0 showed no referees on Windows: the Mac merge loaded them only for profiles with an <c>officials</c> section, and no
/// Windows profile has one, because Windows uses the x86-64 default layout (2,078 referees read live on Game Pass 27.0.1 and
/// 27.1.0 with it). A Mac profile without the section has no known layout.
/// </summary>
public class RefereeSupportTests
{
    static IEnumerable<string> ProfileFiles() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "offsets"), "*.json").Select(Path.GetFileName)!;

    [Fact]
    public void Windows_profiles_read_referees_with_the_default_layout_on_windows()
    {
        var windows = ProfileFiles().Where(f => !f.Contains("-mac", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(windows);
        foreach (var file in windows)
        {
            var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", file));
            Assert.Null(profile.Officials);   // the default layout is what Windows relies on
            Assert.Equal(OperatingSystem.IsWindows(), RefereeDirectory.Supported(profile.Officials));
        }
    }

    [Fact]
    public void A_measured_officials_section_is_supported_everywhere()
    {
        var mac = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.0.1+2503222-mac.json"));
        Assert.NotNull(mac.Officials);
        Assert.True(RefereeDirectory.Supported(mac.Officials));
    }
}
