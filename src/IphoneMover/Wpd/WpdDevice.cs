using System.Runtime.InteropServices;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace IphoneMover.Wpd;

internal sealed record DeviceInfo(string Id, string FriendlyName, string Manufacturer, string Description)
{
    public bool LooksLikeApple =>
        $"{FriendlyName} {Manufacturer} {Description}".Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
        $"{FriendlyName} {Description}".Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
        $"{FriendlyName} {Description}".Contains("iPad", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        string.IsNullOrEmpty(Manufacturer) ? FriendlyName : $"{FriendlyName} ({Manufacturer})";
}

/// <summary>A file on the phone.</summary>
internal sealed record DeviceFile(
    string ObjectId,
    string Name,
    string Folder,          // relative folder on the phone, e.g. "DCIM\100APPLE"
    long Size,              // -1 when the phone does not report it
    DateTime? Created,
    DateTime? Modified,
    bool CanDelete)
{
    public string DevicePath => Folder.Length == 0 ? Name : Folder + "\\" + Name;
}

/// <summary>An open connection to one WPD device (the iPhone).</summary>
internal sealed class WpdDevice : IDisposable
{
    private static readonly WpdWorker Worker = new();

    private IPortableDevice? device;
    private IPortableDeviceContent? content;
    private IPortableDeviceProperties? properties;
    private IPortableDeviceKeyCollection? keys;

    public DeviceInfo Info { get; }

    private WpdDevice(DeviceInfo info) => Info = info;

    // ---------------------------------------------------------------- devices

    public static List<DeviceInfo> ListDevices() => Worker.Invoke(() =>
    {
        var manager = WpdClsid.Create<IPortableDeviceManager>(WpdClsid.PortableDeviceManager);
        manager.RefreshDeviceList();

        uint count = 0;
        HResult.Check(manager.GetDevices(null, ref count), "GetDevices");
        var result = new List<DeviceInfo>();
        if (count == 0)
            return result;

        var ids = new IntPtr[count];
        HResult.Check(manager.GetDevices(ids, ref count), "GetDevices");
        for (int i = 0; i < count; i++)
        {
            string id = TakeString(ids[i]);
            result.Add(new DeviceInfo(
                id,
                ReadManagerString(manager.GetDeviceFriendlyName, id),
                ReadManagerString(manager.GetDeviceManufacturer, id),
                ReadManagerString(manager.GetDeviceDescription, id)));
        }
        return result;
    });

    private delegate int ManagerStringGetter(string id, IntPtr buffer, ref uint chars);

    private static string ReadManagerString(ManagerStringGetter getter, string id)
    {
        uint chars = 0;
        if (getter(id, IntPtr.Zero, ref chars) < 0 || chars == 0)
            return "";
        IntPtr buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            return getter(id, buffer, ref chars) < 0 ? "" : Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads a CoTaskMem string written by WPD and frees it.</summary>
    private static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero)
            return "";
        string s = Marshal.PtrToStringUni(p) ?? "";
        Marshal.FreeCoTaskMem(p);
        return s;
    }

    public static WpdDevice Open(DeviceInfo info) => Worker.Invoke(() =>
    {
        var d = new WpdDevice(info);
        var clientInfo = WpdClsid.Create<IPortableDeviceValues>(WpdClsid.PortableDeviceValues);
        var k = WpdKeys.ClientName; clientInfo.SetStringValue(ref k, "iPhone Mover");
        k = WpdKeys.ClientMajorVersion; clientInfo.SetUnsignedIntegerValue(ref k, 1);
        k = WpdKeys.ClientMinorVersion; clientInfo.SetUnsignedIntegerValue(ref k, 0);
        k = WpdKeys.ClientRevision; clientInfo.SetUnsignedIntegerValue(ref k, 0);
        k = WpdKeys.ClientSecurityQualityOfService; clientInfo.SetUnsignedIntegerValue(ref k, WpdKeys.SecurityImpersonation);

        d.device = WpdClsid.Create<IPortableDevice>(WpdClsid.PortableDeviceFTM);
        HResult.Check(d.device.Open(info.Id, clientInfo), "Open device");
        HResult.Check(d.device.Content(out var c), "IPortableDevice.Content");
        d.content = c;
        HResult.Check(c.Properties(out var p), "IPortableDeviceContent.Properties");
        d.properties = p;

        d.keys = WpdClsid.Create<IPortableDeviceKeyCollection>(WpdClsid.PortableDeviceKeyCollection);
        foreach (var key in new[]
        {
            WpdKeys.ObjectContentType, WpdKeys.ObjectName, WpdKeys.ObjectOriginalFileName, WpdKeys.ObjectSize,
            WpdKeys.ObjectDateCreated, WpdKeys.ObjectDateModified, WpdKeys.ObjectCanDelete,
        })
        {
            var kk = key;
            d.keys.Add(ref kk);
        }
        return d;
    });

