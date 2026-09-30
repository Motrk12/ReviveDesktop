# Revive

A Windows app that gets back deleted photos, videos, music and documents from memory cards, USB sticks,
hard drives and the Recycle Bin. It only ever **reads** the drive being recovered and never writes to it.

![Revive icon](src/Revive.App/Assets/Revive-256.png)

**[Download Revive.exe](https://github.com/Motrk12/ReviveDesktop/releases/latest)** for Windows 10/11 (64-bit). It's a single file with nothing to install.

## Using it

1. Run `Revive.exe`. If Windows SmartScreen says "Windows protected your PC", choose **More info** and then **Run anyway**
   (the app isn't code-signed yet). You can also build it yourself; see below.
2. Pick where the files were lost: the Recycle Bin, a drive, a memory card, or a disk image file.
   Reading a drive directly needs administrator rights; Revive offers to restart itself as administrator.
3. Choose **Quick scan** (seconds to minutes, keeps original names and folders) or **Deep scan**
   (searches every free sector; also works after formatting).
4. Browse the results by type, search, and preview photos. Each file shows its **recovery chance**.
5. Tick what you want and click **Recover selected files…**. Save to a *different* drive; Revive warns you if you don't.

Recovered files go into a `Recovered <date>` folder with the original folder structure. Deep-scan finds
go into `Photos`, `Videos`, and so on. Nothing already there is ever overwritten.

You can also open a disk image straight away: `Revive.exe card.img`.

## How it works

| Method | What it reads | Keeps names? | Notes |
|---|---|---|---|
| Recycle Bin | `$Recycle.Bin\<your SID>\$I…/$R…` pairs | Yes | Works without admin rights. |
| NTFS quick scan | Master File Table records whose "in use" flag is cleared | Yes, with full path | Checks the volume bitmap to rate each file *Excellent* / *Poor*. Handles fragmented files, sparse runs, attribute lists and small files stored inside the record. |
| FAT12/16/32 quick scan | Directory entries marked `0xE5` | Yes (long names rebuilt from LFN entries via checksum) | The lost first letter of 8.3 names is borrowed from a sibling with the same pattern (`_MG_0001.JPG` → `IMG_0001.JPG`). Assumes contiguous clusters. |
| exFAT quick scan | Directory entry sets with the in-use bit cleared | Yes | Uses the "no FAT chain" flag or the surviving FAT chain. |
| Deep scan | Every free sector, looking for file headers | No (`Photo 00001.jpg` …) | Walks each file's structure to find where it ends, rather than guessing a size. Skips space owned by existing files and anything the quick scan already found. |

Deep-scan formats: JPEG (with EXIF date taken), PNG, GIF, BMP, TIFF and TIFF-based RAW (CR2, NEF, ARW, DNG, PEF),
HEIC/AVIF/CR3, MP4/MOV/M4V/3GP/M4A, AVI (including >1 GB OpenDML), WAV, WebP, MKV/WebM, Ogg/Opus, MP3,
PDF, ZIP/DOCX/XLSX/PPTX/ODT/EPUB/APK, DOC/XLS/PPT/MSG, and 7z.

Disks with a partition table (MBR or GPT), such as whole-card images, are scanned partition by partition.
Unreadable (bad) sectors are read as zeros and counted, so a failing card doesn't stop the scan.

## Limits worth knowing

- **SSDs**: most SSDs erase deleted data soon after deletion (TRIM), so files deleted from an internal SSD
  are often gone. Memory cards, USB sticks and hard drives are far more recoverable. Revive flags files whose
  data has been blanked.
- **Phones** that connect over MTP (most Android phones, iPhones) don't appear as drives and can't be scanned.
  Scan the phone's SD card in a card reader instead.
- **Fragmented files** without file-system records (deep scan) may come back damaged, especially large videos.
- NTFS **compressed or encrypted** files are skipped.
- Reading a drive directly (quick or deep scan) needs **administrator** rights.

## Building and testing

Requires the .NET 8 SDK on Windows.

```bash
dotnet build Revive.sln
dotnet test tests/Revive.Tests
dotnet publish src/Revive.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

The tests carve real media files (generated with ffmpeg, in `tests/TestData/media`) out of noise-filled disks,
and scan real FAT16, FAT32, exFAT and NTFS images that were created, filled and partly deleted by the Linux
file-system drivers. The images are not checked in; build them with:

```bash
wsl -d docker-desktop -- sh /mnt/host/g/Recover/tests/tools/make-fs-images.sh
```

(any root Alpine shell works; see the script header). Without them, those tests are skipped.

## Project layout

```
src/Revive.Core/        Recovery engine (no UI)
  IO/                   Raw volume, disk image and sub-range sources; extent streams
  FileSystems/          NTFS, FAT, exFAT scanners; allocation maps; MBR/GPT
  Carving/              Deep-scan engine and per-format carvers
  RecycleBin/           Recycle Bin reader
  Recovery/             Safe copy-out (no overwrites, sanitised names)
  Scanning/             Scan orchestration and drive listing
src/Revive.App/         WPF app (MVVM)
tests/Revive.Tests/     xUnit tests
tests/tools/            Script that builds the real file-system test images
```
