# Grabbing the other pages of a forum thread: design (not built)

Status: **a proposal, nothing built.** `PageSnapshot.PageUrls` is filled in by the grab script and parsed by `PageScanner.Parse`
(Forum Scraper commit, f311a33), and nothing reads it. This says what it would take to use it without breaking the rules the Web
download window keeps, and asks the owner a few questions at the end.

## What exists today

- The grab script (`PageScanner.GrabScript`) looks in the page's own page-number controls (XenForo `.pageNav`, phpBB `.pagination`,
  vBulletin-style `.PageNav` / `.pagelinks` / `.pages`, and `nav[aria-label*="page"]`) for links with `rel="next"` or `"prev"`, a number as
  their text, or text like Next/Previous/«/». It de-duplicates them, leaves out the page itself, and returns at most 40.
- It does **not** check where a link goes. A link to another site in one of those controls comes through as it is.
- `ResolveAsync` already follows one kind of link: a picture's own link, opened once to find the full-size picture, with the page's cookies
  sent only to the page's own host.
- Discourse (and anything that loads more posts by scrolling) has no page links, so `PageUrls` is empty there.

## The rule, and what has to change

Today's rule, in `PageScanner`'s summary comment and BUILDING.md: *"It never goes further than one link, never fills in forms, and does not try
to get past ad screens, countdowns, paywalls or logins."* "One link" means: from a thumbnail, open its link once, and never follow a link found
on that page.

Reading a thread's other pages is a second kind of step, so the rule has to say so. Proposed wording, for BUILDING.md and the code comment
**when this is built** (today's behaviour is unchanged, so today's wording stays until then):

> From the page the user is on, Ocula takes at most two kinds of single step. It opens each picture's own link once, to find the full-size
> picture. And only if the user asks, it reads the other pages of the same thread that the page itself lists as its page numbers, on the same
> site, up to a fixed number. It never follows a link found on a page it opened for either step, so it cannot crawl: page links come only from
> the page the user is on, and a picture's link is read once and not followed further. It never fills in or sends forms, never signs in, and
> does not try to get past ad screens, countdowns, paywalls or logins.

So the rule is not "one link" any more but "one step, of two named kinds, from the page the user chose, and no step from a step". The thread's
other pages are the same document as the one the user is reading, split up by the site, which is why this is a fair extension; each picture on
them still gets exactly the one link it gets today.

## The design

### 1. Only when the user asks
- Grabbing a page does what it does now. If the page listed other pages of the thread on its own site, a line appears under the picture
  list: **"This thread has 4 more pages. Also grab their pictures?"** with a button. Nothing opens until it is pressed.
- No setting that does this automatically, no background or scheduled use.

### 2. Which pages
- Only the links in `PageUrls` from the page the user is on. **Ocula never guesses or builds a page address** (no "page-2 … page-N"), and
  never reads the page links of a page it opened itself.
- **Same origin only** (same scheme, host and port as the page the user is on). Stricter than "same site": a sibling subdomain, a CDN or another
  forum is out. Links that fail this are dropped, whatever the page lists.
- Must look like pagination, not any link in the control: a `rel` of next or prev, or an address with a recognisable page marker
  (`/page-N`, `/page/N`, `?page=N`, `&start=N`, `&offset=N`) on the same path or a path that differs only by that marker.
- De-duplicated, in page order, **at most 10 pages per press**. A longer thread is done by going to a later page in the pane and pressing again.

### 3. How a page is read
Recommended: **the visible browser pane navigates to each page in turn and runs the same `GrabScript`**, then goes back to the page the user
was on. Reasons: one parser, so what is found on page 3 is found by exactly the code that found it on page 1; sign-in and script-built pages
work as they do for the user; cookies are the browser's own and never leave the site they belong to; the user can see what is happening.
The alternative, fetching each page with `HttpClient` and parsing the HTML in C#, needs a second parser, can't read script-built pages, and
has to send cookies by hand (`Request` already restricts those to the page's host, which would carry over).

### 4. Where it stops
It stops, says why in one line, and keeps what it has when:
- the address after loading is not on the same origin (a redirect), or the response is not a success (401, 403, 429, 5xx all end it at once;
  429 and 503 are the site asking to slow down, and it does);
- the page has a password field, a captcha, or an interstitial it can't read past (which is read as "no pictures found" on that page; two
  such pages in a row end the run);
- Cancel is pressed, 3 minutes have passed, or 400 pictures have been collected.

### 5. Politeness
One page at a time; wait for the page to finish loading, then at least 1.5 seconds before the next. No parallel page loads.

### 6. What it never does
Submit or fill in a form; sign in; type into the page; click a button or a link on a page (it only navigates to an address from the list);
get past an ad screen, countdown, paywall or login; send cookies anywhere but the site they came from; keep going when the site says stop.

### 7. Results
Pictures from all pages are merged into the one list, de-duplicated by address, each with a small "page 3" label so the user can see where
it came from. Resolving full-size pictures is the existing step, one link per picture, with its existing limit of four at a time and
the same-host cookie rule.

### 8. Privacy
Network use stays: the site the user is on, plus the image hosts the pictures live on, as now. PRIVACY.md's Web download paragraph gains a
sentence: *"If you choose to grab a thread's other pages, Ocula opens up to 10 more pages of that same site in the browser pane, one at a time."*

## How it would be tested
- Pure function (no browser): choosing pages from `PageUrls`: same origin only, pagination-looking only, de-duplicated, ordered, capped at
  10, never the current page, never an address that wasn't listed.
- Browser tests like `ForumGrabTests`, against a small local server serving a three-page thread: all pages merged and de-duplicated;
  stops on a redirect to another origin; stops on 429; stops on a page with a password field; stops at the cap; Cancel.
- By hand, on a real XenForo and a real phpBB thread, before anything is called working.

## Questions for the owner
1. Is a cap of 10 pages per press right? (Proposal: 10, repeat from a later page for more.)
2. Same **origin** only (proposal), or same registrable domain?
3. Pane navigation (proposal) or `HttpClient` fetching?
4. Should the pane go back to the page the user was on afterwards (proposal: yes)?
5. Is it acceptable that Discourse-style infinite scroll isn't covered?

## Known problems in the current page-link detection (found in review)
- The selectors `.pages a` and `.pagination a` are broad and a numeric label is enough, so unrelated links can be picked up (the same-origin
  and pagination-looking checks above would drop them).
- A lower-case `.pagenav` is not in the list (only `.pageNav` and `.PageNav`). Some vBulletin versions use it, as far as I remember; I haven't
  seen a real vBulletin page, so treat that as a guess to check.
