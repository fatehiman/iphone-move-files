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

    /// <summary>True when the object still exists on the phone.</summary>
    public bool Exists(string objectId) => Worker.Invoke(() =>
    {
        int hr = properties!.GetValues(objectId, keys, out var values);
        if (values is not null)
            Marshal.ReleaseComObject(values);
        return hr >= 0;
    });

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
