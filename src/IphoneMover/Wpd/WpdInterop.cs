// Minimal Windows Portable Devices (WPD) COM interop.
//
// Interface layouts follow PortableDeviceApi.idl from the Windows SDK. They were
// cross-checked against the MediaDevices project (MIT, Ralf Beckers,
// https://github.com/Bassman2/MediaDevices). Only the methods used by this app are
// called; the vtable order of every declared method must match the IDL exactly.

using System.Runtime.InteropServices;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace IphoneMover.Wpd;

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;

    public PropertyKey(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

/// <summary>PROPVARIANT, 64-bit layout (24 bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VT_EMPTY = 0;
    public const ushort VT_DATE = 7;
    public const ushort VT_UI4 = 19;
    public const ushort VT_LPWSTR = 31;

    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr ptr;
    [FieldOffset(8)] public double dateVal;
    [FieldOffset(8)] public long longVal;
}

internal static class NativeMethods
{
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pvar);
}

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_FILE_NOT_FOUND = unchecked((int)0x80070002);   // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
    public const int E_NOT_FOUND = unchecked((int)0x80070490);        // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_WPD_DEVICE_NOT_OPEN = unchecked((int)0x802A0001);

    public static bool IsNotFound(int hr) => hr == E_FILE_NOT_FOUND || hr == E_NOT_FOUND;

    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new WpdException(hr, what);
    }
}

internal sealed class WpdException(int hr, string what)
    : Exception($"{what} failed: {Describe(hr)}")
{
    public int HResult32 { get; } = hr;

    public static string Describe(int hr)
    {
        string? msg = Marshal.GetExceptionForHR(hr)?.Message;
        return $"0x{hr:X8}" + (string.IsNullOrWhiteSpace(msg) ? "" : $" ({msg.Trim()})");
    }
}

internal static class WpdKeys
{
    private static readonly Guid ObjectProps = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
    private static readonly Guid ClientInfo = new("204D9F0C-2292-4080-9F42-40664E70F859");

    public static readonly PropertyKey ObjectParentId = new(ObjectProps, 3);
    public static readonly PropertyKey ObjectName = new(ObjectProps, 4);
    public static readonly PropertyKey ObjectPersistentUniqueId = new(ObjectProps, 5);
    public static readonly PropertyKey ObjectContentType = new(ObjectProps, 7);
    public static readonly PropertyKey ObjectSize = new(ObjectProps, 11);
    public static readonly PropertyKey ObjectOriginalFileName = new(ObjectProps, 12);
    public static readonly PropertyKey ObjectDateCreated = new(ObjectProps, 18);
    public static readonly PropertyKey ObjectDateModified = new(ObjectProps, 19);
    public static readonly PropertyKey ObjectCanDelete = new(ObjectProps, 26);

    public static readonly PropertyKey StorageAccessCapability = new(new Guid("01A3057A-74D6-4E80-BEA7-DC4C212CE50A"), 11);
    public static readonly PropertyKey DeviceProtocol = new(new Guid("26D4979A-E643-4626-9E2B-736DC0C92FDC"), 6);

    // WPD_COMMON command parameters
    private static readonly Guid CategoryCommon = new("F0422A9C-5DC8-4440-B5BD-5DF28835658A");
    public static readonly PropertyKey CommonCommandCategory = new(CategoryCommon, 1001);
    public static readonly PropertyKey CommonCommandId = new(CategoryCommon, 1002);
    public static readonly PropertyKey CommonHResult = new(CategoryCommon, 1003);

