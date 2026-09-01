# Continuation prompt

Paste everything below into a new Claude Code session on this repository.

---

Continue building the **Media Optimizer** Jellyfin plugin in this repo. It is at v1.4.0.0 and
working; your job is to add features and improve it, not to rewrite it.

## What it is

A Jellyfin 10.11 server plugin (C#, net9.0) that inspects any media file from inside Jellyfin and
converts it with FFmpeg. ~10,600 lines of C#, ~1,100 lines of injected client JavaScript, two
dashboard pages. Work on branch `claude/jellyfin-media-optimizer-92xiw8` and push there.

Read `README.md` and `docs/implementation-plan.html` first — the plan explains *why* the
architecture is the way it is, including what Jellyfin genuinely does not allow.

## Set up the environment first (nothing is preinstalled)

```bash
# .NET 9 SDK — required, the project targets net9.0
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh && chmod +x /tmp/di.sh
/tmp/di.sh --channel 9.0 --install-dir /opt/dotnet --no-path
export DOTNET_ROOT=/opt/dotnet PATH=$PATH:/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Real ffmpeg — 7 integration tests drive it; they skip (not fail) without it
curl -sSL -o /tmp/ff.tar.xz https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
cd /tmp && tar -xJf ff.tar.xz && mv ffmpeg-*-static/ffmpeg ffmpeg-*-static/ffprobe /usr/local/bin/

# Browser tests use the preinstalled Chromium; do NOT run `playwright install`
cd web-tests && npm install
# executablePath is /opt/pw-browsers/chromium-1194/chrome-linux/chrome (check the actual version dir)
```

## Verify like this, every time

```bash
dotnet build -c Release          # must be clean
dotnet test                      # 124 tests, all must pass
cd web-tests && npm test         # DOM + hostile-CSS + dialog render + dashboard render
./tools/package.sh               # rebuilds zip AND manifest.json together
```

`tools/package.sh` reads the version and changelog out of `build.yaml`. **Never hand-edit
`manifest.json`** — Jellyfin verifies the MD5 on download, so the zip and manifest must be
generated in one step or installs fail with a checksum error.

## Standards this project is held to

- **Verify, don't assert.** Claims about behaviour must be backed by a test that actually runs it:
  real ffmpeg for encoding, a real browser for layout. Several bugs here were only found by
  rendering the UI in Chromium and measuring it.
- **Be honest in the UI.** Never invent numbers. An earlier build fabricated "11h 40m to encode"
  from a hard-coded table; it now only shows a time once the server's real throughput has been
  measured. Same rule everywhere: derived values are marked, unknowns say so.
- **Fail closed and say why.** Every part is independent; a failure reports itself on the
  dashboard status panel with what to do about it, and never takes anything else down.
- **Never lose a file.** The original is untouched until the output passes verification, then it
  is renamed (not copied) and kept for a retention period.
- Comments explain *why*, not what. Match the existing style.

## Known constraints — do not rediscover these the hard way

- **jellyfin-web has no plugin API.** The 3-dot menu entry and player button are grafted onto
  private DOM. The dialog renders in a **shadow root** because jellyfin-web's stylesheet reaches
  arbitrary descendants and previously collapsed the layout. Do not move it out of the shadow root.
  `web-tests/hostile.mjs` proves the isolation.
- **File Transformation 2.5+ requires admin auth on its HTTP endpoint.** Registration goes through
  its in-process DI service by reflection instead (`Web/FileTransformationRegistrar.cs`). Do not
  revert to the HTTP call — it 401s silently and the UI never appears.
- **Some containers tag the video stream with the file's overall bitrate.** Estimates are anchored
  on the source bitrate and sanity-clamped; without that every preset predicts a bigger file.
- **Moving a file across volumes copies every byte.** Originals are renamed in place with a
  `.mooriginal` suffix and encoding happens in the media folder, so both moves are renames. This
  was the single biggest speed win. Do not route large files through a data folder.
- **ABI is pinned per Jellyfin minor version** (`targetAbi 10.11.0.0`). GPLv3 by convention.
- **Never claim a plugin runs correctly in Jellyfin from a passing build.** See below.

## The one real gap

**This has never been run inside a live Jellyfin server.** Compilation against the real 10.11
packages, embedded resources, the served JS bundle and all rendering are verified here — but
plugin loading, DI resolution, controller routing and the File Transformation handshake are only
verified structurally. Say so plainly rather than implying it is proven. If the user reports what
the dashboard status panel says, use that as the real signal.

## Backlog, roughly in order of value

1. **Automatic rules** — sweep the library on a schedule and convert anything matching saved
   filters (e.g. "everything over 4 GB and above 1080p, watched, to 1080p HEVC"). The filters,
   the batch runner and the scheduled task already exist; this is mostly wiring plus a rules UI.
2. **Per-library defaults** — different presets for Movies vs TV vs Home Videos.
3. **Notifications** on completion or failure, via Jellyfin's activity log and webhook plugin.
4. **A real dry-run** — sample-encode three short segments and report the measured size rather
   than the modelled one. `MeasureBitrateAsync` and the throughput tracking are already there.
5. **Health report** — scan the library and rank what is worth converting by predicted saving,
   so the user has a worklist instead of hunting.
6. **HDR10+ / Dolby Vision** via `dovi_tool` when present. Currently DV blocks a re-encode by
   design; making it work needs an external binary and a raw x265 pipeline.
7. **Per-title x265 tuning** and grain synthesis for film sources.
8. **Concurrency by resource** — allow two 1080p jobs but only one 4K.

Pick what is most valuable, say what you chose and why, build it properly with tests, bump the
version in `build.yaml` and the csproj, repackage, push, and tell me what you could not verify.
