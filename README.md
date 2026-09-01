# Media Optimizer for Jellyfin

Inspect any file in your library from inside Jellyfin and convert it with FFmpeg — resolution,
codec, bit depth, bitrate, audio tracks — one film at a time or hundreds at once.

Nothing is deleted until the new file has been checked, and every claim the interface makes about
size, speed or quality is measured rather than guessed.

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
| **Dashboard → Media Optimizer** | Status, library search, bulk selection, the queue and its history |
| **Dashboard → Plugins → Media Optimizer** | Settings: languages, speed, output policy, safety |
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

Search or filter your library — by size, resolution, bitrate, watched state, container or codec —
tick the files you want, and apply one preset to all of them. Each file is still analysed
individually, so the preset adapts to what it actually is, and anything unconvertible is listed as
skipped with the reason.

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
readable trail independent of the plugin's own history.

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

Every endpoint that starts, cancels or reverses a conversion requires administrator rights.

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
  one job at a time and pauses while anyone is streaming.
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
