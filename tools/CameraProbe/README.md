# CameraProbe

Research tool, not part of the app. Lists what Windows can tell us about USB-connected cameras and phones, to
find out whether Ocula can show a camera's make, model and firmware. Background and results:
`docs/CAMERA-FIRMWARE.md`.

```
dotnet run --project tools/CameraProbe -p:Platform=x64
dotnet run --project tools/CameraProbe -p:Platform=x64 -- --log probe.txt
```

It prints three sections:

1. **WPD**: every portable device with manufacturer, model, firmware version, friendly name, protocol, device type,
   transport, power level and the property names the device supports. Devices are opened read-only.
2. **WinRT**: what `Windows.Devices.Portable` / `DeviceInformation` return for the same devices (to see whether it
   could replace the COM code).
3. **USB drives**: cameras in mass-storage mode (drive letters, a DCIM folder check, and the SCSI firmware string,
   which belongs to the reader, not the camera).

The serial number is read and shown on screen only. It is never written to the `--log` file, and device instance
ids in the log keep only the USB vendor and product id. The tool doesn't write to any device, download anything
or contact the network.

## What to test

Plug in one device at a time, run the probe, and note the mode set on the device:

- A camera in **PTP / MTP** mode (the usual "PC connection" or "file transfer" setting).
- The same camera in **mass storage** mode, if it has one.
- A phone set to "File transfer".
- A camera with a card reader, to compare the drive result.

Then add a row per device to the results table in `docs/CAMERA-FIRMWARE.md`.