    // ---------------------------------------------------------------- listing

    /// <summary>Lists all files on the device (recursive). Reports the current folder as progress.</summary>
    public List<DeviceFile> ListFiles(IProgress<string>? progress, CancellationToken ct) => Worker.Invoke(() =>
    {
        var files = new List<DeviceFile>();
        // Top level objects are storages ("Internal Storage"). Their name is not part of the folder path.
        foreach (string storageId in EnumChildren(WpdKeys.DeviceObjectId))
            Walk(storageId, "", files, progress, ct, depth: 0);
        return files;
    });

    private void Walk(string parentId, string folder, List<DeviceFile> files, IProgress<string>? progress, CancellationToken ct, int depth)
    {
        if (depth > 32)
            return;
        progress?.Report(folder.Length == 0 ? "\\" : folder);

        foreach (string id in EnumChildren(parentId))
        {
            ct.ThrowIfCancellationRequested();
            var obj = ReadObject(id);
            if (obj is null)
                continue;

            if (obj.Value.IsContainer)
            {
                string sub = folder.Length == 0 ? obj.Value.Name : folder + "\\" + obj.Value.Name;
                Walk(id, sub, files, progress, ct, depth + 1);
            }
            else
            {
                files.Add(new DeviceFile(id, obj.Value.Name, folder, obj.Value.Size,
                    obj.Value.Created, obj.Value.Modified, obj.Value.CanDelete));
            }
        }
    }

    private List<string> EnumChildren(string parentId)
    {
        var ids = new List<string>();
        int hr = content!.EnumObjects(0, parentId, null, out var en);
        if (hr < 0 || en is null)
            return ids;
        try
        {
            var batch = new IntPtr[100];
            while (true)
            {
                uint fetched = 0;
                hr = en.Next((uint)batch.Length, batch, ref fetched);
                for (int i = 0; i < fetched; i++)
                    ids.Add(TakeString(batch[i]));
                if (hr != HResult.S_OK || fetched == 0)
                    break;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(en);
        }
        return ids;
    }

    private readonly record struct ObjectProps(string Name, bool IsContainer, long Size, DateTime? Created, DateTime? Modified, bool CanDelete);

    private ObjectProps? ReadObject(string id)
    {
        if (properties!.GetValues(id, keys, out var values) < 0 || values is null)
            return null;
        try
        {
            var k = WpdKeys.ObjectContentType;
            bool isContainer = values.GetGuidValue(ref k, out Guid type) >= 0 &&
                               (type == WpdKeys.ContentTypeFolder || type == WpdKeys.ContentTypeFunctionalObject);

            // OBJECT_NAME on iPhone has no extension; ORIGINAL_FILE_NAME has the real name.
            k = WpdKeys.ObjectOriginalFileName;
            if (values.GetStringValue(ref k, out string name) < 0 || string.IsNullOrEmpty(name))
            {
                k = WpdKeys.ObjectName;
                if (values.GetStringValue(ref k, out name) < 0 || string.IsNullOrEmpty(name))
                    name = id;
            }

            long size = -1;
            k = WpdKeys.ObjectSize;
            if (values.GetUnsignedLargeIntegerValue(ref k, out ulong s) >= 0)
                size = (long)s;

            k = WpdKeys.ObjectCanDelete;
            // When the phone does not say, assume deletable and let the delete call decide.
            bool canDelete = values.GetBoolValue(ref k, out int cd) < 0 || cd != 0;

            return new ObjectProps(name, isContainer, size,
                ReadDate(values, WpdKeys.ObjectDateCreated), ReadDate(values, WpdKeys.ObjectDateModified), canDelete);
        }
        finally
        {
            Marshal.ReleaseComObject(values);
        }
    }

    private static DateTime? ReadDate(IPortableDeviceValues values, PropertyKey key)
    {
        if (values.GetValue(ref key, out PropVariant pv) < 0)
            return null;
        try
        {
            if (pv.vt != PropVariant.VT_DATE)
                return null;
            DateTime dt = DateTime.FromOADate(pv.dateVal);
            return dt.Year < 1980 ? null : DateTime.SpecifyKind(dt, DateTimeKind.Local);
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref pv);
        }
    }

