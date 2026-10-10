# Smart recognition: evaluation and tuning

How the rules in `Services/SmartScoring.cs` were chosen, and how well they work. Measured on
9 October 2026 with `tools/SmartEval` (see its README to run it again) on both model tiers:
Compact (CLIP ViT-B/32, int8) and Accurate (the same model, fp32).

## The evaluation set

178 photos, each labelled by looking at it (`tools/SmartEval/labels.csv`):

- 104 of the owner's photos from Z:\Photos (people, events, sport, concerts). Only one has a test
  subject in it: a dog.
- 74 public-domain photos from Wikimedia Commons (`tools/SmartEval/cc0-sources.csv`), chosen to
  contain the test subjects.

Positives: sunset over water 15, dog 17, people at a table 9, snow 12, food 20. Photos where a
subject is only partly there (a snowy pavement, hands holding food) are labelled ambiguous and
aren't counted either way. Four extra queries have no positives at all (cat, fireworks, elephant,
christmas tree): a good search shows nothing for them.

## Search

### What was wrong

The old rule kept every photo scoring at least `max(0.19, best - 0.07)` against the raw query.
Raw CLIP similarities are a poor yardstick on their own:

- Right and wrong photos overlap. Compact: right photos scored 0.22–0.27, wrong ones up to 0.25.
- Each query has its own level. Unrelated photos score about 0.17 against "sunset over water" but
  0.195 against "dog", so no single floor fits every query.
- Some photos are close to every sentence. CLIP finds them generally "photo-like", so they turn up
  for anything, including "elephant".

### The new rule

1. Send "a photo of {query}" instead of the bare words (unless the query already starts with
   "a photo", "picture of" and so on). CLIP was trained on captions, and this alone tightens the
   scores.
