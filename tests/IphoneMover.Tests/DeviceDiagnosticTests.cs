using System.Buffers.Binary;
using System.Text;
using IphoneMover.Wpd;
using Xunit.Abstractions;

namespace IphoneMover.Tests;

/// <summary>
/// Read-only diagnostic for delete problems. Runs only when IPHONE_DIAG=1.
/// It prints what the phone and the Windows driver say about deleting. It changes nothing.
/// </summary>
public sealed class DeviceDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public void PrintDeleteDiagnostics()
    {
        if (Environment.GetEnvironmentVariable("IPHONE_DIAG") != "1")
            return;

        var info = WpdDevice.ListDevices().First(d => d.LooksLikeApple);
        output.WriteLine($"device: {info} id={info.Id}");
        using var device = WpdDevice.Open(info);
        var files = device.ListFiles(null, CancellationToken.None);
        output.WriteLine($"files: {files.Count}, CanDelete=true: {files.Count(f => f.CanDelete)}, false: {files.Count(f => !f.CanDelete)}");

        foreach (var line in device.Describe(files.Take(2).Select(f => f.ObjectId)))
            output.WriteLine(line);

        // How does the phone answer GetObjectInfo for an existing and a non-existing handle?
        output.WriteLine($"ExistsOnPhone({files[0].ObjectId}) = {device.ExistsOnPhone(files[0].ObjectId)}");
        WpdDevice.TryGetHandle(files[0].ObjectId, out uint h);
        output.WriteLine("GetObjectInfo existing: " + device.MtpReadCommand(0x1008, h));
        output.WriteLine("GetObjectInfo 0x0FFFFFF0: " + device.MtpReadCommand(0x1008, 0x0FFFFFF0));
        output.WriteLine($"ExistsOnPhone(o0FFFFFF0) = {device.ExistsOnPhone("o0FFFFFF0")}");

        // Reconnect: a new session must work, and the object ids (MTP handles) must stay the same.
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        bool ok = device.Reconnect();
        output.WriteLine($"Reconnect = {ok} in {sw2.ElapsedMilliseconds} ms; " +
                         $"ExistsOnPhone({files[0].ObjectId}) after reconnect = {device.ExistsOnPhone(files[0].ObjectId)}");
        using (var ms = new MemoryStream())
        {
            var small = files.Where(f => f.Size > 0).MinBy(f => f.Size)!;
            long n = device.Download(small.ObjectId, ms, null, CancellationToken.None);
            output.WriteLine($"Download {small.DevicePath} after reconnect: {n} of {small.Size} bytes");
        }

        // PTP GetDeviceInfo (0x1001): which operations does the phone support?
        var di = device.MtpReadCommand(0x1001);
        output.WriteLine("GetDeviceInfo: " + di);
        if (di.Data.Length > 0)
            output.WriteLine(ParseDeviceInfo(di.Data));

        // PTP GetStorageIDs (0x1004) and GetStorageInfo (0x1005): the phone's own access capability.
        var ids = device.MtpReadCommand(0x1004);
        output.WriteLine("GetStorageIDs: " + ids);
        if (ids.Data.Length >= 4)
        {
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(ids.Data);
            for (int i = 0; i < n; i++)
            {
                uint sid = BinaryPrimitives.ReadUInt32LittleEndian(ids.Data.AsSpan(4 + i * 4));
                var si = device.MtpReadCommand(0x1005, sid);
                string access = si.Data.Length >= 6
                    ? BinaryPrimitives.ReadUInt16LittleEndian(si.Data.AsSpan(4)) switch
                    {
                        0 => "read/write",
                        1 => "read-only, no delete",
                        2 => "read-only, delete allowed",
                        var x => x.ToString(),
                    }
                    : "?";
                output.WriteLine($"storage 0x{sid:X8}: {si} accessCapability={access}");
            }
        }
    }

    private static string ParseDeviceInfo(byte[] d)
    {
        int pos = 0;
        ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos)); pos += 2; return v; }
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos)); pos += 4; return v; }
        string Str()
        {
            int n = d[pos++];
            string s = n == 0 ? "" : Encoding.Unicode.GetString(d, pos, n * 2).TrimEnd('\0');
            pos += n * 2;
            return s;
        }
        ushort[] Arr16() { uint n = U32(); var a = new ushort[n]; for (int i = 0; i < n; i++) a[i] = U16(); return a; }

        var sb = new StringBuilder();
        sb.AppendLine($"  standardVersion={U16()} vendorExtId=0x{U32():X} vendorExtVersion={U16()} vendorExt='{Str()}' functionalMode={U16()}");
        var ops = Arr16();
        sb.AppendLine("  operations: " + string.Join(" ", ops.Select(o => $"{o:X4}")));
        sb.AppendLine($"  DeleteObject (100B) supported: {ops.Contains((ushort)0x100B)}");
        var events = Arr16();
        var devProps = Arr16();
        var captureFormats = Arr16();
        var imageFormats = Arr16();
        sb.AppendLine($"  manufacturer='{Str()}' model='{Str()}' version='{Str()}'");
        return sb.ToString();
    }
}