    /// <summary>
    /// True when the object is still known to the driver. Note: after a delete the driver can keep
    /// the old properties in its cache, so use <see cref="ExistsOnPhone"/> to confirm a delete.
    /// </summary>
    public bool Exists(string objectId) => Worker.Invoke(() =>
    {
        int hr = properties!.GetValues(objectId, keys, out var values);
        if (values is not null)
            Marshal.ReleaseComObject(values);
        return hr >= 0;
    });

    private const ushort PtpGetObjectInfo = 0x1008;
    private const ushort PtpDeleteObject = 0x100B;
    private const uint PtpResponseOk = 0x2001;
    private const uint PtpResponseInvalidObjectHandle = 0x2009;

    /// <summary>The MTP/PTP object handle. The Windows MTP driver uses object IDs like "oCA46" (handle 0xCA46).</summary>
    internal static bool TryGetHandle(string objectId, out uint handle)
    {
        handle = 0;
        return objectId.Length > 1 && objectId[0] == 'o' &&
               uint.TryParse(objectId.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out handle);
    }

    /// <summary>
    /// Asks the phone directly (PTP GetObjectInfo), not the driver cache.
    /// Returns null when the answer is not clear.
    /// </summary>
    public bool? ExistsOnPhone(string objectId)
    {
        if (!TryGetHandle(objectId, out uint handle))
            return null;
        var r = MtpReadCommand(PtpGetObjectInfo, handle);
        if (r.ResponseCode == PtpResponseOk)
            return true;
        if (r.ResponseCode == PtpResponseInvalidObjectHandle)
            return false;
        return null;
    }

    /// <summary>Deletes with the raw PTP DeleteObject operation (fallback when the WPD delete fails).</summary>
    public MtpResult PtpDelete(string objectId)
    {
        if (!TryGetHandle(objectId, out uint handle))
            return new MtpResult(HResult.E_INVALIDARG, 0, [], []);
        return MtpCommand(PtpDeleteObject, handle, 0);
    }

    // ---------------------------------------------------------------- transfer

