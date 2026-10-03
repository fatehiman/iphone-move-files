# iPhone Mover

A small Windows app that **moves** photos and videos from an iPhone (connected with USB) to a folder on the PC.

"Move" means: for each file, the app copies it, checks the copy, and only then deletes it from the iPhone.

```
┌ iPhone: [Apple iPhone ▼] [Find devices] [Load files from phone] ───────────────┐
│ On the iPhone                         │ On this PC                             │
│ Folder: [DCIM\100APPLE (312 files) ▼]◀▶│                                        │
│ ☑ IMG_0001.HEIC  DCIM\100APPLE 2.1 MB │ [D:\ ▼] [Up] [New folder] [Refresh]    │
│ ☑ IMG_0001.MOV   DCIM\100APPLE 3.4 MB │ Destination: D:\Photos\iPhone          │
│ ☐ IMG_0002.JPG   DCIM\100APPLE 1.8 MB │ 📁 100APPLE                             │
├───────────────────────────────────────┴────────────────────────────────────────┤
│ ☑ Delete from iPhone after verified copy   [Move checked files →] [Cancel] ▓▓░ │
│ log ...                                                                         │
└─────────────────────────────────────────────────────────────────────────────────┘
```

- Left list: the files on the iPhone (name, folder, size, date, status).
- Right list: the folders and files on the PC. The **Destination** folder is where files go.
- No image preview, so the app stays fast and simple.

## How a file is moved

For every photo, these steps run in order:

1. **Copy** the file from the phone to `<Destination>\<phone folder>\<name>.part`.
2. **Size check**: the bytes received and the file size on disk must both equal the size the iPhone reports.
3. **Format check** (header and structure, see below). If a check fails, the `.part` file is removed and the photo stays on the phone.
4. **Rename** `.part` to the real name. Set the file dates from the phone.
5. **Delete** from the iPhone. This only happens if every file of the same photo passed all checks. After the delete, the app asks the phone again, to make sure the file is really gone.

Every action is written to `iphone-mover-log.csv` in the Destination folder.

### Files that belong together

iOS stores one photo as several files, for example:

| File | What it is |
|---|---|
| `IMG_1234.HEIC` | the photo |
| `IMG_1234.MOV` | the Live Photo video |
| `IMG_E1234.HEIC` | the edited version |
| `IMG_1234.AAE` | the edit settings |

If iOS deletes one of them, it can remove the whole photo. So the app always moves these files **as one group**. If you check one of them, the others are added for you, and the confirm dialog tells you. If any file in the group fails, nothing in the group is deleted.

### Format checks

| Type | What is checked |
|---|---|
| HEIC / HEIF / AVIF | All top-level boxes are read. The box sizes written in the headers must add up to **exactly** the file size. Must start with `ftyp` and have a `meta` box. |
| MOV / MP4 / M4V / 3GP | Same box-size check. Must have `moov` and `mdat` (or `moof`). |
| JPEG | Starts with `FFD8`. The segment lengths are walked until the image data. The file must end with `FFD9`. |
| PNG | Signature, every chunk length, and every chunk **CRC**. Must end at `IEND`. |
| GIF | Header and the end byte `0x3B`. |
| TIFF / DNG | Header, and every IFD is inside the file. |
| WEBP / AVI | The RIFF size in the header must equal the file size. |
| AAE | A complete property list (XML or binary). |

Other file types are copied but **never deleted**, because they cannot be checked.

## Before you use it: settings on the iPhone

1. **Settings → Photos → Transfer to Mac or PC → "Keep Originals"**.
   If this is "Automatic", iOS converts HEIC to JPG while copying. Then the size does not match, and the app will not delete anything.
2. **iCloud Photos should be OFF**. If it is ON:
   - the phone often refuses delete over USB (the app shows "the phone refused"), and
   - with "Optimize iPhone Storage", the phone only has small versions of many photos.
