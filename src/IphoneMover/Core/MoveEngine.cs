using System.Globalization;
using System.Text;
using IphoneMover.Wpd;

namespace IphoneMover.Core;

internal enum FileState
{
    Copying,
    Moved,          // copied, checked, deleted from phone
    Copied,         // copied and checked, not deleted (by choice or because of a rule)
    Failed,         // copy, check or delete failed; the file is still on the phone
}

internal sealed record FileReport(DeviceFile File, FileState State, string Detail, string? PcPath);

internal sealed class MoveSummary
{
    public int Moved;
    public int CopiedOnly;
    public int Failed;
    public long Bytes;
}

/// <summary>
/// Moves files from the phone to the PC. For every asset group:
/// 1. copy each file to "&lt;name&gt;.part", 2. check size and format, 3. rename to the final name,
/// 4. only if every file of the group passed: delete the group from the phone.
/// </summary>
internal sealed class MoveEngine(
    WpdDevice device,
    string destinationRoot,
    bool deleteAfterCopy,
    IProgress<FileReport> report,
    Action<long> onBytes,
    Func<string, bool>? askUser = null,
    Action<string>? info = null)
{
    /// <summary>The session with the phone broke in the middle of a photo.</summary>
    private sealed class ConnectionLostException(int hr, Exception? inner = null)
        : Exception("connection to the iPhone lost: " + WpdException.Describe(hr), inner);

    /// <summary>How often one photo is tried again after a reconnect before it counts as failed.</summary>
    public const int MaxReconnectsPerPhoto = 3;
    private static readonly int[] ReconnectDelaysSeconds = [2, 5, 10, 20, 30];

    private bool reconnectAllowed;

    /// <summary>Pause when the destination drive has less free space than this.</summary>
    public const long MinFreeSpace = 1L << 30; // 1 GB

    public const string LogFileName = "iphone-mover-log.csv";

    private const string KeepOriginalsHint =
        " Tip: on the iPhone set Settings > Photos > Transfer to Mac or PC = \"Keep Originals\".";
    private const string ReadOnlyHint =
        " The phone refused. Possible reasons: iCloud Photos is on, or the photo was synced from a computer (iTunes).";

    /// <summary>Stop the run when this many files fail one after another (phone unplugged, locked, ...).</summary>
    public const int MaxFailuresInRow = 10;

    private long bytesDone;
    private int failuresInRow;
    private readonly Dictionary<string, StreamWriter> logs = new(StringComparer.OrdinalIgnoreCase);

    public MoveSummary Run(IReadOnlyList<List<DeviceFile>> groups, CancellationToken ct)
    {
        var summary = new MoveSummary();
        Directory.CreateDirectory(destinationRoot);
        try
        {
            foreach (var group in groups)
            {
                for (int attempt = 1; ; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    WaitForFreeSpace(group, ct);
                    reconnectAllowed = attempt <= MaxReconnectsPerPhoto;
                    try
                    {
                        RunGroup(group, summary, ct);
                        break;
                    }
                    catch (ConnectionLostException ex)
                    {
                        // Files already copied are found on the PC next time ("already on PC"), so repeating is safe.
                        info?.Invoke($"{group[0].DevicePath}: {ex.Message}. Reconnecting...");
                        Reconnect(ct);
                        failuresInRow = 0;
                    }
                }
                if (failuresInRow >= MaxFailuresInRow)
                    throw new IOException($"Stopped: {failuresInRow} files failed one after another. " +
                                          "Is the iPhone still connected and unlocked? Start the move again to continue.");
            }
        }
        finally
        {
            foreach (var w in logs.Values)
                w.Dispose();
            logs.Clear();
        }
        return summary;
    }

    /// <summary>
    /// Before each photo: when the destination drive has less than 1 GB free (after this photo),
    /// pause and ask the user to free space. Retry checks again; Stop ends the run.
    /// </summary>
    private void WaitForFreeSpace(List<DeviceFile> group, CancellationToken ct)
    {
        long need = group.Sum(f => Math.Max(0, f.Size));
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destinationRoot))!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long free = drive.AvailableFreeSpace;
            if (free - need >= MinFreeSpace)
                return;
            string message =
                $"The destination drive {drive.Name} has only {free / (1024.0 * 1024 * 1024):0.00} GB free.\n\n" +
                "The move is paused. Please free some space on this drive, then click Retry.\n" +
                "Click Cancel to stop the move (you can continue later).";
            if (askUser is null || !askUser(message))
                throw new OperationCanceledException("Stopped: not enough free space on " + drive.Name);
        }
    }

    /// <summary>
    /// Opens a new session with the phone: first automatically a few times (with waits),
    /// then by asking the user to unlock or replug the phone (Retry / Cancel).
    /// </summary>
    private void Reconnect(CancellationToken ct)
    {
        foreach (int seconds in ReconnectDelaysSeconds)
        {
            if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))
                ct.ThrowIfCancellationRequested();
            if (device.Reconnect())
            {
                info?.Invoke("Reconnected to the iPhone. Continuing.");
                return;
            }
        }
        while (true)
        {
            const string message =
                "The connection to the iPhone was lost, and it did not come back by itself.\n\n" +
                "1. Unlock the iPhone.\n" +
                "2. If that does not help: unplug the cable, plug it in again, and tap \"Trust\" on the iPhone.\n\n" +
                "Then click Retry. The move continues with the same photo.\n" +
                "Click Cancel to stop the move (you can continue later).";
            if (askUser is null || !askUser(message))
                throw new OperationCanceledException("Stopped: the connection to the iPhone was lost.");
            ct.ThrowIfCancellationRequested();
            if (device.Reconnect())
            {
                info?.Invoke("Reconnected to the iPhone. Continuing.");
                return;
            }
        }
    }

    private static int HResultOf(Exception ex) => ex is WpdException w ? w.HResult32 : ex.HResult;

    /// <summary>One photo: copy and check all its files, then delete them from the phone.</summary>
    private void RunGroup(List<DeviceFile> group, MoveSummary summary, CancellationToken ct)
    {
        // Step 1-3: copy and check every file of the group.
        var copied = new List<(DeviceFile File, string PcPath, CheckResult Check)>();
        bool groupOk = true;
        foreach (var file in group)
        {
            ct.ThrowIfCancellationRequested();
            report.Report(new FileReport(file, FileState.Copying, "copying...", null));
            try
            {
                var (pcPath, check, note) = CopyAndCheck(file, ct);
                copied.Add((file, pcPath, check));
                if (check.Status != CheckStatus.Valid)
                    groupOk = false;
                summary.Bytes += file.Size;
                failuresInRow = 0;
                WriteLog(file, pcPath, "copied", note + check.Detail);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (reconnectAllowed && HResult.IsConnectionLost(HResultOf(ex)))
            {
                throw new ConnectionLostException(HResultOf(ex), ex);
            }
            catch (Exception ex)
            {
                groupOk = false;
                summary.Failed++;
                failuresInRow++;
                report.Report(new FileReport(file, FileState.Failed, ex.Message, null));
                WriteLog(file, "", "FAILED", ex.Message);
            }
        }

        // Step 4: delete from the phone only when the whole group is safe on the PC.
        foreach (var (file, pcPath, check) in copied)
        {
            if (!deleteAfterCopy)
            {
                summary.CopiedOnly++;
                report.Report(new FileReport(file, FileState.Copied, "copied, " + check.Detail, pcPath));
                continue;
            }
            if (!groupOk)
            {
                summary.CopiedOnly++;
                string why = check.Status == CheckStatus.NotSupported
                    ? $"copied but NOT deleted: {check.Detail}"
                    : "copied but NOT deleted: another file of the same photo failed";
                report.Report(new FileReport(file, FileState.Copied, why, pcPath));
                WriteLog(file, pcPath, "kept on phone", why);
                continue;
            }

            string? error = DeleteFromPhone(file);
            if (error is null)
            {
                summary.Moved++;
                report.Report(new FileReport(file, FileState.Moved, "moved, " + check.Detail, pcPath));
                WriteLog(file, pcPath, "deleted from phone", "");
            }
            else
            {
                summary.Failed++;
                failuresInRow++;
                report.Report(new FileReport(file, FileState.Failed, "copied, but " + error, pcPath));
                WriteLog(file, pcPath, "DELETE FAILED", error);
            }
        }
    }

    /// <summary>
    /// The PC folder for a phone file: &lt;destination&gt;\&lt;phone folder name&gt;, for example
    /// "DCIM\202605__" → "&lt;destination&gt;\202605__". The DCIM level is not kept.
    /// </summary>
    internal static string TargetFolder(string destinationRoot, string phoneFolder)
    {
        string last = phoneFolder.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return last.Length == 0 ? destinationRoot : Path.Combine(destinationRoot, SafeName(last));
    }

    private (string PcPath, CheckResult Check, string Note) CopyAndCheck(DeviceFile file, CancellationToken ct)
    {
        if (file.Size < 0)
            throw new IOException("the phone did not report the file size, so the copy cannot be checked");

        string ext = Path.GetExtension(file.Name);
        string dir = TargetFolder(destinationRoot, file.Folder);
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, SafeName(file.Name));

        // Resume: the same file is already on the PC from an earlier run.
        if (File.Exists(dest))
        {
            if (new FileInfo(dest).Length == file.Size)
            {
                var existing = FormatValidator.Check(dest, ext);
                if (existing.Status != CheckStatus.Invalid)
                {
                    bytesDone += file.Size;
                    onBytes(bytesDone);
                    return (dest, existing, "already on PC; ");
                }
            }
            dest = UniqueName(dest);
        }

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dest))!);
        if (drive.AvailableFreeSpace < file.Size + 50L * 1024 * 1024)
            throw new IOException($"not enough free space on {drive.Name}");

        string part = dest + ".part";
        File.Delete(part);
        long written;
        long startBytes = bytesDone;
        try
        {
            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                written = device.Download(file.ObjectId, fs, n => onBytes(startBytes + n), ct);
                fs.Flush(flushToDisk: true);
            }

            if (written != file.Size)
                throw new IOException($"size mismatch: phone says {file.Size:N0} bytes, received {written:N0}." + KeepOriginalsHint);
            long onDisk = new FileInfo(part).Length;
            if (onDisk != file.Size)
                throw new IOException($"size mismatch: phone says {file.Size:N0} bytes, file on PC has {onDisk:N0}");

            var check = FormatValidator.Check(part, ext);
            if (check.Status == CheckStatus.Invalid)
                throw new IOException("format check failed: " + check.Detail);

            File.Move(part, dest);
            SetTimes(dest, file);
            bytesDone = startBytes + file.Size;
            onBytes(bytesDone);
            return (dest, check, "");
        }
        catch
        {
            TryDelete(part);
            bytesDone = startBytes;
            throw;
        }
    }

    /// <summary>Returns null on success, or an error text.</summary>
    private string? DeleteFromPhone(DeviceFile file)
    {
        if (!file.CanDelete)
            return "NOT deleted: the phone marks this file as read-only." + ReadOnlyHint;

        int hr = device.Delete(file.ObjectId);
        if (HResult.IsNotFound(hr))
            return null; // iOS already removed it together with another file of the same photo
        if (reconnectAllowed && HResult.IsConnectionLost(hr))
            throw new ConnectionLostException(hr);

        if (hr < 0 || hr == HResult.S_FALSE)
        {
            // Fallback: send the PTP DeleteObject operation directly to the phone.
            var ptp = device.PtpDelete(file.ObjectId);
            if (reconnectAllowed && HResult.IsConnectionLost(ptp.HResult))
                throw new ConnectionLostException(ptp.HResult);
            if (!ptp.Ok && ptp.ResponseCode != 0x2009 /* already gone */)
                return $"NOT deleted: WPD delete gave {WpdException.Describe(hr)}, PTP DeleteObject gave {ptp}." +
                       (hr == HResult.E_ACCESSDENIED ? ReadOnlyHint : "");
        }

        // Confirm with the phone itself. (The Windows driver can still show a deleted file from its cache.)
        if (device.ExistsOnPhone(file.ObjectId) == true)
            return "NOT deleted: the phone still has the file after the delete.";
        return null;
    }

    // ------------------------------------------------------------------ helpers

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 || s == ".." ? "_" : s;
    }

    internal static string UniqueName(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
                return candidate;
        }
    }

    private static void SetTimes(string path, DeviceFile file)
    {
        try
        {
            if (file.Created is DateTime c)
                File.SetCreationTime(path, c);
            if ((file.Modified ?? file.Created) is DateTime m)
                File.SetLastWriteTime(path, m);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>One log per PC folder: &lt;destination&gt;\202605__\iphone-mover-log.csv.</summary>
    private StreamWriter LogFor(DeviceFile file)
    {
        string dir = TargetFolder(destinationRoot, file.Folder);
        if (logs.TryGetValue(dir, out var w))
            return w;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, LogFileName);
        bool isNew = !File.Exists(path);
        w = new StreamWriter(path, append: true, new UTF8Encoding(true)) { AutoFlush = true };
        if (isNew)
            w.WriteLine("time,phone_path,pc_path,size,result,detail");
        logs[dir] = w;
        return w;
    }

    private void WriteLog(DeviceFile file, string pcPath, string result, string detail)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        try
        {
            LogFor(file).WriteLine(string.Join(",",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Q(file.DevicePath), Q(pcPath), file.Size.ToString(CultureInfo.InvariantCulture), Q(result), Q(detail)));
        }
        catch (IOException) { }               // a log problem must not stop the move
        catch (UnauthorizedAccessException) { }
    }
}
