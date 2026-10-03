using IphoneMover.Core;

namespace IphoneMover.Tests;

public sealed class MoveEngineTests
{
    [Theory]
    [InlineData(@"DCIM\202605__", @"D:\Photos\202605__")]
    [InlineData(@"DCIM\202512_a", @"D:\Photos\202512_a")]
    [InlineData(@"DCIM\100APPLE", @"D:\Photos\100APPLE")]
    [InlineData(@"202605__", @"D:\Photos\202605__")]
    [InlineData("", @"D:\Photos")]
    public void TargetFolder_UsesExactPhoneFolderName_WithoutDcim(string phoneFolder, string expected)
    {
        Assert.Equal(expected, MoveEngine.TargetFolder(@"D:\Photos", phoneFolder));
    }

    [Fact]
    public void SafeName_ReplacesInvalidCharacters()
    {
        Assert.Equal("a_b", MoveEngine.SafeName("a:b"));
        Assert.Equal("_", MoveEngine.SafeName(".."));
    }
}
