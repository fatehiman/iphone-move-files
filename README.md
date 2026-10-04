# iPhone Mover

<img src="docs/icon.png" width="64" align="right" alt="icon">

A small Windows app that **moves** photos and videos from an iPhone (connected with USB) to a folder on the PC.

"Move" means: for each photo, the app copies it, checks the copy, and only then deletes it from the iPhone.

**Download:** see [Releases](../../releases/latest).
- `IphoneMover.exe` is small, and it needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
- `IphoneMover-standalone.exe` is large, and it needs nothing else.

```
┌ iPhone: [Apple iPhone ▼] [Find devices] [Reload files from phone] ──────────────┐
│ On the iPhone                          │ On this PC                             │
│ Show: (•) Folders ( ) Files            │ [D:\ ▼] [Up] [New folder] [Refresh]    │
│ ☑ 202604__   412 files  622 MB         │ Destination: D:\Photos\iPhone          │
│ ☑ 202605__   513 files  888 MB  moving │ 📁 202603__   (right-click: delete,     │
│ ☑ 202606__   379 files  759 MB         │ 📁 202604__    rename, open, ...)       │
├────────────────────────────────────────┴────────────────────────────────────────┤
│ ☑ Delete from iPhone after verified copy  [Move checked folders →] [Stop] ▓▓░  │
│ 1,204 / 1,304 files — 2.1 GB of 2.2 GB — 31 MB/s — left: 0:00:42               │
└─────────────────────────────────────────────────────────────────────────────────┘
```

## Main features