    // MTP extension commands: send raw MTP/PTP operations to the device.
    private static readonly Guid CategoryMtpExt = new("4D545058-1A2E-4106-A357-771E0819FC56");
    public static readonly PropertyKey MtpExtExecuteWithoutData = new(CategoryMtpExt, 12);
    public static readonly PropertyKey MtpExtExecuteWithDataToRead = new(CategoryMtpExt, 13);
    public static readonly PropertyKey MtpExtReadData = new(CategoryMtpExt, 15);
    public static readonly PropertyKey MtpExtEndDataTransfer = new(CategoryMtpExt, 17);
    public static readonly PropertyKey MtpExtOperationCode = new(CategoryMtpExt, 1001);
    public static readonly PropertyKey MtpExtOperationParams = new(CategoryMtpExt, 1002);
    public static readonly PropertyKey MtpExtResponseCode = new(CategoryMtpExt, 1003);
    public static readonly PropertyKey MtpExtResponseParams = new(CategoryMtpExt, 1004);
    public static readonly PropertyKey MtpExtTransferContext = new(CategoryMtpExt, 1006);
    public static readonly PropertyKey MtpExtTransferTotalDataSize = new(CategoryMtpExt, 1007);
    public static readonly PropertyKey MtpExtTransferNumBytesToRead = new(CategoryMtpExt, 1008);
    public static readonly PropertyKey MtpExtTransferNumBytesRead = new(CategoryMtpExt, 1009);
    public static readonly PropertyKey MtpExtTransferData = new(CategoryMtpExt, 1012);

    public static readonly PropertyKey ResourceDefault = new(new Guid("E81E79BE-34F0-41BF-B53F-F1A06AE87842"), 0);

    public static readonly PropertyKey ClientName = new(ClientInfo, 2);
    public static readonly PropertyKey ClientMajorVersion = new(ClientInfo, 3);
    public static readonly PropertyKey ClientMinorVersion = new(ClientInfo, 4);
    public static readonly PropertyKey ClientRevision = new(ClientInfo, 5);
    public static readonly PropertyKey ClientSecurityQualityOfService = new(ClientInfo, 8);

    public static readonly Guid ContentTypeFolder = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    public static readonly Guid ContentTypeFunctionalObject = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");

    public const string DeviceObjectId = "DEVICE";
    public const uint SecurityImpersonation = 0x00020000;
    public const uint StgmRead = 0;
    public const uint DeleteNoRecursion = 0;
}

internal static class WpdClsid
{
    public static readonly Guid PortableDeviceManager = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");
    // Free-threaded variant of PortableDevice, safe to use from our MTA worker thread.
    public static readonly Guid PortableDeviceFTM = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    public static readonly Guid PortableDeviceValues = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    public static readonly Guid PortableDeviceKeyCollection = new("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");
    public static readonly Guid PortableDevicePropVariantCollection = new("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9");

    public static T Create<T>(Guid clsid) where T : class
    {
        Type type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        return (T)Activator.CreateInstance(type)!;
    }
}

[ComImport, Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    [PreserveSig] int GetDevices([In, Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[]? pPnPDeviceIDs, ref uint pcPnPDeviceIDs);
    [PreserveSig] int RefreshDeviceList();
    [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IntPtr pDeviceFriendlyName, ref uint pcchDeviceFriendlyName);
    [PreserveSig] int GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IntPtr pDeviceDescription, ref uint pcchDeviceDescription);
    [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IntPtr pDeviceManufacturer, ref uint pcchDeviceManufacturer);
}

[ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IPortableDeviceValues pClientInfo);
    [PreserveSig] int SendCommand(uint dwFlags, IPortableDeviceValues pParameters, out IPortableDeviceValues ppResults);
    [PreserveSig] int Content(out IPortableDeviceContent ppContent);
    [PreserveSig] int Capabilities(out IntPtr ppCapabilities);
    [PreserveSig] int Cancel();
    [PreserveSig] int Close();
}

[ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    [PreserveSig] int EnumObjects(uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string pszParentObjectID, IPortableDeviceValues? pFilter, out IEnumPortableDeviceObjectIDs ppEnum);
    [PreserveSig] int Properties(out IPortableDeviceProperties ppProperties);
    [PreserveSig] int Transfer(out IPortableDeviceResources ppResources);
    [PreserveSig] int CreateObjectWithPropertiesOnly(IPortableDeviceValues pValues, IntPtr ppszObjectID);
    [PreserveSig] int CreateObjectWithPropertiesAndData(IPortableDeviceValues pValues, IntPtr ppData, IntPtr pdwOptimalWriteBufferSize, IntPtr ppszCookie);
    // ppResults is an optional [in, out, unique] pointer: we pass IntPtr.Zero (NULL).
    [PreserveSig] int Delete(uint dwOptions, IPortableDevicePropVariantCollection pObjectIDs, IntPtr ppResults);
}