    /// <summary>Copies the file content into <paramref name="target"/>. Returns the number of bytes written.</summary>
    public long Download(string objectId, Stream target, Action<long>? onBytes, CancellationToken ct) => Worker.Invoke(() =>
    {
        HResult.Check(content!.Transfer(out var resources), "IPortableDeviceContent.Transfer");
        IStream? stream = null;
        IntPtr pRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var key = WpdKeys.ResourceDefault;
            uint optimal = 0;
            HResult.Check(resources.GetStream(objectId, ref key, WpdKeys.StgmRead, ref optimal, out stream), "Open file stream on phone");

            int bufSize = (int)Math.Clamp(optimal, 64 * 1024u, 4 * 1024 * 1024u);
            var buffer = new byte[bufSize];
            long total = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Marshal.WriteInt32(pRead, 0);
                stream.Read(buffer, buffer.Length, pRead);
                int read = Marshal.ReadInt32(pRead);
                if (read <= 0)
                    break;
                target.Write(buffer, 0, read);
                total += read;
                onBytes?.Invoke(total);
            }
            return total;
        }
        finally
        {
            Marshal.FreeHGlobal(pRead);
            // Release now: an open stream can block the delete that follows.
            if (stream is not null)
                Marshal.ReleaseComObject(stream);
            Marshal.ReleaseComObject(resources);
        }
    });

    /// <summary>Deletes one object. Returns the HRESULT (S_OK = deleted, S_FALSE = refused by the phone).</summary>
    public int Delete(string objectId) => Worker.Invoke(() =>
    {
        var ids = WpdClsid.Create<IPortableDevicePropVariantCollection>(WpdClsid.PortableDevicePropVariantCollection);
        var pv = new PropVariant { vt = PropVariant.VT_LPWSTR, ptr = Marshal.StringToCoTaskMemUni(objectId) };
        try
        {
            int hr = ids.Add(ref pv);  // Add makes its own copy
            if (hr < 0)
                return hr;
            return content!.Delete(WpdKeys.DeleteNoRecursion, ids, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.PropVariantClear(ref pv);
            Marshal.ReleaseComObject(ids);
        }
    });

    // ---------------------------------------------------------------- raw MTP / PTP commands

    internal sealed record MtpResult(int HResult, uint ResponseCode, uint[] Params, byte[] Data)
    {
        public bool Ok => HResult >= 0 && ResponseCode == 0x2001; // PTP "OK"
        public override string ToString() =>
            $"hr=0x{HResult:X8} response=0x{ResponseCode:X4} params=[{string.Join(",", Params.Select(p => $"0x{p:X}"))}] data={Data.Length}B";
    }

    /// <summary>Sends an MTP/PTP operation that has no data phase (for example DeleteObject 0x100B).</summary>
    public MtpResult MtpCommand(ushort opcode, params uint[] args) => Worker.Invoke(() =>
    {
        var p = NewCommand(WpdKeys.MtpExtExecuteWithoutData);
        var k = WpdKeys.MtpExtOperationCode; p.SetUnsignedIntegerValue(ref k, opcode);
        k = WpdKeys.MtpExtOperationParams; p.SetIPortableDevicePropVariantCollectionValue(ref k, UIntCollection(args));
        int hr = device!.SendCommand(0, p, out var res);
        if (hr < 0)
            return new MtpResult(hr, 0, [], []);
        return ReadResponse(res, []);
    });

    /// <summary>Sends an MTP/PTP operation that returns data (for example GetDeviceInfo 0x1001).</summary>
    public MtpResult MtpReadCommand(ushort opcode, params uint[] args) => Worker.Invoke(() =>
    {
        var p = NewCommand(WpdKeys.MtpExtExecuteWithDataToRead);
        var k = WpdKeys.MtpExtOperationCode; p.SetUnsignedIntegerValue(ref k, opcode);
        k = WpdKeys.MtpExtOperationParams; p.SetIPortableDevicePropVariantCollectionValue(ref k, UIntCollection(args));
        int hr = device!.SendCommand(0, p, out var res);
        if (hr < 0)
            return new MtpResult(hr, 0, [], []);
        hr = CommandHResult(res);
        if (hr < 0)
            return new MtpResult(hr, 0, [], []);

        k = WpdKeys.MtpExtTransferContext; res.GetStringValue(ref k, out string context);
        k = WpdKeys.MtpExtTransferTotalDataSize; res.GetUnsignedLargeIntegerValue(ref k, out ulong total);

        var data = new MemoryStream();
        while ((ulong)data.Length < total)
        {
            uint chunk = (uint)Math.Min(total - (ulong)data.Length, 256 * 1024);
            var rp = NewCommand(WpdKeys.MtpExtReadData);
            k = WpdKeys.MtpExtTransferContext; rp.SetStringValue(ref k, context);
            k = WpdKeys.MtpExtTransferNumBytesToRead; rp.SetUnsignedIntegerValue(ref k, chunk);
            IntPtr buf = Marshal.AllocCoTaskMem((int)chunk);
            try
            {
                k = WpdKeys.MtpExtTransferData; rp.SetBufferValue(ref k, buf, chunk);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buf);
            }
            hr = device.SendCommand(0, rp, out var rr);
            if (hr >= 0) hr = CommandHResult(rr);
            if (hr < 0)
                break;
            k = WpdKeys.MtpExtTransferNumBytesRead; rr.GetUnsignedIntegerValue(ref k, out uint read);
            k = WpdKeys.MtpExtTransferData;
            if (read == 0 || rr.GetBufferValue(ref k, out IntPtr pData, out uint cb) < 0)
                break;
            var bytes = new byte[Math.Min(read, cb)];
            Marshal.Copy(pData, bytes, 0, bytes.Length);
            Marshal.FreeCoTaskMem(pData);
            data.Write(bytes);
        }

        var ep = NewCommand(WpdKeys.MtpExtEndDataTransfer);
        k = WpdKeys.MtpExtTransferContext; ep.SetStringValue(ref k, context);
        int ehr = device.SendCommand(0, ep, out var er);
        if (ehr < 0)
            return new MtpResult(ehr, 0, [], data.ToArray());
        return ReadResponse(er, data.ToArray());
    });

    private static IPortableDeviceValues NewCommand(PropertyKey command)
    {
        var p = WpdClsid.Create<IPortableDeviceValues>(WpdClsid.PortableDeviceValues);
        var k = WpdKeys.CommonCommandCategory; var cat = command.fmtid; p.SetGuidValue(ref k, ref cat);
        k = WpdKeys.CommonCommandId; p.SetUnsignedIntegerValue(ref k, command.pid);
        return p;
    }

    private static int CommandHResult(IPortableDeviceValues res)
    {
        var k = WpdKeys.CommonHResult;
        return res.GetErrorValue(ref k, out int hr) >= 0 ? hr : 0;
    }

    private static MtpResult ReadResponse(IPortableDeviceValues res, byte[] data)
    {
        int hr = CommandHResult(res);
        var k = WpdKeys.MtpExtResponseCode;
        res.GetUnsignedIntegerValue(ref k, out uint code);
        var prms = new List<uint>();
        k = WpdKeys.MtpExtResponseParams;
        if (res.GetIPortableDevicePropVariantCollectionValue(ref k, out var col) >= 0 && col is not null)
        {
            uint n = 0;
            col.GetCount(ref n);
            for (uint i = 0; i < n; i++)
            {
                var pv = new PropVariant();
                if (col.GetAt(i, ref pv) >= 0)
                    prms.Add((uint)pv.longVal);
                NativeMethods.PropVariantClear(ref pv);
            }
        }
        return new MtpResult(hr, code, [.. prms], data);
    }

    private static IPortableDevicePropVariantCollection UIntCollection(uint[] values)
    {
        var col = WpdClsid.Create<IPortableDevicePropVariantCollection>(WpdClsid.PortableDevicePropVariantCollection);
        foreach (uint v in values)
        {
            var pv = new PropVariant { vt = PropVariant.VT_UI4, longVal = v };
            col.Add(ref pv);
        }
        return col;
    }

    /// <summary>Diagnostic data: storages with their WPD access capability, and a few raw object properties.</summary>
    internal List<string> Describe(IEnumerable<string> objectIds) => Worker.Invoke(() =>
    {
        var lines = new List<string>();
        foreach (string storage in EnumChildren(WpdKeys.DeviceObjectId))
        {
            properties!.GetValues(storage, null, out var v);
            var k = WpdKeys.StorageAccessCapability;
            string cap = v.GetUnsignedIntegerValue(ref k, out uint c) >= 0 ? c.ToString() : "n/a";
            k = WpdKeys.ObjectName; v.GetStringValue(ref k, out string name);
            lines.Add($"storage id={storage} name={name} accessCapability={cap} (0=read/write, 1=read-only, 2=read-only but delete allowed)");
        }
        foreach (string id in objectIds)
        {
            properties!.GetValues(id, null, out var v);
            uint n = 0;
            v.GetCount(ref n);
            var parts = new List<string>();
            for (uint i = 0; i < n; i++)
            {
                var key = new PropertyKey();
                var pv = new PropVariant();
                if (v.GetAt(i, ref key, ref pv) < 0) continue;
                string val = pv.vt switch
                {
                    PropVariant.VT_LPWSTR => Marshal.PtrToStringUni(pv.ptr) ?? "",
                    11 => (pv.longVal & 0xFFFF) != 0 ? "true" : "false",   // VT_BOOL
                    19 or 21 or 3 => (pv.longVal & (pv.vt == 21 ? -1L : 0xFFFFFFFF)).ToString(),
                    _ => $"vt{pv.vt}",
                };
                NativeMethods.PropVariantClear(ref pv);
                parts.Add($"{{{key.fmtid}}}/{key.pid}={val}");
            }
            lines.Add($"object {id}:\n   " + string.Join("\n   ", parts));
        }
        return lines;
    });

    public void Dispose()
    {
        Worker.Invoke(() =>
        {
            if (device is null)
                return;
            device.Close();
            foreach (object? o in new object?[] { keys, properties, content, device })
                if (o is not null)
                    Marshal.ReleaseComObject(o);
            keys = null; properties = null; content = null; device = null;
        });
    }
}
