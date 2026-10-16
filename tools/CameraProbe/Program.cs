// CameraProbe: what does Windows tell an app about a connected camera or phone?
//   1. The Windows Portable Devices (WPD) COM API: manufacturer, model, firmware version, serial number.
//   2. WinRT device enumeration: what a WinRT app could see instead of COM.
//   3. USB drives: cameras that mount as a drive (mass storage) rather than a portable device.
// Read-only: devices are opened for reading and nothing is written to them.
//
//   CameraProbe [--log probe.txt]
//
// The serial number is printed on screen but is never written to the log (nor are device instance ids,
// which contain it): a log keeps only the USB vendor and product ids.
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CameraProbe;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;

var logPath = args.SkipWhile(a => a != "--log").Skip(1).FirstOrDefault();
var report = new Report(logPath);

report.Line($"CameraProbe {DateTime.Now:yyyy-MM-dd HH:mm:ss}, Windows {Environment.OSVersion.Version}");
report.Blank();
ProbeWpd(report);
report.Blank();
await ProbeWinRtAsync(report);
report.Blank();
ProbeUsbDrives(report);
report.Close();
return 0;

// ======================================================================================================

static void ProbeWpd(Report r)
{
    r.Line("== 1. Windows Portable Devices (COM: IPortableDeviceManager) ==");
    Wpd.IPortableDeviceManager manager;
    try { manager = Wpd.Create<Wpd.IPortableDeviceManager>(Wpd.ClsidPortableDeviceManager); }
    catch (Exception ex) { r.Line($"Could not create IPortableDeviceManager: {ex.Message}"); return; }

    manager.RefreshDeviceList();
    uint count = 0;
    manager.GetDevices(null, ref count);
    var ids = new string[count];
    if (count > 0) manager.GetDevices(ids, ref count);
    r.Line($"{count} portable device(s) found.");

    var index = 0;
    foreach (var id in ids.Take((int)count))
    {
        index++;
        r.Blank();
        r.Line($"-- Device {index}: {Util.Redact(id)}");
        r.Line($"   (as seen by the device manager) name: {ManagerString(manager.GetDeviceFriendlyName, id)}, " +
               $"description: {ManagerString(manager.GetDeviceDescription, id)}, manufacturer: {ManagerString(manager.GetDeviceManufacturer, id)}");
        OpenAndRead(r, id);
    }
    if (count == 0)
        r.Line("Nothing to read. Connect a camera in PTP/MTP mode (or a phone set to 'File transfer') and run again.");
}

static string ManagerString(ManagerStringCall call, string id)
{
    uint chars = 0;
    var hr = call(id, null, ref chars);              // first call: how long is it?
    if (hr < 0 || chars == 0) return $"(none, 0x{hr:X8})";
    var text = new StringBuilder((int)chars);
    hr = call(id, text, ref chars);
    return hr < 0 ? $"(error 0x{hr:X8})" : text.ToString().TrimEnd('\0');
}