- **Folder view** (default): one row per phone folder (iOS makes one per month, for example `202605__`), with the file count, size and date range. Check all folders, click **Move**, and leave the computer.
- **File view**: the files of one folder (or all files) for moving single files.
- **One photo at a time**: copy → check → delete → next photo. You can stop or close the app at any time. Next time, check the folders again and click Move: it continues with the rest.
- **The same folders on the PC**: phone folder `DCIM\202605__` becomes `<Destination>\202605__\`. There is no `DCIM` level. A log file `iphone-mover-log.csv` is in each folder.
- **All file types** in a folder are moved: photos, videos, `.AAE`, and so on.
- Folders with no files left are not shown.
- **Low disk space**: before each photo, the app checks the destination drive. If less than **1 GB** would be free, the move **pauses** and asks you to free space (Retry / Cancel).
- **Safe for long runs**: the PC does not go to sleep during a move.
- **Reconnects by itself**: if the connection to the phone breaks (phone locked or asleep, USB reset), the app opens a new connection and repeats the same photo. If the phone does not come back after about 1 minute, the move **pauses** and asks you to unlock or replug the iPhone (Retry / Cancel).
- The move stops after 10 failures in a row that are not connection problems.
- **Delay slider** (top row, 0–500 ms, default **75 ms**): a pause after each phone action (read a file, delete a file, list a folder, check a delete). This gives the phone time and makes long moves more stable. While you drag the slider, the old value stays in use. The new value is used when you release the mouse button, also during a move. 75 ms worked well for a long move of 25,000 files.
- While a move runs, the check boxes on the left are locked.
- **PC side**: right-click menu with Open, Show in Explorer, New folder, Rename (F2), Delete to Recycle Bin (Del), and Delete permanently (Shift+Del).
- **Remembers** the device, the view, the phone folder, and the PC folder. On start, it loads the phone automatically. If something is not found, it uses the first device or drive.

## How a photo is moved

For every photo, these steps run in order:

1. **Copy** each file from the phone to `<Destination>\<phone folder>\<name>.part`.
2. **Size check**: the bytes received and the file size on disk must both equal the size the iPhone reports.
3. **Format check** (header and structure, see below). If a check fails, the `.part` file is removed and the photo stays on the phone.
4. **Rename** `.part` to the real name. Set the file dates from the phone.
5. **Delete** from the iPhone, but only if every file of the same photo passed all checks.
   - The app deletes with WPD. If that fails, it sends the PTP `DeleteObject` operation directly to the phone.
   - Then it asks the **phone itself** (PTP `GetObjectInfo`) if the file is gone. It does not ask the Windows driver, because the driver can still show a deleted file from its cache.

If a file is already on the PC (same size, valid format), it is not copied again. Only the delete step runs.

### Files that belong together

iOS stores one photo as several files, for example:

| File | What it is |
|---|---|
| `IMG_1234.HEIC` | the photo |
| `IMG_1234.MOV` | the Live Photo video |
| `IMG_E1234.HEIC` | the edited version |
| `IMG_1234.AAE` | the edit settings |

If iOS deletes one of them, it can remove the whole photo. So the app always moves these files **as one group**: it copies and checks all of them, then it deletes all of them. If any file in the group fails, nothing in the group is deleted.

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
2. **iCloud Photos should be OFF**. If it is ON, the phone may refuse deletes over USB, and with "Optimize iPhone Storage" the phone only has small versions of many photos.
3. **Unlock** the phone and tap **Trust** when you connect it.
4. For a long move: set **Settings → Display & Brightness → Auto-Lock → Never** while the move runs, and keep the phone charging.

> ⚠️ A photo deleted from the PC may **not** go to "Recently Deleted" on the phone. Test with a few photos first. You can also uncheck **"Delete from iPhone after verified copy"** to only copy.

## How to use

1. Connect the iPhone with USB, unlock it, and tap **Trust**.
2. Start `IphoneMover.exe`. It finds the iPhone and loads the folder list. This is about 4 seconds for 25,000 files. After a lost connection or a replug, Windows has no cache, so it can take **about 2 minutes**. The status line shows the progress, and **Stop** works. If not found, click **Find devices**.
3. On the right side, pick the drive and the destination folder. You can use **New folder**.
4. On the left, click **Check all shown** (or check single folders). Double-click a folder to see its files.
5. Click **Move checked folders →** and confirm.
6. Watch the **Status** column:
   - Folder view: `moving... 312 left`, then `✔ all moved`.
   - File view: ✔ moved, ◐ copied but kept on the phone (the reason is shown), ✖ failed (the file is still on the phone).
7. To continue later: start the app, check the folders again, and click Move.

## Requirements

- Windows 10 or 11, 64-bit.
- The .NET 8 Desktop Runtime (x64). This is not needed for the standalone exe.
- Windows must see the iPhone as "Apple iPhone" under *This PC*. If it does not, install the **Apple Devices** app (or iTunes) from the Microsoft Store for the USB driver.

## Build

```
dotnet build -c Release
dotnet test -c Release
dotnet publish src/IphoneMover -c Release -p:PublishSingleFile=true -o publish
dotnet publish src/IphoneMover -c Release -p:PublishSingleFile=true --self-contained true -p:EnableCompressionInSingleFile=true -o publish-standalone
```

The icon is drawn by `tools/make-icon.ps1`. Run it to create `src/IphoneMover/app.ico` again.

### Test with a real iPhone (copy only, never deletes)

```
set IPHONE_COPY_TEST=D:\temp\copytest
dotnet test -c Release --filter DeviceCopyTests --logger "console;verbosity=detailed"
```

It lists the phone, copies the smallest JPG, HEIC and MOV to that folder, checks them, and checks that they are still on the phone. Without the variable, this test does nothing.

### Delete diagnostic (read-only)

```
set IPHONE_DIAG=1
dotnet test -c Release --filter DeviceDiagnosticTests --logger "console;verbosity=detailed"
```

It prints the storage access rights, the `CanDelete` flags, and the PTP operations the phone supports (`DeleteObject` = 0x100B). Tested with an iPhone on iOS 16.3.1: `DeleteObject` is supported and the storage is read/write.

## How it works (for developers)

| Path | What it does |
|---|---|
| `src/IphoneMover/Wpd/WpdInterop.cs` | COM interface definitions for **WPD** (Windows Portable Devices), the Windows API for MTP/PTP devices. |
| `src/IphoneMover/Wpd/WpdDevice.cs` | List devices and files, download (`IPortableDeviceResources.GetStream`), delete (`IPortableDeviceContent.Delete`), and raw PTP commands (`WPD_COMMAND_MTP_EXT_*`). |
| `src/IphoneMover/Wpd/WpdWorker.cs` | All WPD calls run on one MTA background thread. |
| `src/IphoneMover/Core/FormatValidator.cs` | The format checks. |
| `src/IphoneMover/Core/AssetGrouping.cs` | Groups the files of one photo (Live Photo, edits, AAE). |
| `src/IphoneMover/Core/MoveEngine.cs` | Copy → check → rename → delete, free-space pause, and the CSV logs. |
| `src/IphoneMover/MainForm.cs` | The WinForms window. |
| `src/IphoneMover/Settings.cs` | Remembered settings in `%AppData%\IphoneMover\settings.json`. |
| `tests/IphoneMover.Tests` | xUnit tests, plus the optional real-device tests. |

The WPD interface layouts were cross-checked with [MediaDevices](https://github.com/Bassman2/MediaDevices) (MIT). The idea of reading `WPD_OBJECT_ORIGINAL_FILE_NAME` (the iPhone hides extensions in the normal name) comes from [iphone-photo-importer](https://github.com/mccubbinds/iphone-photo-importer).

The app does not use libimobiledevice/AFC. Deleting files with AFC does not update the iOS Photos database, so the deleted photos still show in the Photos app as broken "ghost" items. A delete over WPD/PTP goes through iOS, which updates the library correctly.

## License

[MIT](LICENSE)
