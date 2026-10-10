# SmartEval

Measures Ocula's smart recognition on labelled photos: which photos a describe-it search shows
(`? sunset over water`) and which tags are suggested. It links the app's own `ClipModel`,
`SmartLabels` and `SmartScoring`, and asks Windows for the same 448-pixel thumbnails the app
analyses, so it measures what Ocula does. The results and the reasoning behind the current rules
are in [docs/SMART-EVALUATION.md](../../docs/SMART-EVALUATION.md).

## Run it

Turn on Smart recognition in Ocula once (Settings > Smart recognition) so the models are
downloaded; the tool finds them in the packaged app's data folder (or pass `--models`). Then, from
the repo root:

```
dotnet run --project tools/SmartEval -p:Platform=x64 -- --root "%USERPROFILE%\Pictures\Ocula smart eval" --labels tools/SmartEval/labels.csv
```

| Option | |
|---|---|
| `--root` | The photo folder. Paths in the labels file are relative to it. |
| `--labels` | Ground truth (default: `labels.csv` in the photo folder). |
| `--tier` | `compact`, `accurate` or `both` (default). |
| `--tags` | Also list every photo's tag suggestions, before and after. |
| `--dump` | Write every prompt's similarity to every photo to a CSV, for trying new rules. |

It prints a Markdown report. Image embeddings are cached in the photo folder
(`embeddings-<model>.bin`), so later runs take seconds.

## The evaluation set

`labels.csv` lists 178 photos: `own/` holds 104 of the owner's photos (mostly people and events,
copied from Z:\Photos), `cc0/` 74 public-domain photos from Wikimedia Commons chosen to contain
sunsets over water, dogs, people at a table, snow and food (`cc0-sources.csv` has each file's
title, licence, author and page). Each photo has the subjects clearly in it (`positives`) and
those only partly there (`ambiguous`, not counted either way). The photos themselves aren't in the
repository.

To re-download the CC0 photos, fetch each `source` page's file at 640 pixels wide and save it
under the `file` name in `cc0/`.