[ComImport, Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPortableDeviceObjectIDs
{
    [PreserveSig] int Next(uint cObjects, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pObjIDs, ref uint pcFetched);
    [PreserveSig] int Skip(uint cObjects);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumPortableDeviceObjectIDs ppEnum);
    [PreserveSig] int Cancel();
}

[ComImport, Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    [PreserveSig] int GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, out IPortableDeviceKeyCollection ppKeys);
    [PreserveSig] int GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, ref PropertyKey key, out IPortableDeviceValues ppAttributes);
    [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, IPortableDeviceKeyCollection? pKeys, out IPortableDeviceValues ppValues);
}

[ComImport, Guid("FD8878AC-D841-4D17-891C-E6829CDB6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceResources
{
    [PreserveSig] int GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, out IPortableDeviceKeyCollection ppKeys);
    [PreserveSig] int GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, ref PropertyKey key, out IPortableDeviceValues ppResourceAttributes);
    [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, ref PropertyKey key, uint dwMode, ref uint pdwOptimalBufferSize, out IStream ppStream);
}

[ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    [PreserveSig] int GetCount(ref uint pcElems);
    [PreserveSig] int GetAt(uint dwIndex, ref PropertyKey pKey);
    [PreserveSig] int Add(ref PropertyKey key);
    [PreserveSig] int Clear();
    [PreserveSig] int RemoveAt(uint dwIndex);
}

[ComImport, Guid("89B2E422-4F1B-4316-BCEF-A44AFEA83EB3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    [PreserveSig] int GetCount(ref uint pcElems);
    [PreserveSig] int GetAt(uint dwIndex, ref PropVariant pValue);
    [PreserveSig] int Add(ref PropVariant pValue);
}

[ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    [PreserveSig] int GetCount(ref uint pcelt);
    [PreserveSig] int GetAt(uint index, ref PropertyKey pKey, ref PropVariant pValue);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pValue);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pValue);
    [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    [PreserveSig] int SetSignedIntegerValue(ref PropertyKey key, int value);
    [PreserveSig] int GetSignedIntegerValue(ref PropertyKey key, out int value);
    [PreserveSig] int SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
    [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
    [PreserveSig] int SetSignedLargeIntegerValue(ref PropertyKey key, long value);
    [PreserveSig] int GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
    [PreserveSig] int SetFloatValue(ref PropertyKey key, float value);
    [PreserveSig] int GetFloatValue(ref PropertyKey key, out float value);
    [PreserveSig] int SetErrorValue(ref PropertyKey key, int value);
    [PreserveSig] int GetErrorValue(ref PropertyKey key, out int value);
    [PreserveSig] int SetKeyValue(ref PropertyKey key, ref PropertyKey value);
    [PreserveSig] int GetKeyValue(ref PropertyKey key, out PropertyKey value);
    [PreserveSig] int SetBoolValue(ref PropertyKey key, int value);
    [PreserveSig] int GetBoolValue(ref PropertyKey key, out int value);
    [PreserveSig] int SetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int GetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int SetGuidValue(ref PropertyKey key, ref Guid value);
    [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
    [PreserveSig] int SetBufferValue(ref PropertyKey key, IntPtr pValue, uint cbValue);
    [PreserveSig] int GetBufferValue(ref PropertyKey key, out IntPtr ppValue, out uint pcbValue);
    [PreserveSig] int SetIPortableDeviceValuesValue(ref PropertyKey key, IPortableDeviceValues value);
    [PreserveSig] int GetIPortableDeviceValuesValue(ref PropertyKey key, out IPortableDeviceValues value);
    [PreserveSig] int SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IPortableDevicePropVariantCollection value);
    [PreserveSig] int GetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, out IPortableDevicePropVariantCollection value);
}
