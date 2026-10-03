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

    [Theory]
    [InlineData(unchecked((int)0x802A0002), true)]  // the error seen when the session broke
    [InlineData(unchecked((int)0x8007048F), true)]  // device not connected
    [InlineData(unchecked((int)0x80070002), false)] // file not found: not a connection problem
    [InlineData(unchecked((int)0x80070005), false)] // access denied
    [InlineData(0, false)]
    public void IsConnectionLost(int hr, bool expected)
    {
        Assert.Equal(expected, IphoneMover.Wpd.HResult.IsConnectionLost(hr));
    }

    [Fact]
    public void SafeName_ReplacesInvalidCharacters()
    {
        Assert.Equal("a_b", MoveEngine.SafeName("a:b"));
        Assert.Equal("_", MoveEngine.SafeName(".."));
    }
}