static void OpenAndRead(Report r, string id)
{
    var device = Wpd.Create<Wpd.IPortableDevice>(Wpd.ClsidPortableDeviceFtm);
    var client = Wpd.Create<Wpd.IPortableDeviceValues>(Wpd.ClsidPortableDeviceValues);
    var key = Wpd.ClientName; client.SetStringValue(ref key, "CameraProbe");
    key = Wpd.ClientMajor; client.SetUnsignedIntegerValue(ref key, 1);
    key = Wpd.ClientMinor; client.SetUnsignedIntegerValue(ref key, 0);
    key = Wpd.ClientRevision; client.SetUnsignedIntegerValue(ref key, 0);
    key = new Wpd.PropertyKey("204D9F0C-2292-4080-9F42-40664E70F859", 9);   // WPD_CLIENT_DESIRED_ACCESS: read only
    client.SetUnsignedIntegerValue(ref key, 0x80000000);                    // GENERIC_READ

    var hr = device.Open(id, client);
    if (hr < 0) { r.Line($"   Open failed: 0x{hr:X8} ({Util.Describe(hr)})"); return; }
    try
    {
        device.Content(out var content);
        content.Properties(out var properties);

        // Which properties does the device object offer at all?
        if (properties.GetSupportedProperties(Wpd.DeviceObjectId, out var supported) >= 0 && supported.GetCount(out var n) >= 0)
        {
            var names = new List<string>();
            for (uint i = 0; i < n; i++)
                if (supported.GetAt(i, out var k) >= 0) names.Add(PropertyName(k));
            r.Line($"   properties offered by the device object ({n}): {string.Join(", ", names)}");
        }

        // The ones we want.
        var keys = Wpd.Create<Wpd.IPortableDeviceKeyCollection>(Wpd.ClsidPortableDeviceKeyCollection);
        foreach (var wanted in new[] { Wpd.FirmwareVersion, Wpd.Manufacturer, Wpd.Model, Wpd.SerialNumber, Wpd.FriendlyName,
                                        Wpd.DeviceType, Wpd.Protocol, Wpd.Transport, Wpd.PowerLevel })
        {
            var copy = wanted;
            keys.Add(ref copy);
        }
        hr = properties.GetValues(Wpd.DeviceObjectId, keys, out var values);
        if (hr < 0) { r.Line($"   GetValues failed: 0x{hr:X8} ({Util.Describe(hr)})"); return; }

        r.Line($"   manufacturer:      {Text(values, Wpd.Manufacturer)}");
        r.Line($"   model:             {Text(values, Wpd.Model)}");
        r.Line($"   firmware version:  {Text(values, Wpd.FirmwareVersion)}");
        r.Line($"   friendly name:     {Text(values, Wpd.FriendlyName)}");
        r.Line($"   protocol:          {Text(values, Wpd.Protocol)}");
        r.Line($"   device type:       {Number(values, Wpd.DeviceType, Wpd.DeviceTypeName)}");
        r.Line($"   transport:         {Number(values, Wpd.Transport, Wpd.TransportName)}");
        r.Line($"   power level:       {Number(values, Wpd.PowerLevel, n => $"{n}%")}");
        r.Line($"   serial number:     {Text(values, Wpd.SerialNumber)}", log: "   serial number:     (not logged)");
    }
    finally
    {
        device.Close();
    }
}

static string Text(Wpd.IPortableDeviceValues values, Wpd.PropertyKey key)
{
    var hr = values.GetStringValue(ref key, out var text);
    return hr >= 0 ? (text.Length == 0 ? "(empty)" : text) : $"(not reported, 0x{hr:X8})";
}

static string Number(Wpd.IPortableDeviceValues values, Wpd.PropertyKey key, Func<uint, string> name)
{
    var hr = values.GetUnsignedIntegerValue(ref key, out var number);
    return hr >= 0 ? name(number) : $"(not reported, 0x{hr:X8})";
}

static string PropertyName(Wpd.PropertyKey key)
{
    if (key.Format == new Guid("26D4979A-E643-4626-9E2B-736DC0C92FDC"))
        return key.Pid switch
        {
            2 => "sync partner", 3 => "FIRMWARE_VERSION", 4 => "POWER_LEVEL", 5 => "POWER_SOURCE", 6 => "PROTOCOL",
            7 => "MANUFACTURER", 8 => "MODEL", 9 => "SERIAL_NUMBER", 10 => "SUPPORTS_NON_CONSUMABLE", 11 => "DATETIME",
            12 => "FRIENDLY_NAME", 13 => "SUPPORTED_DRM_SCHEMES", 14 => "SUPPORTED_FORMATS_ARE_ORDERED", 15 => "TYPE",
            _ => $"device#{key.Pid}"
        };
    return $"{key.Format.ToString()[..8]}#{key.Pid}";
}

// ======================================================================================================

static async Task ProbeWinRtAsync(Report r)
{
    r.Line("== 2. WinRT (Windows.Devices.Enumeration, Windows.Devices.Portable) ==");
    var wanted = new[]
    {
        "System.ItemNameDisplay", "System.Devices.Manufacturer", "System.Devices.ModelName", "System.Devices.FriendlyName",
        "System.Devices.ContainerId", "System.Devices.Connected", "System.Devices.Aep.IsConnected"
    };
    foreach (var (label, selector) in new[]
    {
        ("StorageDevice.GetDeviceSelector()", StorageDevice.GetDeviceSelector()),
        ("DeviceClass.PortableStorageDevice", (string?)null),
    })
    {
        try
        {
            var found = selector != null
                ? await DeviceInformation.FindAllAsync(selector, wanted)
                : await DeviceInformation.FindAllAsync(DeviceClass.PortableStorageDevice);
            r.Line($"{label}: {found.Count} device(s)");
            foreach (var d in found)
            {
                r.Line($"   name: {d.Name}, id: {Util.Redact(d.Id)}, enabled: {d.IsEnabled}");
                foreach (var (k, v) in d.Properties)
                    r.Line(Util.ContainerOrInstance.Replace($"      {k} = {(v is Array a ? string.Join(",", a.Cast<object>()) : v)}", "[withheld]"));
                var firmware = d.Properties.Keys.Where(k => k.Contains("irmware", StringComparison.OrdinalIgnoreCase)).ToList();
                r.Line($"      firmware-related properties offered: {(firmware.Count == 0 ? "none" : string.Join(", ", firmware))}");
            }
        }
        catch (Exception ex)
        {
            r.Line($"{label}: failed ({ex.GetType().Name}: {ex.Message})");
        }
    }
    r.Line("WinRT note: its device properties are PnP/AQS properties (System.Devices.*); WPD's firmware version is read from the " +
           "device's own MTP/PTP data, which has no System.Devices.* equivalent. Use the COM API for it.");
}


