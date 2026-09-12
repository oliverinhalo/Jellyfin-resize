# Media Optimizer for Jellyfin

Inspect any file in your library from inside Jellyfin and convert it with FFmpeg — resolution,
codec, bit depth, bitrate, audio tracks — one film at a time, hundreds at once, or by a rule that
runs itself overnight.

Nothing is deleted until the new file has been checked, and every number the interface shows says
what kind of number it is: measured, estimated, or a guess it cannot do better than.

**Jellyfin 10.11.x** · .NET 9 · GPL-3.0 · uses the FFmpeg already bundled with your server

---

## Install

### 1. Add the repository

**Dashboard → Plugins → Repositories → `+`**

| Field | Value |
|---|---|
| Repository Name | `Media Optimizer` |
| Repository URL | `https://raw.githubusercontent.com/oliverinhalo/Jellyfin-resize/claude/jellyfin-media-optimizer-92xiw8/manifest.json` |

If Jellyfin rejects it, the URL is wrong: it must end in `manifest.json` and be the **raw** link,
not the GitHub page you see when browsing the repo.

### 2. Install it

Go to **Dashboard → Plugins** and click the **Available** chip at the top.

> **This is the step everyone gets stuck on.** Jellyfin 10.11's plugins page opens on the
> **Installed** filter, so a freshly added repository looks like it did nothing. Nothing shows up
> until you switch to **Available** or **All**. The separate "Catalog" page was removed after
> 10.10.

Find **Media Optimizer** under *General* and install it.

### 3. Restart Jellyfin

Not optional — the plugin will not load until you do. Afterwards, **Dashboard → Plugins → My
Plugins** should show it as *Active*.

### 4. Optional: the in-app buttons

The 3-dot menu entry and the player button need a second plugin to get their script into the web
client. Repeat step 1 with:

| Field | Value |
|---|---|
| Repository Name | `IAmParadox` |
| Repository URL | `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` |

Install **File Transformation** the same way and restart again. No configuration needed.

**Skip this and you lose only the buttons.** Everything else works from **Dashboard → Media
Optimizer**, which has its own library search and opens the identical conversion dialog.

### Requirements

- **Jellyfin 10.11.0 or newer.** The plugin ABI is pinned per Jellyfin minor version, so it will
  not appear at all on 10.10.x.
- FFmpeg — already part of Jellyfin. The plugin uses the server's own binary and never ships one.

---

## Where everything is

| Where | What |
|---|---|
| **Dashboard → Media Optimizer** | Status, library search, bulk selection, automatic rules, the queue and its history |
| **Dashboard → Plugins → Media Optimizer** | Settings: languages, speed, output policy, safety |
| **Dashboard → Scheduled Tasks** | *Media Optimizer: automatic rules* nightly, and *housekeeping* |
| **In the web client** | "Optimize file…" in any 3-dot menu, and a tune icon in the player |

---

## What it does

The dialog has two halves. On the left, what the file actually is: container, size, duration and
bitrate; video codec, profile, resolution, bit depth, frame rate (flagged when variable) and
dynamic range; every audio track with its channel layout, language, and whether it is lossless or
carries Atmos/DTS:X objects; the subtitle and attachment inventory.

On the right, what you want instead — and a live estimate underneath showing the predicted size,
the saving, and how confident that prediction is.

### Presets

| Preset | What it changes |
|---|---|
| **Standard** | Keeps the resolution, re-encodes to a more efficient codec. Best on older H.264 or MPEG files. |
| **Medium reduction** | One step down the resolution ladder (4K → 1440p, 1080p → 720p) plus leaner audio. |
| **High reduction** | A step down *and* a lower bitrate. Smallest file; the quality loss is visible. |
| **Lossless only** | Bit-exact changes only. Verified by hash afterwards. |
| **Custom** | Everything by hand. |

The dialog opens on whichever preset actually helps *that* file. A file already encoded in HEVC at
a sensible bitrate cannot be shrunk by re-encoding it to HEVC again, so it steers you to a
resolution change and says why rather than offering a preset that would save nothing.

### In bulk

The dashboard opens on **most to gain first**, which is not the same as largest first: the biggest
file in most libraries is a remux that is already efficiently encoded and has nothing to give up.
Each row says roughly what a conversion would reclaim and where it would come from — "≈ 12 GiB to
gain · HEVC instead of H.264" — worked out from size, resolution and codec without reading the
files, so a whole library can be ranked in one page load. It is marked as an approximation because
it is one; the dialog's estimate reads the file's real stream bitrates.

