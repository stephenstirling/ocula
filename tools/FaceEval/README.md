# FaceEval

Measures Ocula's People feature on real photos, with the app's own face code (`FaceService`,
`YuNetDecoder`, `FaceAlignment`, `FaceGrouping`, linked from `Services/`), and asks Windows for the
same 960-pixel thumbnails the app analyses (falling back to Magick.NET for paths Windows can't
thumbnail, such as network drives, as the app does). Results and conclusions are in
[docs/PEOPLE-EVALUATION.md](../../docs/PEOPLE-EVALUATION.md).

Turn on People in Ocula once (Settings > People) so the models are downloaded; the tool finds them in
the packaged app's data folder (or pass `--models`).

## Find faces and draw the boxes

```
dotnet run --project tools/FaceEval -p:Platform=x64 -- detect --out "%TEMP%\ocula-faces\detect" photo1.jpg photo2.jpg ...
```

Writes a copy of each photo with its faces outlined (green: kept by the app, orange: found but
filtered out because the score is under 0.8 or the face is under 40 px) with the score and size,
and the five landmarks as dots. This mode looks for faces down to a score of 0.5 so you can see
what the app's filters remove.

## Check grouping

```
dotnet run --project tools/FaceEval -p:Platform=x64 -- group --labels labels.csv --root . --out "%TEMP%\ocula-faces\group"
```

`labels.csv` has a header line and then `path,person` per line (put a path in quotes if it contains a
comma). The largest face in each photo is taken to be that person. The tool prints how far apart
same-person and different-person similarities are, how a plain pair cut-off performs, and then groups
the faces the way People does (in order, joining the closest group above the threshold) for thresholds
from 0.35 to 0.55 over several photo orders, scoring each with pair precision, recall and F1, purity,
and the number of groups. Embeddings are cached in the output folder, so a rerun is quick.

| Option | |
|---|---|
| `--min-face` | Smallest face to use, in pixels (default 40, what the app uses). |
| `--orders` | How many photo orders to try (default 6: the given order and shuffles). |
| `--models` | The folder holding `yunet.onnx` and `sface.onnx`. |

To test a change to `FaceService` without editing it, build with
`-p:FaceServiceSource=path\to\modified\FaceService.cs`.

The labels are only as good as your ground truth. The ones used so far came from photo folders named
after a single person, which is a proxy: a folder can contain other people, and the largest face is
not always the person the folder is named for.