2. Compare each photo with the query and with four neutral prompts ("a photo", "a photo of
   something", "a picture", "an image"), and score it by how much closer it is to the query than
   to the neutral prompts' average. This cancels both the photo-like photos and most of each
   query's own level.
3. Show photos whose margin is 0.025 or more, best margin first.

Across all queries, nine in ten right photos had a margin of 0.028 or more (median 0.044), and 99%
of wrong photos less than 0.016. The same threshold suits both tiers. Ranking by margin is also
better: mean average precision 0.994 (Compact) and 1.000 (Accurate), from 0.962 and 0.988 with the
old raw scores.

Rules tried and rejected: a higher floor plus a gap to the best score (best F1 0.88 even with the
"a photo of" prompt, and each tier needed its own floor), and outlier detection against the
library's own scores (works on this set, but would fail on a library that is mostly dogs).

### Before and after

Shown: photos the search returns. Right / wrong / missed: labelled positives shown, other photos
shown, positives not shown. Before = old rule, after = new rule; top 3 are the first results.

**Compact**

| Query | Before: shown | right / wrong / missed | top 3 | After: shown | right / wrong / missed | top 3 |
|---|--:|:-:|---|--:|:-:|---|
| sunset over water | 27 | 15 / 10 / 0 | cc0 sunset 06<br>cc0 sunset 01<br>cc0 sunset 12 | 16 | 15 / 0 / 0 | cc0 sunset 17<br>cc0 sunset 03<br>cc0 sunset 01 |
| dog | 101 | 17 / 84 / 0 | cc0 dog 11<br>cc0 dog 07<br>101 IMG_3797 | 16 | 16 / 0 / 1 | cc0 dog 10<br>cc0 dog 15<br>cc0 dog 12 |
| people at a table | 162 | 9 / 147 / 0 | cc0 table 11<br>cc0 table 02<br>cc0 table 03 | 12 | 8 / 2 / 1 | cc0 table 11<br>cc0 table 03<br>cc0 table 04 |
| snow | 30 | 12 / 15 / 0 | cc0 snow 08<br>cc0 snow 09<br>cc0 snow 13 | 14 | 12 / 0 / 0 | cc0 snow 08<br>cc0 snow 09<br>cc0 snow 04 |
| food | 96 | 20 / 68 / 0 | cc0 food 13<br>cc0 food 03<br>cc0 food 02 | 22 | 20 / 0 / 0 | cc0 food 14<br>cc0 food 11<br>cc0 food 07 |
| cat | 97 | 0 / 97 / 0 | cc0 dog 03<br>cc0 dog 13<br>cc0 dog 08 | 0 | 0 / 0 / 0 |  |
| fireworks | 103 | 0 / 103 / 0 | 075 IMG_6622<br>043 girls<br>019 IMG_5597 | 0 | 0 / 0 / 0 |  |
| elephant | 130 | 0 / 130 / 0 | 085 IMG_1901<br>075 IMG_6622<br>028 IMG_8600 | 0 | 0 / 0 / 0 |  |
| christmas tree | 53 | 0 / 53 / 0 | cc0 snow 06<br>cc0 snow 01<br>cc0 snow 13 | 3 | 0 / 3 / 0 | cc0 snow 06<br>cc0 snow 01<br>cc0 snow 04 |

**Accurate**

| Query | Before: shown | right / wrong / missed | top 3 | After: shown | right / wrong / missed | top 3 |
|---|--:|:-:|---|--:|:-:|---|
| sunset over water | 18 | 15 / 1 / 0 | cc0 sunset 09<br>cc0 sunset 12<br>cc0 sunset 11 | 14 | 14 / 0 / 1 | cc0 sunset 11<br>cc0 sunset 06<br>cc0 sunset 09 |
| dog | 28 | 17 / 11 / 0 | cc0 dog 01<br>cc0 dog 03<br>cc0 dog 09 | 16 | 16 / 0 / 1 | cc0 dog 07<br>cc0 dog 15<br>cc0 dog 12 |
| people at a table | 36 | 9 / 22 / 0 | cc0 table 11<br>cc0 table 12<br>cc0 table 02 | 10 | 9 / 0 / 0 | cc0 table 11<br>cc0 table 12<br>cc0 table 08 |
| snow | 47 | 12 / 32 / 0 | cc0 snow 09<br>cc0 snow 08<br>cc0 snow 03 | 14 | 11 / 0 / 1 | cc0 snow 09<br>cc0 snow 01<br>cc0 snow 08 |
| food | 56 | 20 / 29 / 0 | cc0 food 10<br>cc0 food 13<br>cc0 table 06 | 21 | 20 / 0 / 0 | cc0 food 11<br>cc0 table 08<br>cc0 table 06 |
| cat | 27 | 0 / 27 / 0 | 085 IMG_1901<br>cc0 dog 03<br>063 IMG_9642 | 0 | 0 / 0 / 0 |  |
| fireworks | 57 | 0 / 57 / 0 | 043 girls<br>085 IMG_1901<br>062 IMG_9536 | 0 | 0 / 0 / 0 |  |
| elephant | 72 | 0 / 72 / 0 | 085 IMG_1901<br>025 IMG_7761<br>035 IMG_9509 | 0 | 0 / 0 / 0 |  |
| christmas tree | 48 | 0 / 48 / 0 | cc0 snow 04<br>cc0 snow 01<br>cc0 snow 06 | 2 | 0 / 2 / 0 | cc0 snow 04<br>cc0 snow 01 |

What remains:

- "christmas tree" finds snow-laden fir trees, which look like one.
- Compact counts two "people at a table" extras: a set place with a wrap and a drink but nobody
  there, and people round a barbecue. It misses a close-up of an elderly couple at lunch.
- Both tiers miss a dog that is a speck in a field at sunset; Accurate also misses a hazy sun over
  a beach and a road through snowy hills seen from above.

## Tag suggestions

### What was wrong

Suggestions are a softmax over about 230 labels: each label's confidence is how much better it
fits than all the others, and labels at 10% or more were suggested. Two things left many photos
with nothing:

- Near-duplicates split the confidence. A clear portrait came out as "portrait" 5% and "posed
  portrait" 4%; a beach volleyball photo as "beach volleyball" and "volleyball".
- With 230 labels, a photo with nothing striking in it rarely gives any label 10%.

### The new rule

1. Labels that mean nearly the same thing form groups (`SmartLabels.Groups`): portrait / posed
   portrait, volleyball / beach volleyball, baseball / softball, running / track and field,
   boat / sailboat / sailing, camping / campsite, sunset / sunrise / golden hour, night sky /
   stars / milky way, building / apartment building, jacket / coat, swimwear / swimsuit, cake /
   dessert. A group's confidence is its labels' sum, shown under its best label.
2. The most confident label (or group) is suggested from 5%; further ones still need 10%. At most 6.

Tried and rejected: a lower threshold for every label (more tags, but many more wrong ones such as
"bear" for a dog and "cow" at a concert), a softer softmax (fewer tags), and scoring labels
against the neutral prompts like search does (labels are too short for it; most photos got
nothing).

### Before and after

Expected tag: a suggestion names the photo's labelled subject (for example "dog", or "sunset" for a
sunset over water; for people at a table, "restaurant", "family", "food" and the like).

**Compact**

| | Photos with no tags | Tags per photo | Expected tag suggested |
|---|--:|--:|--:|
| Before | 58 of 178 | 1.1 | 55 of 73 |
| After | 11 of 178 | 1.4 | 63 of 73 |

**Accurate**

| | Photos with no tags | Tags per photo | Expected tag suggested |
|---|--:|--:|--:|
| Before | 9 of 178 | 1.8 | 60 of 73 |
| After | 0 of 178 | 1.8 | 63 of 73 |