Search or filter your library — by size, resolution, bitrate, watched state, container or codec —
tick the files you want, and apply one preset to all of them. Each file is still analysed
individually, so the preset adapts to what it actually is, and anything unconvertible is listed as
skipped with the reason.

### On a schedule

A rule converts matching files by itself, once a night, so a library keeps itself in order without
anyone picking files by hand. A rule says what it takes — films or episodes, one library or all of
them, a minimum resolution or size, a container, a codec, whether anyone has watched it, how long
it has been in the library —
and what to do with it, and the rest is the ordinary conversion path: jobs in the same queue, one
at a time, paused while anyone is streaming, and no original touched until the result verifies.

The defaults are deliberately timid, because the failure mode of an automatic rule is not "it did
nothing":

- **A ceiling per run.** Three files a night by default. A rule cannot queue the library.
- **A minimum saving.** 15% by default; below that it leaves the file alone. Spending four hours
  of CPU and a generation of quality to reclaim 3% is not optimising anything.
- **A grace period.** 30 days by default, so nothing is replaced the evening it arrives — before
  anyone has watched it once, or noticed that the download was bad.
- **Never twice.** A file this plugin has already converted is never taken again.
- **Nothing a person would have been asked about.** Anything the dialog would block is skipped with
  the same reason. Dolby Vision is the clearest case: accepting the loss of it is a decision for a
  person, not for a rule running at four in the morning.
- **Off until you say otherwise.** A new rule is saved switched off, and **Preview** shows exactly
  what it would take — and why it passed over the rest — without queueing anything.

Rules are applied top to bottom and the first one to take a file keeps it, so with two rules that
overlap, the one above wins — "keep the 4K films as they are, shrink everything else" is only that
sentence if the keeping rule is first. **Move up** and **Move down** on the dashboard decide it, and
every job a rule queues says which rule queued it.

Rules live on the dashboard page, and run as the scheduled task *Media Optimizer: automatic rules*,
so you can move them, run them by hand, or switch them off from Jellyfin's own scheduled task page.

### Measuring instead of predicting

Every number the dialog shows before a conversion is modelled — anchored on the file's own bitrate
rather than a generic table, which is why it does not claim a lean HEVC file will shrink, but still
a prediction. **Measure it** in the dialog encodes three eight-second stretches of the real file
with the real settings and reports what they produced: a measured size, a measured range, and a
time estimate taken from how fast those samples actually ran on this machine.

It also answers the question nobody could answer before: **how much worse will it look?** Each
sample is compared with the source frame by frame — VMAF where your FFmpeg has it, SSIM otherwise —
and the result is reported as a score *and* in words: "VMAF 96.4 — indistinguishable from the
source", or "VMAF 81.2 — noticeably softer on detailed scenes". A conversion that keeps the video
stream untouched is not compared at all; there is nothing to compare.

It costs about a minute, which is why it is a button rather than something that happens as you
type. It runs on the server rather than inside the request that asked for it — a minute is longer
than most reverse proxies will hold a connection open — so the dialog asks how it is going, and
closing the dialog stops it rather than leaving the server encoding for nobody. The spread between the samples is shown rather than averaged away, because three samples
cannot know about the twenty minutes of dark, grainy footage at the end of the film, and the
quality score is reported with the name of the metric that produced it, because SSIM 0.98 and
VMAF 98 are not the same claim.

### Telling it what it is looking at

Grain and animation want opposite decisions from an encoder, and it is the one thing about a file
a person can see instantly that no probe can tell reliably. **Content** in the dialog — *live
action*, *animation*, *film grain* — is passed straight through to the encoder's own tuning: line
art stops being smoothed, and grain is kept rather than smeared into blotches, at the cost of a
bigger file.

It is only offered for the x264 and x265 software encoders, because they are the only ones with a
setting that means this. The hardware encoders use the same flag for something else entirely, so
nothing is sent there, and if a preset was chosen for an encoder that cannot take it the plan says
so rather than changing nothing silently. A rule can carry it too, which is where it fits best: a
rule already narrows a library down, so "the anime library, tuned for animation" is true of every
file that rule takes.

### Letting it choose the setting

Once the difference can be measured, the question can be turned round. **Find the setting…** asks
how close to the source the result has to look — *indistinguishable*, *very hard to tell apart*, or
*slightly softer* — and then finds the smallest file that still meets it, on this file, by
encoding short stretches at different settings and comparing each with the source.

