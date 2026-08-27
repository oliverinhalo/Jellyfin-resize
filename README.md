# Media Optimizer for Jellyfin

Inspect any media file from inside Jellyfin and convert it with FFmpeg — resolution, codec, bit
depth, bitrate, audio — plus a genuinely lossless mode whose bit-exactness is verified by hash
after every encode.

Built for **Jellyfin 10.11.x**. Licensed GPL-3.0, matching the official Jellyfin plugin template.

## Install

Add this as a plugin repository in Jellyfin (**Dashboard → Plugins → Repositories → `+`**):

```
https://raw.githubusercontent.com/oliverinhalo/Jellyfin-resize/claude/jellyfin-media-optimizer-92xiw8/manifest.json
```

Then go to **Dashboard → Plugins**, click the **Available** filter chip, install
**Media Optimizer**, and restart Jellyfin. Full step-by-step, including the extra plugin needed
for the in-app buttons, is [below](#installing).

---

## What it does

Open the 3-dot menu on any movie or episode, or press the tune icon in the player, and you get a
dialog with two halves.

**Left — what the file is now:** container, size, duration, overall bitrate; video codec, profile,
resolution, bit depth, frame rate (with a VFR flag), dynamic range and bitrate; every audio track
with codec, channel layout, language and whether it is lossless or carries Atmos/DTS:X objects;
subtitle and attachment inventory; and whether the folder is writable.

**Right — what you want instead:** strategy presets, resolution (720p / 1080p / 1440p / 4K /
original / custom), video codec, bit depth, CRF or bitrate or target size, encoder preset,
optional hardware encoding, per-track audio decisions, and where the result goes.

A live estimate at the foot shows predicted size, the saving, a range, and the encode time.

## Honest limitations

These are properties of Jellyfin and of video compression, not bugs.

- **The in-app dialog only exists in browser-based clients.** Jellyfin has no plugin API for its
  web client, so the menu entry and player button are grafted onto private DOM. They appear in
  browsers, in Jellyfin Media Player, and in the Android app's web views. They do **not** appear on
  Android TV, Roku, Kodi, tvOS or Swiftfin, and no server plugin can put them there. Everything is
  also available from **Dashboard → Media Optimizer**, which works everywhere.
- **The DOM hooks will break.** Between jellyfin-web `v10.11.0` and current `master`,
  `actionSheet.js` became `.ts` and the video OSD moved directories. Both are files this depends
  on. Every hook fails closed and logs once; expect to need a plugin update after a Jellyfin
  release.
- **Dolby Vision cannot survive a re-encode.** FFmpeg parses the DV RPU but cannot re-inject it.
  Re-encoding DV video is blocked by default; you can explicitly accept conversion to HDR10.
- **You cannot losslessly shrink already-lossy video.** Re-encoding H.264/HEVC with `-qp 0` stores
  the *decoded pixels*, which carry far more entropy than the bitstream they came from — the result
  is typically 3–20× larger. The plugin refuses this rather than letting you discover it.
- **HDR10+ dynamic metadata is lost** on re-encode; the output keeps static HDR10.
- **Encoding competes with playback.** Jellyfin exposes no resource governor to plugins. The queue
  defaults to one job, below-normal priority, and pauses while anyone is streaming.

## Lossless mode, precisely

Genuinely bit-exact operations, and roughly what each saves:

| Technique | Typical saving | Notes |
|---|---|---|
| Drop unwanted audio/subtitle tracks | 10–50% | The largest real saving on most remuxes |
| Lossless audio → FLAC | 10–35% (50%+ from PCM) | TrueHD, DTS-HD MA and LPCM decode bit-exactly |
| Strip filler NAL units | 0%, or 10–20% | Only on sources padded to constant bitrate |
| Remux container | <1% | Not a size strategy; useful for compatibility |
| Lossless video re-encode | 40–70% | **Only** from a lossless source (FFV1, HuffYUV, raw) |

After a lossless job the plugin decodes each retained track from both files and compares MD5
hashes. A mismatch fails the job and leaves the original untouched. The result shows a
verification badge — the claim is checked, not asserted.

"Visually lossless" (CRF 16–18) is presented separately and never labelled lossless.

## Safety

The original file is not touched until a verified replacement exists on disk.

- **Preflight:** free space, file-stability window (never grabs an active download), no active
  playback on the item, writable target, and exclusion of ISO/BDMV/`.strm`/multi-part items.
- **Verification gate:** output parses, duration within ±0.5% or 1s, optional full decode scan,
  and hash comparison for lossless jobs.
- **Output policies:** *Sidecar* (default, original never modified), *Alternate version*, and
  *Replace* — which moves the original to quarantine with a retention period and a working
  **Restore original** button, not a delete.
- **Library reconciliation:** on a container change the existing item is repointed via
  `UpdateItemAsync`. Watched state, resume positions, favourites, playlists and collections are
  keyed on the item id, so they survive. Companion files (`.nfo`, artwork, external subtitles) are
  renamed alongside.

Every endpoint that starts, cancels or reverts a conversion requires `RequiresElevation`.

## Installing

### 1. Add the plugin repository

In Jellyfin: **Dashboard → Plugins → Repositories → `+`**

| Field | Value |
|---|---|
| Repository Name | `Media Optimizer` |
| Repository URL | `https://raw.githubusercontent.com/oliverinhalo/Jellyfin-resize/claude/jellyfin-media-optimizer-92xiw8/manifest.json` |

Press **Save**. If Jellyfin says the repository is invalid, the URL is wrong — it must end in
`manifest.json` and be the **raw** GitHub URL, not the page you see when browsing the repo.

### 2. Install the plugin

Go to **Dashboard → Plugins** and click the **Available** chip at the top of the page.

> **This is the step everyone gets stuck on.** In Jellyfin 10.11 the plugins page defaults to the
> **Installed** filter, so a newly added repository looks like it did nothing. Nothing appears
> until you switch to **Available** (or **All**). There is no separate "Catalog" page any more —
> that was 10.10 and earlier.

Find **Media Optimizer** (category *General*) and click **Install**.

### 3. Restart Jellyfin

Required. The plugin will not load until you do. After restarting, check
**Dashboard → Plugins → My Plugins** — Media Optimizer should show as *Active*.

### 4. Add File Transformation, for the in-app buttons

The 3-dot menu entry and the player button need a second plugin to get their script into the web
client. Repeat step 1 with:

| Field | Value |
|---|---|
| Repository Name | `IAmParadox` |
| Repository URL | `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` |

Then, again under **Dashboard → Plugins** with the **Available** chip selected, install
**File Transformation** and restart Jellyfin again. No configuration needed.

**This step is optional.** Without it everything still works from
**Dashboard → Media Optimizer** — you just do not get the in-app buttons.

### Requirements

- **Jellyfin 10.11.x.** The plugin will show as *Not Supported* on 10.10 or earlier; the plugin
  ABI is pinned per Jellyfin minor version.
- FFmpeg — already bundled with Jellyfin. The plugin uses the server's own binary and never
  ships its own.

### Where things are afterwards

- **Dashboard → Media Optimizer** — the queue: progress, history, cancel, restore an original.
- **Dashboard → Plugins → Media Optimizer** — settings: output policy, directories, concurrency,
  safety options.
- **In the web client** (with File Transformation installed) — "Optimize file…" in any movie or
  episode's 3-dot menu, and a tune icon in the video player.

### If a plugin does not appear

1. **Click the "Available" chip.** The page defaults to *Installed*. This is nearly always it.
2. **Hard-refresh the browser** (Ctrl+Shift+R), then restart Jellyfin. Manifests are cached.
3. **Check the server can reach the URL.** The manifest is fetched by the *Jellyfin server*, not
   your browser, so pasting the URL into your own browser proves nothing. Look in
   **Dashboard → Logs** for errors mentioning the repository host. A server without outbound
   HTTPS, or behind a proxy, gets nothing and reports nothing in the UI.
4. **Check your Jellyfin version.** Jellyfin only offers a plugin whose `targetAbi` is less than
   or equal to the server version. Media Optimizer targets `10.11.0.0`, so it needs 10.11.0 or
   newer and will not appear at all on 10.10.x.

### Updating

Jellyfin checks the repository automatically. When a new version appears, **Dashboard → Plugins**
offers the update; restart afterwards.

### Uninstalling

**Dashboard → Plugins → Media Optimizer → Uninstall**, then restart. Files it already converted
are left exactly as they are. If you used *Replace* and want an original back, restore it from the
queue page **before** uninstalling — the quarantine folder is tracked in the plugin's own job
history, which uninstalling leaves behind on disk but the UI can no longer read.

### Building it yourself instead

```bash
dotnet publish -c Release
```

Copy `Jellyfin.Plugin.MediaOptimizer/bin/Release/net9.0/Jellyfin.Plugin.MediaOptimizer.dll` into a
`plugins/MediaOptimizer` folder inside your Jellyfin **data** directory (not the install
directory), then restart.

## Development

```bash
dotnet build                 # build
dotnet test                  # 60 tests, including 7 against a real ffmpeg
```

The FFmpeg integration tests generate a short clip, run planner-produced argument vectors through
a real binary, and assert on the actual output — that a lossless job hashes identically, that a
lossy one claiming to be lossless is *rejected*, that a truncated output is caught, that a
downscale produces the requested resolution, and that an upscale is refused. They skip when ffmpeg
is absent.

See [`docs/implementation-plan.html`](docs/implementation-plan.html) for the design rationale and
the full risk register.