A rough check for wrong tags (animal labels on photos without that animal, sport labels on the
Commons photos, food labels on the owner's photos without food) counts the same number before and
after: 17 on Compact and 29 on Accurate. Of the 47 Compact photos that had no tags before and get
one now, the tag is right for 36 (mostly "portrait"). Between 4% and 5%, only 2 of 9 such tags
were right, which set the threshold. Examples, Compact:

| Photo | Before | After |
|---|---|---|
| Outdoor head-and-shoulders portrait | – | portrait |
| Woman in a leather jacket on a bench | – | jacket |
| Sunset over the sea with a jetty | – | sunset |
| Sunset over a beach | beach, ocean | sunset, beach, ocean |
| Husky in a snowy forest | – | dog |
| Beach volleyball player | beach volleyball, volleyball | beach volleyball |
| Man and woman walking past a car | – | couple |
| Woman in a plaid shirt | – | jacket |
| Dog playing with a ball | soccer | soccer |
| Women feeding a goat | horse, cow, horse riding, farm | horse, cow, horse riding, farm |

The last three show the limits: the best guess can be wrong, one strong label can crowd out the
right one, and labels that aren't in the list (goat) can't be suggested.

### New labels: goat, protest, speech

The tables above were measured before these three labels were added. Most remaining tag errors
on the owner's photos were things the list had no word for: a goat at a farm, rallies that only
got "flag" or "sign", people speaking or reading at a microphone. Candidate labels were scored
with `--dump`, and the tag rule was re-run on the dumped scores with each candidate set before any
was added. Three were kept:

- **goat**, with the other animals.
- **protest**: it wins clearly on all three rally photos with placards (56–91%) and nowhere else.
- **speech**: right on four photos of people talking or reading at a microphone, on Accurate.

Tried and left out:

- "rally" tags an ice-rink photo (as in a car rally); "demonstration" tags an independence-day
  speech and a poetry reading. "protest" alone does the job.
- "singer" turns up on about ten ordinary portraits on Accurate ("a photo of singer" reads like a
  celebrity portrait), even grouped with "concert".
- "microphone" pushes "concert" off two concert photos on Compact; grouped with "concert", three
  concerts show "microphone" instead. "musician" and "band" grouped with "concert" do the same.
  "public speaking" takes "concert" from a singer with a guitar on Compact.
- "stage" and "audience" hardly change anything. "pig", "chicken", "rabbit", "donkey" and "llama"
  cause no wrong tags, but the set has no photos to show they help, so they wait.

None of the three is a near-duplicate of an existing label, so `SmartLabels.Groups` is unchanged
("protest" is not a kind of "parade"; grouping them would hide parades behind it).

Before = the list without the three labels, after = with them; both use the current rule.

| | Compact: no tags | tags per photo | expected tag | Accurate: no tags | tags per photo | expected tag |
|---|--:|--:|--:|--:|--:|--:|
| Before | 11 of 178 | 1.4 | 63 of 73 | 0 of 178 | 1.8 | 63 of 73 |
| After | 10 of 178 | 1.4 | 63 of 73 | 0 of 178 | 1.8 | 63 of 73 |

Every own/ photo whose tags changed:

| Photo | Compact before | Compact after | Accurate before | Accurate after |
|---|---|---|---|---|
| 007 speaker at a microphone | portrait | portrait | portrait | speech, portrait |
| 010 woman talking into a microphone, seated | – | – | concert | speech |
| 016 the same speaker as 007 | posed portrait | posed portrait | portrait | speech, portrait |
| 018 singer with a guitar | concert | concert | concert | speech, concert |
| 020 placards at a night protest | – | protest | poster, crowd | protest |
| 049 reading from a book at a microphone | book | speech | book, jacket | speech |
| 091 rally with placards and flags | flag | protest | parade, crowd, poster | protest |
| 092 rally placard | sign | protest | crowd, parade, text, book, poster | protest, speech |
| 104 women feeding a goat | horse, cow, horse riding, farm | goat, horse, cow | horse, cow | horse, cow, goat |

New wrong tags: "speech" on the singer (018, next to "concert", which stays) and on the rally
placard (092). Lost along the way: "farm" for the goat (right) and "horse riding" (wrong) on
Compact, and on the Commons photos three tags that sat just above 10% and now fall just below it:
"kitchen" on dishes at a counter (doubtful), "restaurant" on a plated steak (fair) and
"restaurant" on a 1940s dining room (right; it keeps "birthday party", which still counts as an
expected tag). Each new label takes a little confidence from all the others, so the list should
only grow where photos need it.

## Caveats

- 178 photos is a small set, and the thresholds were chosen on it. The search margin sits in a
  wide gap between right and wrong photos, so it should carry over. The 5% tag rule only affects
  each photo's single best label.
- The ground truth is one person's judgement from looking at each photo.
- The tool asks Windows for the same thumbnails as the app. Its embeddings of the owner's photos
  still differ a little from the ones in the app's library (median cosine 0.975), probably because
  those were stored before the image preprocessing was fixed (commit dbfe037). Re-analysing the
  library would bring them in line.