This is the one thing no preset can do. Every "use CRF 22" is a number that suited somebody else's
files; a grainy 1970s film and a flat animated series want settings four or five apart, and no
table knows which one it is looking at. The search covers about twenty settings in five short
encodes by halving the range, then confirms its answer at three points across the film — and it is
the *worst* of those three that has to meet the target, because an average is exactly how one bad
dark scene hides. The setting it finds is applied to the form, with the score, the verdict in
words, and how many seconds of the film it was confirmed on.

It takes a few minutes of real encoding, and it says so before it starts. If the source cannot
reach the target at any setting — already heavily compressed, or damaged — it says that instead of
quietly returning the best of a bad set.

---

## MP4 or MKV

MP4 is the default because it plays on everything, but it cannot store some of what a Blu-ray rip
contains. When a file needs more than MP4 offers, the plugin writes MKV instead and says why, in
the dialog, rather than dropping tracks quietly. That happens when the file has:

- **image-based subtitles** (PGS, DVD, DVB) — MP4 has no bitmap subtitle format at all;
- **embedded subtitle fonts** — attachments are Matroska-only;
- **audio MP4 cannot carry** — TrueHD, MLP or Blu-ray PCM. The track is kept exactly as it is
  rather than being re-encoded to fit.

Text subtitles are the one case that converts cleanly: SubRip and ASS become MP4's own subtitle
format. The words survive; styling and positioning do not. Choose MKV to keep them exactly.

You can always override the container yourself, and the plugin will tell you what that costs.

---

## Trimming tracks you never use

A Blu-ray rip often carries five audio languages and a commentary track. If you only ever watch in
English, the rest is pure waste — and removing it is **bit-exact for everything you keep**, which
usually makes it the single largest saving available.

Set **Audio languages** to `eng` in the settings, or per conversion, or across a whole batch.
`eng`, `en`, `en-GB` and `English` all match, because containers spell it inconsistently. Subtitles
have their own list, and commentary tracks can be dropped separately.

Two safeguards: tracks with no language tag are kept by default (on a single-language release the
untagged track is usually the only one), and **a file is never left with no audio at all** — if
nothing matches your list, the default track survives.

---

## What happens to the original

Replacing a file leaves the old one **in the same folder**, renamed with an extension Jellyfin
ignores so it never appears as a second copy. That makes the swap an instant rename instead of a
whole-file copy, and putting it back equally instant.

| Choice | Effect |
|---|---|
| **Replace, keep the old file** *(default)* | Kept for 7 days, then deleted automatically. Undo any time before that. |
| **Replace and delete now** | Frees the space immediately. No undo. |
| **Keep the original** | Writes a new file alongside, or adds it as another version. |

Every replacement is appended to `replacements.log` in the plugin data folder, so there is a
readable trail independent of the plugin's own history, and each finished or failed conversion
writes a line to **Dashboard → Activity** — what the file went from and to, how much was freed, and
until when the original can be put back. Both can be switched off in the settings.

---

## Making it faster

Software encoding is genuinely slow — x265 at 4K is a few frames per second, and that is the job,
not a bug. But most of the gap between this plugin and running FFmpeg by hand was never the
encoder. It was these, all now fixed:

| What was costing time | How much | Now |
|---|---|---|
| Full decode verification after every replace | roughly doubled every job | Off by default; the cheap checks still run |
| Encoding into a folder on another disk | a full copy of the finished file | Encodes in the media folder — the final move is a rename |
| Moving the original into a data folder | a second full copy | Renamed in place, instantly |
| Below-normal process priority | slower whenever anything else runs | Off by default |

On a 5 GB film those copies alone were minutes of pure disk shuffling per job.

**The levers that remain, in order of effect:**

1. **Encode on the graphics card** — ten to twenty times faster. See below.
2. **Reduce the resolution first** — 4K to 1440p is roughly a quarter of the pixels, so roughly a
   quarter of the time, and a far bigger saving than any amount of CRF tuning.
3. **Set Speed to "Fastest"** — typically three to five times sooner for a file about 10% larger.
4. **Let it run overnight** — the queue pauses while anyone is streaming and runs one job at a time.

### Can it use the GPU and still make a small file?

Mostly, yes. That reputation comes from GPUs being run on their defaults. This plugin drives them
in their highest-quality mode instead: multi-pass rate control, a 32-frame lookahead, B-frames with
middle reference, and spatial and temporal adaptive quantisation.

