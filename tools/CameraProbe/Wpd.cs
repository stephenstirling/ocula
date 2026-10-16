using System.Runtime.InteropServices;
using System.Text;

namespace CameraProbe;

/// <summary>
/// Minimal COM declarations for the Windows Portable Devices API, taken from the Windows SDK headers
/// (PortableDeviceApi.h, PortableDeviceTypes.h, PortableDevice.h). Only the first methods each interface
/// needs are declared; the vtable order matches the headers, so later slots are simply never called.
/// </summary>
internal static class Wpd
{
    public static readonly Guid ClsidPortableDeviceManager = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
    public static readonly Guid ClsidPortableDeviceFtm = new("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");   // free-threaded PortableDevice
    public static readonly Guid ClsidPortableDeviceValues = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    public static readonly Guid ClsidPortableDeviceKeyCollection = new("de2d022d-2480-43be-97f0-d1fa2cf98f4f");

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid Format;
        public uint Pid;
        public PropertyKey(string format, uint pid) { Format = new Guid(format); Pid = pid; }
    }

    // WPD_DEVICE_PROPERTIES_V1 {26D4979A-E643-4626-9E2B-736DC0C92FDC}
    private const string DeviceProps = "26D4979A-E643-4626-9E2B-736DC0C92FDC";
    public static readonly PropertyKey FirmwareVersion = new(DeviceProps, 3);
    public static readonly PropertyKey PowerLevel = new(DeviceProps, 4);
    public static readonly PropertyKey Protocol = new(DeviceProps, 6);
    public static readonly PropertyKey Manufacturer = new(DeviceProps, 7);
    public static readonly PropertyKey Model = new(DeviceProps, 8);
    public static readonly PropertyKey SerialNumber = new(DeviceProps, 9);
    public static readonly PropertyKey FriendlyName = new(DeviceProps, 12);
    public static readonly PropertyKey DeviceType = new(DeviceProps, 15);
    // WPD_DEVICE_TRANSPORT {463DD662-7FC4-4291-911C-7F4C9CCA9799}, 4
    public static readonly PropertyKey Transport = new("463DD662-7FC4-4291-911C-7F4C9CCA9799", 4);
    // WPD_CLIENT_INFORMATION_PROPERTIES_V1 {204D9F0C-2292-4080-9F42-40664E70F859}
    public static readonly PropertyKey ClientName = new("204D9F0C-2292-4080-9F42-40664E70F859", 2);
    public static readonly PropertyKey ClientMajor = new("204D9F0C-2292-4080-9F42-40664E70F859", 3);
    public static readonly PropertyKey ClientMinor = new("204D9F0C-2292-4080-9F42-40664E70F859", 4);
    public static readonly PropertyKey ClientRevision = new("204D9F0C-2292-4080-9F42-40664E70F859", 5);

    public const string DeviceObjectId = "DEVICE";   // WPD_DEVICE_OBJECT_ID

    public static string DeviceTypeName(uint type) => type switch
    {
        0 => "generic", 1 => "camera", 2 => "media player", 3 => "phone", 4 => "video", 5 => "personal information manager",
        6 => "audio recorder", _ => $"unknown ({type})"
    };

    public static string TransportName(uint transport) => transport switch
    {
        0 => "unspecified", 1 => "USB", 2 => "IP", 3 => "Bluetooth", _ => $"unknown ({transport})"
    };

    [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceManager
    {
        [PreserveSig] int GetDevices([In, Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? ids, ref uint count);
        [PreserveSig] int RefreshDeviceList();
        [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id, [In, Out] StringBuilder? name, ref uint chars);
        [PreserveSig] int GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string id, [In, Out] StringBuilder? description, ref uint chars);
        [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string id, [In, Out] StringBuilder? manufacturer, ref uint chars);
    }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDevice
    {
        [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceValues clientInfo);
        [PreserveSig] int SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
        [PreserveSig] int Content(out IPortableDeviceContent content);
        [PreserveSig] int Capabilities(out IntPtr capabilities);
        [PreserveSig] int Cancel();
        [PreserveSig] int Close();
    }

    [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceContent
    {
        [PreserveSig] int EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId, IPortableDeviceValues? filter, out IntPtr enumerator);
        [PreserveSig] int Properties(out IPortableDeviceProperties properties);
    }

    [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceProperties
    {
        [PreserveSig] int GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
        [PreserveSig] int GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
        [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues values);
    }

    [ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceKeyCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int Add(ref PropertyKey key);
    }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    }

    public static T Create<T>(Guid clsid) where T : class =>
        (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
}
