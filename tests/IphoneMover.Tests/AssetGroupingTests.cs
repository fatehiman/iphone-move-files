using IphoneMover.Core;
using IphoneMover.Wpd;

namespace IphoneMover.Tests;

public sealed class AssetGroupingTests
{
    private static DeviceFile F(string folder, string name) => new(folder + "/" + name, name, folder, 1, null, null, true);

    [Fact]
    public void LivePhoto_EditedCopy_And_Aae_AreOneGroup()
    {
        var all = new[]
        {
            F(@"DCIM\100APPLE", "IMG_0001.HEIC"), F(@"DCIM\100APPLE", "IMG_0001.MOV"),
            F(@"DCIM\100APPLE", "IMG_E0001.HEIC"), F(@"DCIM\100APPLE", "IMG_0001.AAE"),
            F(@"DCIM\100APPLE", "IMG_0002.HEIC"), F(@"DCIM\101APPLE", "IMG_0001.HEIC"),
        };
        var groups = AssetGrouping.Expand([all[0]], all);
        Assert.Single(groups);
        Assert.Equal(4, groups[0].Count);
    }

    [Fact]
    public void SelectingTwoFilesOfOnePhoto_GivesOneGroup()
    {
        var all = new[] { F("A", "IMG_0005.JPG"), F("A", "IMG_0005.MOV"), F("A", "IMG_0006.JPG") };
        var groups = AssetGrouping.Expand([all[0], all[1], all[2]], all);
        Assert.Equal(2, groups.Count);
    }
}