That lands **10–20% larger** than a slow CPU encode rather than the ~50% a GPU on its defaults
costs, while still being many times faster. For a library-wide reduction that is almost always the
right trade; keep the CPU for the files you care most about.

### Would another program be faster?

- **Tdarr** or **Unmanic** spread encoding across several machines. With a spare PC, that beats
  anything a single-server plugin can do.
- **SVT-AV1** is often faster than x265 at comparable quality, and is chosen automatically for High
  reduction when your FFmpeg has it and AV1 encoding is enabled.
- Nothing makes software 4K encoding quick. If jobs must finish in minutes, hardware is the only
  real answer.

---

## Lossless mode, precisely

Genuinely bit-exact operations, and roughly what each saves:

| Technique | Typical saving | Notes |
|---|---|---|
| Drop unwanted audio/subtitle tracks | 10–50% | The largest real saving on most remuxes |
| Lossless audio → FLAC | 10–35%, 50%+ from PCM | TrueHD, DTS-HD MA and LPCM decode bit-exactly |
| Strip filler NAL units | 0%, or 10–20% | Only on sources padded to a constant bitrate |
| Remux container | under 1% | Not a size strategy; useful for compatibility |
| Lossless video re-encode | 40–70% | **Only** from a lossless source (FFV1, HuffYUV, raw) |

After a lossless job the plugin decodes every retained track from both files and compares MD5
hashes. A mismatch fails the job and leaves the original untouched — the claim is checked, not
asserted.

"Visually lossless" (CRF 16–18) is presented separately and never labelled lossless.

---

## Safety

The original is not touched until a verified replacement exists on disk.

- **Preflight** — free space, a file-stability window so an in-progress download is never grabbed,
  no active playback on the item, a writable target, and exclusion of ISO, BDMV, `.strm` and
  multi-part items.
- **Verification** — the output parses, its duration matches within ±0.5% or one second, every
  expected stream is present, and for lossless jobs the hashes match. An optional full decode scan
  catches deeper corruption at the cost of roughly doubling the job.
- **The queue survives a crash.** Every change is written to disk before it is acknowledged, so a
  power cut loses nothing. An interrupted encode is picked up again automatically; one interrupted
  while moving files is held for review instead, because there the original may already have moved.
- **Library reconciliation** — on a container change the existing item is repointed rather than
  rescanned, so watched state, resume positions, favourites, playlists and collections survive.
  Companion files (`.nfo`, artwork, external subtitles) are renamed alongside, and stale scrub
  previews are rebuilt.

Every endpoint that starts, cancels or reverses a conversion requires administrator rights, and so
does everything that would reveal where files live on the server, list the queue, or make the
server read a whole media file. Exactly one endpoint answers without a signed-in user: the client
script itself, because the `<script>` tag the browser adds carries no credentials. A test asserts
that surface by reflection, so an endpoint cannot lose its authorisation in a refactor without the
build failing.

---

## Honest limitations

These are properties of Jellyfin and of video compression, not bugs.

- **The in-app buttons only exist in browser-based clients.** Jellyfin has no plugin API for its
  web client, so they are grafted onto private DOM. They appear in browsers, Jellyfin Media Player
  and the Android app's web views — not on Android TV, Roku, Kodi, tvOS or Swiftfin, and no server
  plugin can put them there. The dashboard page does everything they do.
- **Those hooks will break eventually.** Between jellyfin-web `v10.11.0` and current `master`,
  `actionSheet.js` became `.ts` and the video OSD moved directories — both files this depends on.
  Every hook fails closed and logs once; expect a plugin update after a Jellyfin release.
- **Dolby Vision cannot survive a re-encode.** FFmpeg parses the RPU but cannot re-inject it.
  Re-encoding DV video is blocked by default; you can explicitly accept conversion to HDR10.
- **HDR10+ dynamic metadata is lost** on re-encode. The output keeps static HDR10.
- **You cannot losslessly shrink already-lossy video.** Re-encoding H.264 or HEVC at `-qp 0` stores
  the *decoded pixels*, which carry far more entropy than the bitstream they came from — the result
  is typically 3–20× larger. The plugin refuses this rather than letting you discover it.
- **Encoding competes with playback.** Jellyfin gives plugins no resource governor. The queue runs
  one job at a time by default and pauses while anyone is streaming. Raising the limit counts in
  ordinary jobs rather than in job slots — a 4K encode counts as two, because two of them at once
  is not twice the work, it is a server that stops answering.
