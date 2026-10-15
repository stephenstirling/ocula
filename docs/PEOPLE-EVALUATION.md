# People: what was measured

People finds faces with YuNet, turns each into 128 numbers with SFace, and groups faces whose numbers are
close. This records the checks made in October 2026, how to repeat them (`tools/FaceEval`), and what
they showed. Licence and training-data questions are in [KNOWN-ISSUES.md](KNOWN-ISSUES.md).

## The models

Downloaded through Settings > People, then checked against what Hugging Face publishes:

| File | Bytes | SHA-256 |
|---|---|---|
| `yunet.onnx` (`opencv/face_detection_yunet` @ `3cc26e7f`) | 232,589 | `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4` |
| `sface.onnx` (`opencv/face_recognition_sface` @ `3d708243`) | 38,696,353 | `0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79` |

Both matched. The download now fails if either differs. The real tensors, printed from the files:

- YuNet: input `input` float32 [1, 3, 640, 640] (BGR, 0 to 255); outputs `cls_8/16/32` [1, 6400/1600/400, 1],
  `obj_*` [1, n, 1], `bbox_*` [1, n, 4] and `kps_*` [1, n, 10]. The code looks these up by name.
- SFace: input `data` float32 [1, 3, 112, 112]; output `fc1` float32 [1, 128].

## Decoding checked against OpenCV

The Zoo wrappers just call OpenCV's `FaceDetectorYN` and `FaceRecognizerSF`, so their source is the reference.
YuNet's decoding (score = sqrt(clamp(cls) x clamp(obj)); box centre (cell + offset) x stride; size
exp(raw) x stride; landmarks (raw + cell) x stride; zero padding to the bottom and right; BGR 0 to 255) matches
`face_detect.cpp` line by line. SFace's alignment template matches `face_recognize.cpp`.

**One bug found and fixed: SFace reads RGB.** OpenCV calls `blobFromImage(..., swapRB = true)` on its BGR
crop. Ocula fed it BGR. On the same 182 labelled faces (below), at SFace's own 0.363 pair cut-off:

| Input order | Same-person median similarity | Precision | False matches |
|---|---|---|---|
| BGR (before) | 0.41 | 78.8% | 226 |
| RGB (now) | 0.45 | 86.0% | 147 |

## Detection on 20 photos

Twenty varied photos (portraits, groups, crowds, profiles, a back view, low light, a dog), with the boxes drawn
on and every photo checked by eye (the images are in `%TEMP%\ocula-faces\detect` when the tool is run). The
app keeps faces scoring at least 0.8 and at least 40 px; the tool also shows what that filter removes.

| | Faces I count | Kept by Ocula |
|---|---|---|
| All 20 photos | 30 clearly visible | **22** (73%) |
| Wrong boxes kept | | **0** |

Every kept box was on a face and the landmarks (eyes, nose, mouth corners) sat where they should. The 8 misses were small
(under about 45 px), blurred, in profile in the dark, or hidden behind someone: two people kissing in a dark room
(none found even at a 0.5 score), a man behind a window (the two boxes in that photo scored 0.57 and 0.74), blurred or distant faces (0.60 to 0.76), and
three faces of 27 to 33 px that were found (0.82 to 0.89) but are under the 40 px minimum. YuNet's false alarms (a dog's head
0.56, a woman seen from behind 0.57, a partial profile 0.52) were all below 0.8, so none were kept. Lowering the score to 0.7 would add a few
real but small or blurred faces; those give poor embeddings, so 0.8 stays.

## Grouping thresholds

Ground truth was a proxy: 204 photos sampled from 15 folders each named after one person (12 people, three of them
shot on different dates), with the largest face in each photo taken to be that person. 182 photos had a usable face.
Folders can contain other people and the largest face isn't always the subject, so some "errors" are label noise.
Faces were grouped in order, as People does (join the closest group above the threshold, else start a new one),
over 6 photo orders.

Similarity between faces: same person median 0.45 (25% 0.19, 75% 0.63); different people median 0.14, 99% below 0.36,
99.9% below 0.45, maximum 0.60.

| Threshold | Groups (real: 12) | Single-face groups | Purity | Pair precision | Pair recall | Pair F1 |
|---|---|---|---|---|---|---|
| 0.350 | 29.3 | 14.3 | 89% | 79.2% | 64.1% | 0.707 |
| 0.363 | 31.5 | 15.3 | 92% | 83.3% | 63.8% | 0.722 |
| 0.375 | 33.2 | 16.2 | 94% | 89.4% | 62.3% | 0.734 |
| 0.400 | 38.3 | 22.3 | 96% | 93.1% | 60.9% | 0.736 |
| 0.425 | 39.7 | 23.2 | 97% | 95.3% | 60.5% | **0.739** |
| **0.450** (current) | 41.2 | 24.3 | 98% | 97.0% | 59.6% | 0.738 |
| 0.475 | 43.8 | 25.3 | 98% | 97.9% | 54.4% | 0.699 |
| 0.500 | 48.2 | 28.8 | 98% | 98.8% | 49.6% | 0.659 |
| 0.525 | 51.8 | 31.7 | 99% | 99.3% | 46.8% | 0.635 |
| 0.550 | 55.2 | 34.0 | 99% | 99.4% | 44.2% | 0.611 |

**Recommendation: keep 0.45.** F1 is flat from 0.375 to 0.45 (the spread between photo orders is about 0.02, more than
the differences), and recall falls off a cliff above 0.45. Below it, precision drops (93% at 0.40, 83% at 0.363). People's
rule is that two groups for one person is easy to fix (give both the same name and they merge) while one group holding
two people is worse, which favours the higher-precision end. Expect each real person to land in several groups: 41
groups for 12 people.

## In the app, on the real library

With the old BGR code, People scanned 3,452 photos of `Z:\Photos` and made 908 groups from 4,644 faces; after the fix and
a fresh scan, 3,662 photos gave 4,889 faces in 1,027 groups. Scanning runs at about 14 photos a second
(about 70 ms each) when it has the CPU to itself. The largest groups mix people: event photos in crowds produce
groups dominated by women in sunglasses, or by profile views, rather than by identity. SFace's embeddings follow pose and
sunglasses more than a studio-portrait test suggests. That is a limit of the model, and it is why every name is the
user's choice and groups can be hidden.

## What the People window does (checked in the running app)

| | Result |
|---|---|
| Name a group, with "Add the name as a tag" off | Saved; the card, the count ("1,026 still need a name") and the detail title update |
| Close and reopen the window | The name is still there |
| Type a name while scanning refreshes the list | **Was wiped every 3 seconds; fixed** (the name box now survives refreshes) |
| Hide this group | The card and the detail panel go, and the count drops by one |
| Delete all face data | Every face, group and name goes; the window says "People is off". **It left People switched on, so the next launch rescanned everything and rebuilt the groups, and a scan in flight could save one face after the delete. Fixed: it now waits for the scan to stop and turns People off, as its dialog says** |
| People after a restart | **Never started: the 40-second start timer was a local variable and could be collected before it fired. Fixed, and the same for Smart recognition** |

Not checked in the running app: "Not this person" (a right-click menu on a face, which could not be driven while the
windows weren't on the visible desktop), and what "Add the name as a tag" writes into the files (switched off for every test
so no photo was touched).

## Repeating the checks

```
dotnet run --project tools/FaceEval -p:Platform=x64 -- detect --out "%TEMP%\ocula-faces\detect" photo1.jpg ...
dotnet run --project tools/FaceEval -p:Platform=x64 -- group --labels labels.csv --root . --out "%TEMP%\ocula-faces\group"
dotnet test -p:Platform=x64
```
