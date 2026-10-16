# Camera firmware: research notes

Status: **research only, no UI.** The probe (`tools/CameraProbe`) is written and runs, but this PC had no camera,
phone or USB drive connected, so the part that reads a device's firmware version has **never run against real
hardware**. Everything marked "untested" below needs a real device. See "Results from real devices" at the end;
it is empty until someone plugs something in.

Scope (from BUILDING.md): show a USB-connected camera's make, model and current firmware; offer a button that
opens the maker's official firmware page in the browser. Ocula never downloads, installs or flashes firmware,
never contacts a maker's site by itself, and does not claim whether a newer version exists. Opt-in, and no data
leaves the PC.

## What was checked

| Question | Answer | Basis |
|---|---|---|
| Can .NET list portable devices and read manufacturer / model / firmware? | Yes in principle, through the Windows Portable Devices (WPD) COM API: `IPortableDeviceManager` to list, `IPortableDevice.Open` plus `IPortableDeviceProperties.GetValues` on object `DEVICE` for `WPD_DEVICE_MANUFACTURER`, `WPD_DEVICE_MODEL`, `WPD_DEVICE_FIRMWARE_VERSION`. | The COM interop in `tools/CameraProbe/Wpd.cs` compiles and enumerates (0 devices on this PC, no errors). Reading properties: **untested**. |
| Does `WPD_DEVICE_FIRMWARE_VERSION` equal the camera's PTP `DeviceVersion`? | **Unconfirmed.** PTP's DeviceInfo has Manufacturer, Model, DeviceVersion and SerialNumber, and the WPD functional-device spec lists a firmware version property. No Microsoft source found says the MTP/PTP driver maps one to the other. | Needs a real camera. |
| Can WinRT replace the COM code? | **Probably not.** `Windows.Devices.Portable.StorageDevice` and `DeviceInformation.FindAllAsync` return PnP-style properties (name, id, container); no firmware property was found in them. The probe prints what WinRT returns so this can be confirmed. | Documentation and the probe's WinRT section; untested with a device. |
| What about cameras in mass-storage mode? | They show up as a USB disk, not as a portable device, so WPD will not list them. The disk's `FirmwareRevision` (WMI `Win32_DiskDrive`) is the SCSI inquiry string of the card reader inside the camera, **not** the camera firmware. | Reasoning from how Windows exposes them; the probe's third section prints these so it can be confirmed. |
| Fallback for mass-storage cameras | Idea, not built: photos often record the firmware in EXIF / maker notes (EXIF `Software` tag, or a maker-specific field). That reports the firmware at the time the photo was taken. | Untested. |
| Is the serial number needed? | Read, because the probe wants to see whether it is exposed, but **never written to the saved log**. | By design. |

## Things that could still make this unreliable

- Cameras set to PTP / MTP are expected to expose the property; the same camera set to mass storage, or to a
  tethering / "PC remote" mode that uses a maker driver, may not. Which modes work per maker is not known yet.
- Phones normally list as portable devices, but some only report a version once the user has chosen
  "File transfer" on the phone, and Android may not fill the firmware field.
- Some devices need to be unlocked before Windows will answer.
- Opening a device can briefly claim it. The probe opens read-only (`GENERIC_READ`), but this should be watched
  with a camera that another program (the maker's own tool) is connected to.

## Maker firmware pages

Each URL below was requested on 2026-10-15. "Opened" means the page loaded and its content mentions firmware
or downloads. Several makers refuse scripted requests, so those were opened in the app's browser pane or marked
as not fully verified. None is guessed; where I couldn't confirm a page, it says so.

| Maker | URL | Status |
|---|---|---|
| Canon (US) | https://www.usa.canon.com/support/software-and-drivers | Opened in a browser. Search by product from there. curl got "Access Denied", so a Canon page may not load for scripted checks. Other Canon regions not verified. |
| Nikon | https://downloadcenter.nikonimglib.com/en/index.html | Opened. Its "What's New" list includes Firmware entries. Per-model page example: https://downloadcenter.nikonimglib.com/en/products/589/Z_9.html (opened). |
| Sony | https://www.sony.com/electronics/support | **Not fully verified.** `esupport.sony.com` redirects to it, so the address is real, but sony.com refuses scripted requests and the browser pane here blocks the domain, so I never saw the page. Sony's own regional links include https://www.sony.eu/support/ and https://www.sony-asia.com/section/support, also refused to curl and unseen. A Sony camera needs a real person to check this in a browser. |
| Fujifilm | https://www.fujifilm-x.com/global/support/download/firmware/cameras/ | Opened ("FUJIFILM Camera Firmware", X Series and GFX). US variant: https://www.fujifilm-x.com/en-us/support/download/firmware/cameras/ (opened). |
| Panasonic (LUMIX) | https://av.jpn.support.panasonic.com/support/global/cs/dsc/download/index.html | Opened. Has a search by model number. US shop page https://shop.panasonic.com/pages/lumix-firmware-updates (opened, titled "LUMIX Firmware Updates & Downloads"). |
| OM System | https://support.jp.omsystem.com/en/support/imsg/digicamera/download/software/ | Opened: "Download Files: Software, Firmware, Drivers". Also lists Olympus-branded cameras (Stylus, TG and others). US alternative https://explore.omsystem.com/us/en/drivers-and-downloads (opened). |
| Olympus | Use the OM System page above. | The old host `dl-support.olympus-imaging.com` no longer resolves (DNS failure), so it must not be used. |
| Leica | https://leica-camera.com/en-int/downloads | Opened ("Downloads | Leica Camera AG"; has a Firmware category). Per-model pages also exist, e.g. https://leica-camera.com/en-int/photography/cameras/q/q3-43-black/downloads (opened). These per-model addresses are slug-based, so only link the hub unless the model is matched exactly. |

Guidance for the future UI: link the maker's general firmware page (the table above) rather than a model deep link,
because deep links change. Keep the URL list in one place in code, open it with the default browser only when
the user clicks, and never fetch it.

## How to run the probe

See `tools/CameraProbe/README.md`. In short:

```
dotnet run --project tools/CameraProbe -p:Platform=x64 -- --log probe.txt
```

## Results from real devices

None yet. After running the probe with each device, add a row here:

| Device | Mode on the camera / phone | WPD lists it? | Manufacturer | Model | Firmware read? | Notes |
|---|---|---|---|---|---|---|
| (empty) | | | | | | |