- **A rule's predicted saving is a model, not a measurement.** It is anchored on the file's own
  bitrate rather than a generic table, which is why it does not claim that re-encoding a lean HEVC
  file will shrink it — but it is still a prediction, and that is why a rule's minimum-saving floor
  exists and why Preview is worth running before switching one on.
- **It has not been verified inside a live Jellyfin server.** Everything here is built and tested
  against the real 10.11 packages, but plugin loading and the File Transformation handshake are
  verified structurally, not observed running. The status panel is what tells you the truth on
  your server.

---

## Troubleshooting

**Nothing appeared after adding the repository.**

1. Click the **Available** chip — the page defaults to *Installed*. This is nearly always it.
2. Hard-refresh (Ctrl+Shift+R), then restart Jellyfin. Manifests are cached.
3. Check the *server* can reach the URL. It is fetched by Jellyfin, not your browser, so pasting it
   into your own browser proves nothing. Look in **Dashboard → Logs** for the repository host. A
   server without outbound HTTPS gets nothing and reports nothing in the UI.
4. Check your version. Jellyfin only offers a plugin whose `targetAbi` is at or below the server
   version; this one needs 10.11.0+.

**The in-app buttons don't appear.** Install File Transformation (step 4), restart, then
hard-refresh. The status panel says explicitly whether the injection registered.

**A conversion failed straight away.** Before encoding, every job runs half a second of the real
thing through the real muxer, so an impossible combination fails in a second instead of an hour.
The message names what FFmpeg objected to. Switching the container to MKV resolves nearly all of
them, because it can store formats MP4 cannot — see [MP4 or MKV](#mp4-or-mkv).

**Is anything working at all?** **Dashboard → Media Optimizer** opens with a status line that
expands into a per-part check: the plugin, FFmpeg and its encoders, encoding speed measured on your
hardware, the queue, the in-app injection, and your permissions. Each failure reports itself with
what to do, and never hides the others. If that page renders, the plugin is loaded and routed.

---

## Updating and uninstalling

Jellyfin checks the repository automatically; when a new version appears, **Dashboard → Plugins**
offers it. Restart afterwards.

To uninstall: **Dashboard → Plugins → Media Optimizer → Uninstall**, then restart. Converted files
are left exactly as they are. If you used *Replace* and want an original back, **restore it before
uninstalling** — the kept files remain on disk, but the UI that knows which is which will be gone.

---

## Building it yourself

```bash
dotnet publish -c Release
```

Copy `Jellyfin.Plugin.MediaOptimizer/bin/Release/net9.0/Jellyfin.Plugin.MediaOptimizer.dll` into a
`plugins/MediaOptimizer` folder inside your Jellyfin **data** directory (not the install
directory), then restart.

## How it is reviewed

Each release carries a written self-review: what was checked, what was found, and what was not
verified. [`docs/self-review-1.5.0.md`](docs/self-review-1.5.0.md) is the current one — four passes
over the code, including the eight defects the review found in this same release's own new code and
the three a security pass found in code older than it.

---

## Development

```bash
dotnet build -c Release        # requires the .NET 9 SDK
dotnet test                    # 124 tests
cd web-tests && npm test       # browser-based UI tests
./tools/package.sh             # rebuilds the zip and manifest.json together
```

`tools/package.sh` takes the version and changelog from `build.yaml`. Never hand-edit
`manifest.json` — Jellyfin verifies the MD5 on download, so the two must be generated in one step.

**The tests exercise real things.** Seven drive an actual FFmpeg binary on a generated clip and
assert on the output: that a lossless job hashes identically, that a lossy job *claiming* to be
lossless is rejected, that a truncated output is caught, that a downscale produces the requested
resolution, that an upscale is refused, and that cancelling kills FFmpeg leaving the source
untouched. The browser tests render the dialog and dashboard in Chromium and measure the layout,
including under deliberately hostile page CSS — which is how the shadow-root isolation is verified
rather than assumed.

See [`docs/implementation-plan.html`](docs/implementation-plan.html) for the design rationale and
the full risk register, and [`docs/NEXT-SESSION-PROMPT.md`](docs/NEXT-SESSION-PROMPT.md) to pick
the work up in a fresh session.

## Licence

GPL-3.0, matching the official Jellyfin plugin template.