3. **Unlock** the phone and tap **Trust** when you connect it. Keep it unlocked while the list loads.

> ⚠️ A photo deleted from the PC may **not** go to "Recently Deleted" on the phone. Test with a few photos first. You can also uncheck **"Delete from iPhone after verified copy"** to only copy.

## How to use

1. Connect the iPhone with USB, unlock it, and tap **Trust**.
2. Start `IphoneMover.exe`. The iPhone is selected in the top list. If not, click **Find devices**.
3. Click **Load files from phone**. This is fast: about 4 seconds for 25,000 files.
4. On the right side, pick the drive and folder. You can use **New folder**.
5. **Work chunk by chunk.** iOS keeps one folder per month (for example `DCIM\202606__`). The **Folder** box on the left shows one folder at a time, with its file count and size. Use ◀ ▶ to go to the previous or next folder. Choose "All folders" to see everything.
6. Check the files on the left. You can use **Check all shown**, or select rows and click **Check selected rows**. Click a column header to sort. Checks are kept when you change folders. The label shows the total checked in all folders.
7. Click **Move checked files →** and confirm.
8. Watch the **Status** column:
   - ✔ moved (copied, checked, and deleted from the phone)
   - ◐ copied but kept on the phone (the reason is shown)
   - ✖ failed (the file is still on the phone)

If you run it again on the same folder, files that are already on the PC (same size, valid format) are not copied again. Only the delete step runs for them.

## Requirements

- Windows 10 or 11, 64-bit.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
- Windows must see the iPhone as "Apple iPhone" under *This PC*. If it does not, install the **Apple Devices** app (or iTunes) from the Microsoft Store for the USB driver.

## Build

```
dotnet build -c Release
dotnet test -c Release
dotnet publish src/IphoneMover -c Release -p:PublishSingleFile=true -o publish
```

`publish/IphoneMover.exe` is one file (it needs the .NET 8 Desktop Runtime).

### Test with a real iPhone (copy only, never deletes)

```
set IPHONE_COPY_TEST=D:\temp\copytest
dotnet test -c Release --filter DeviceCopyTests --logger "console;verbosity=detailed"
```

It lists the phone, copies the smallest JPG, HEIC and MOV to that folder, checks them, and checks that they are still on the phone. Without the variable, this test does nothing.

## How it works (for developers)

| Path | What it does |
|---|---|
| `src/IphoneMover/Wpd/WpdInterop.cs` | COM interface definitions for **WPD** (Windows Portable Devices), the Windows API for MTP/PTP devices. |
| `src/IphoneMover/Wpd/WpdDevice.cs` | List devices, list files, download a file (`IPortableDeviceResources.GetStream`), delete by object ID (`IPortableDeviceContent.Delete`). |
| `src/IphoneMover/Wpd/WpdWorker.cs` | All WPD calls run on one MTA background thread. |
| `src/IphoneMover/Core/FormatValidator.cs` | The format checks. |
| `src/IphoneMover/Core/AssetGrouping.cs` | Groups the files of one photo (Live Photo, edits, AAE). |
| `src/IphoneMover/Core/MoveEngine.cs` | Copy → check → rename → delete, plus the CSV log. |
| `src/IphoneMover/MainForm.cs` | The WinForms window. |
| `tests/IphoneMover.Tests` | xUnit tests for the format checks and grouping, plus the optional real-device copy test. |

The WPD interface layouts were cross-checked with [MediaDevices](https://github.com/Bassman2/MediaDevices) (MIT). The idea of reading `WPD_OBJECT_ORIGINAL_FILE_NAME` (the iPhone hides extensions in the normal name) comes from [iphone-photo-importer](https://github.com/mccubbinds/iphone-photo-importer).

The app does not use libimobiledevice/AFC. Deleting files with AFC does not update the iOS Photos database, so the deleted photos still show in the Photos app as broken "ghost" items. A delete over WPD/PTP goes through iOS, which updates the library correctly.
