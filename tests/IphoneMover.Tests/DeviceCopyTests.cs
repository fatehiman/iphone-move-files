using IphoneMover.Core;
using IphoneMover.Wpd;
using Xunit.Abstractions;

namespace IphoneMover.Tests;

/// <summary>
/// Copy test against a real connected iPhone. It never deletes anything.
/// Runs only when IPHONE_COPY_TEST is set to a destination folder, for example:
///   set IPHONE_COPY_TEST=D:\temp\copytest &amp;&amp; dotnet test --filter DeviceCopyTests
/// </summary>
public sealed class DeviceCopyTests(ITestOutputHelper output)
{
    [Fact]
    public void CopyFewFiles_FromConnectedIphone_WithoutDelete()
    {
        string? dest = Environment.GetEnvironmentVariable("IPHONE_COPY_TEST");
        if (string.IsNullOrEmpty(dest))
            return;

        var info = WpdDevice.ListDevices().FirstOrDefault(d => d.LooksLikeApple);
        Assert.NotNull(info);
        using var device = WpdDevice.Open(info);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = device.ListFiles(null, CancellationToken.None);
        output.WriteLine($"{files.Count} files listed in {sw.Elapsed.TotalSeconds:0.0}s");

        // One JPG, one HEIC and one MOV (the smallest of each), so the test is quick.
        var pick = new[] { ".JPG", ".HEIC", ".MOV" }
            .Select(ext => files.Where(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && f.Size > 0)
                                .OrderBy(f => f.Size).FirstOrDefault())
            .OfType<DeviceFile>()
            .ToList();
        Assert.NotEmpty(pick);

        var reports = new List<FileReport>();
        var engine = new MoveEngine(device, dest, deleteAfterCopy: false,
            new SyncProgress<FileReport>(reports.Add), _ => { });
        var groups = pick.Select(f => new List<DeviceFile> { f }).ToList();
        var summary = engine.Run(groups, CancellationToken.None);

        foreach (var r in reports.Where(r => r.State != FileState.Copying))
            output.WriteLine($"{r.File.DevicePath} {r.File.Size:N0} -> {r.State}: {r.Detail}");
        Assert.Equal(0, summary.Failed);
        Assert.Equal(pick.Count, summary.CopiedOnly);
        foreach (var f in pick)
            Assert.True(device.Exists(f.ObjectId), "file must still be on the phone");
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
