# Known issues

## SixLabors.ImageSharp 2.1.13 security advisories

Ocula stays on ImageSharp **2.1.13**, the last 2.x release, because `CoenM.ImageSharp.ImageHash`
(duplicate detection) only works with ImageSharp 2.x. NuGet reports five advisories against it
(all published 7 October 2026). Every one is fixed only in **4.1.2**, so no 2.x update helps.

### How Ocula uses ImageSharp

ImageSharp never reads the user's original files. It only decodes **thumbnails Ocula has
already made** (`ThumbnailService.GetBytesAsync`): a Windows Shell thumbnail, a JPEG re-encoded
by Magick.NET, or a JPEG of a video frame grabbed by VLC. There are three callers:

| Caller | What it does with ImageSharp |
|---|---|
| `DuplicateFinder.MakeFingerprint` | Decodes a thumbnail, resizes, rotates and flips it for pHash/dHash |
| `FaceService.Analyse` | Decodes a thumbnail, crops and aligns faces, saves face crops as JPEG |
| `SmartService.EncodeImage` | Decodes a thumbnail, resizes and crops it for CLIP |

Ocula never decodes TIFF or BigTIFF with ImageSharp, never encodes TIFF with it (exports use
Magick.NET), never calls `HistogramEqualization`, and never reads or converts the ICC profile
of an `Image`. The photos behind those thumbnails are local files in folders the user added.
Pictures saved by the web downloader (Phase 6) count as untrusted: they come from websites, then
are thumbnailed and analysed like any other photo.

### The advisories

| ID | Severity | What it affects | Ocula exposed? |
|---|---|---|---|
| [GHSA-gwg2-r3hj-4w44](https://github.com/advisories/GHSA-gwg2-r3hj-4w44) (CVE-2026-106114) | Medium | A malformed embedded **ICC profile** makes the ICC parser allocate memory from unchecked CLUT sizes (about 115 MB from a 200-byte profile in 1.x to 3.x; 860 MB in 4.x when decoding with ICC conversion on). Memory exhaustion, not code execution. | **Low.** Magick.NET's `Thumbnail()` keeps colour profiles, so a crafted profile in a user's photo can reach ImageSharp inside a thumbnail. In 2.x the profile is only parsed when something reads its entries or converts with it; Ocula does neither, and automatic ICC conversion during decoding is a 4.x feature. Worst case is a large allocation that fails one thumbnail's analysis. Mitigation if needed: strip profiles from the copy passed to ImageSharp. |
| [GHSA-j3p4-wp97-rph4](https://github.com/advisories/GHSA-j3p4-wp97-rph4) (CVE-2026-106113) | High | Out-of-bounds write in **`HistogramEqualization()`** on a 32-bit float TIFF decoded as `Image<HalfVector4>`. | **No.** Ocula never calls `HistogramEqualization`, never uses `HalfVector4` and never decodes TIFF with ImageSharp. |
| [GHSA-j9gm-c75j-xc9q](https://github.com/advisories/GHSA-j9gm-c75j-xc9q) (CVE-2026-106110) | High | Out-of-bounds write in the **TIFF CCITT Group 3 (T4) encoder** for narrow 1-bit images. | **No.** Ocula never encodes TIFF with ImageSharp. |
| [GHSA-jjfr-hcj7-qf5w](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w) (CVE-2026-106115) | High | Out-of-bounds write in the **TIFF CCITT Group 4 (T6) encoder** for 1-bit images. | **No.** Ocula never encodes TIFF with ImageSharp. |
| [GHSA-wmxv-xphr-5c9g](https://github.com/advisories/GHSA-wmxv-xphr-5c9g) (CVE-2026-106116) | Medium | A malformed **BigTIFF** entry count makes the decoder loop for a long time (denial of service). | **No.** Thumbnails reaching ImageSharp are JPEG or a Windows thumbnail bitmap, never TIFF. |

### What to do

- Keep the build warnings (NU1902/NU1903) visible rather than suppressing them, so a 2.x fix or
  new advisory isn't missed.
- Planned fix: replace `CoenM.ImageSharp.ImageHash` with Ocula's own pHash and dHash code, then
  move to ImageSharp 4.1.2 or later (see "Later" in `BUILDING.md`). ImageSharp 3.0 and later
  are under the Six Labors Split License rather than Apache-2.0; check that it still fits
  before upgrading.

## People models: licence and training data

**Short version: the licence of the SFace model is unclear, and it may not be cleared for a distributed
app. Settle this before release.** The files are labelled Apache-2.0, but nothing documents what the
network was trained on, and the original code has no licence at all.

What was checked (from the OpenCV Zoo and the source repositories, October 2026):

| | YuNet (finds faces) | SFace (tells faces apart) |
|---|---|---|
| Files Ocula downloads | `face_detection_yunet_2023mar.onnx`, 232,589 bytes, from `opencv/face_detection_yunet` revision `3cc26e7f` | `face_recognition_sface_2021dec.onnx`, 38,696,353 bytes, from `opencv/face_recognition_sface` revision `3d708243` |
| Licence of those files | **MIT** (the Zoo directory's `LICENSE`, Copyright 2020 Shiqi Yu) | **Apache-2.0** (the Zoo directory's `LICENSE`: "All files in this directory are licensed under Apache 2.0") |
| Where the model came from | `ShiqiYu/libfacedetection.train`, commit `a61a4289`; that repository is **BSD-3-Clause** | `zhongyy/SFace` (the TIP 2021 paper's code), converted to ONNX by a Zoo contributor. **That repository has no licence file** (GitHub lists none) |
| Training data | WIDER Face (its training README: "YuNet face detection training on WIDER Face") | **Not stated by the Zoo.** The Zoo says only "MobileFaceNet instances trained on the SFace loss function". The paper and the upstream README train on CASIA-WebFace, VGGFace2 and MS-Celeb-1M (as MS1MV2), and don't say which one produced these weights |
| Hashes | SHA-256 `8f2383e4…2fa4` | SHA-256 `0ba9fbfa…4e79` (both checked on download and in the tests) |

What I could not establish:

- **Which dataset trained the SFace weights.** If it was one of the three above, then, as far as I
  know and have *not* verified here, CASIA-WebFace and VGGFace2 were published for non-commercial
  research, and Microsoft withdrew MS-Celeb-1M in 2019. WIDER Face (YuNet) is also, I believe,
  offered for research use. Check the current terms of each from the dataset owners.
- **Whether an Apache-2.0 label from the Zoo is enough.** It covers what the Zoo contributors own
  (the conversion and the surrounding files). It can't grant rights in weights or data it doesn't hold,
  and the upstream code carries no licence.
- Ocula is free and asks for donations on GitHub. Whether that counts as "non-commercial" is a
  question for whoever advises on the licences, not something this repository can answer.

Options if this can't be cleared: ask the SFace author (`zhongyy`) for a licence and the training
set, move to a face-recognition model whose training data and licence are documented (the same check
applies to anything else), or ship People without the downloadable model and let users supply their own.

People also stays off until the user turns it on and downloads the models, and nothing leaves the PC.