// ======================================================================================================

static void ProbeUsbDrives(Report r)
{
    r.Line("== 3. USB drives (cameras in mass-storage mode, card readers) ==");
    try
    {
        using var disks = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive WHERE InterfaceType = 'USB'");
        var any = false;
        foreach (ManagementObject disk in disks.Get())
        {
            any = true;
            var letters = DriveLettersOf((string)disk["DeviceID"]);
            var dcim = letters.Where(l => Directory.Exists(l + @"\DCIM")).ToList();
            r.Line($"-- {disk["Model"]} | firmware revision (SCSI inquiry): {disk["FirmwareRevision"] ?? "(none)"} | {Util.Redact((string?)disk["PNPDeviceID"] ?? "")}");
            r.Line($"   drive letters: {(letters.Count == 0 ? "(none)" : string.Join(" ", letters))}; DCIM folder: {(dcim.Count > 0 ? "yes (" + string.Join(" ", dcim) + ")" : "no")}; " +
                   $"serial number: (not logged)", log: null);
            r.Line($"   serial number: {disk["SerialNumber"] ?? "(none)"}", log: "   serial number: (not logged)");
        }
        if (!any) r.Line("No USB drives found. (A camera in mass-storage mode would appear here, with the camera's USB product string as its model.)");
    }
    catch (Exception ex)
    {
        r.Line($"Could not query USB drives: {ex.Message}");
    }
}

static List<string> DriveLettersOf(string diskDeviceId)
{
    var letters = new List<string>();
    var escaped = diskDeviceId.Replace(@"\", @"\\");
    using var partitions = new ManagementObjectSearcher(
        $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escaped}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
    foreach (ManagementObject partition in partitions.Get())
    {
        using var logical = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
        letters.AddRange(logical.Get().Cast<ManagementObject>().Select(l => (string)l["DeviceID"]));
    }
    return letters;
}

// ======================================================================================================

namespace CameraProbe
{
    internal delegate int ManagerStringCall(string id, StringBuilder? text, ref uint chars);

    internal static class Util
    {
        public static readonly Regex ContainerOrInstance = new(@"\{[0-9a-fA-F-]{36}\}|\\\\\?\\[^\s,]*", RegexOptions.Compiled);

        /// <summary>Keeps the USB vendor and product ids and drops everything else (instance ids carry the serial number).</summary>
        public static string Redact(string id)
        {
            var m = Regex.Match(id, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
            return m.Success ? $"USB VID_{m.Groups[1].Value.ToUpperInvariant()} PID_{m.Groups[2].Value.ToUpperInvariant()} (instance id withheld)" : "(device id withheld)";
        }

        public static string Describe(int hr) => hr switch
        {
            unchecked((int)0x80070005) => "access denied: another app may have the camera open",
            unchecked((int)0x800700AA) => "device busy: another app (Photos, a camera utility) is using it",
            unchecked((int)0x80004005) => "unspecified failure; the device may be locked, asleep or in a mode WPD can't use",
            unchecked((int)0x8007001F) => "device not functioning: try another cable or mode",
            unchecked((int)0x80070490) => "element not found",
            _ => Marshal.GetExceptionForHR(hr)?.Message ?? "unknown"
        };
    }

    /// <summary>Writes to the screen and, if asked, to a log file. The log never gets a serial number.</summary>
    internal sealed class Report
    {
        private readonly StreamWriter? _log;

        public Report(string? logPath) => _log = logPath == null ? null : new StreamWriter(logPath, append: false, new UTF8Encoding(false));

        /// <param name="log">What goes in the log instead of <paramref name="text"/>; null means the same text.</param>
        public void Line(string text, string? log = null)
        {
            Console.WriteLine(text);
            _log?.WriteLine(log ?? text);
        }

        public void Blank() => Line(string.Empty);

        public void Close() => _log?.Dispose();
    }
}
