# Continuation prompt

Paste everything below into a new Claude Code session on this repository.

---

Continue building the **Media Optimizer** Jellyfin plugin in this repo. It is at v1.5.0.0 and
working; your job is to add features and improve it, not to rewrite it.

## What it is

A Jellyfin 10.11 server plugin (C#, net9.0) that inspects any media file from inside Jellyfin and
converts it with FFmpeg — by hand, in bulk, or by saved rules that run on a schedule. Roughly
13,000 lines of C#, ~1,250 lines of injected client JavaScript, two dashboard pages, 298 tests.

Read `README.md` and `docs/implementation-plan.html` first — the plan explains *why* the
architecture is the way it is, including what Jellyfin genuinely does not allow.

## Set up the environment first (nothing is preinstalled)

```bash
# .NET 9 SDK — required, the project targets net9.0
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh && chmod +x /tmp/di.sh
/tmp/di.sh --channel 9.0 --install-dir /opt/dotnet --no-path
export DOTNET_ROOT=/opt/dotnet PATH=$PATH:/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Real ffmpeg — the integration tests drive it; they skip (not fail) without it, and the lossless
# and sampled-estimate guarantees are only actually proven when it is present
curl -sSL -o /tmp/ff.tar.xz https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
cd /tmp && tar -xJf ff.tar.xz && cp ffmpeg-*-static/ffmpeg ffmpeg-*-static/ffprobe /usr/local/bin/

# Browser tests use the preinstalled Chromium; do NOT run `playwright install`
cd web-tests && npm install
```

## Verify like this, every time

```bash
dotnet build -c Release          # must be clean, no warnings
dotnet test                      # 298 tests, all must pass, none skipped when ffmpeg is present
cd web-tests && npm test         # DOM, dialog lifecycle, hostile CSS, dialog render, dashboard render
./tools/package.sh 1.5.x.0 claude/jellyfin-media-optimizer-92xiw8
```

`tools/package.sh` reads the version and changelog out of `build.yaml`. **Never hand-edit
`manifest.json`** — Jellyfin verifies the MD5 on download, so the zip and manifest must be
generated in one step or installs fail with a checksum error. Pass the **default branch** as the
second argument: that is the URL existing installs watch, not whatever branch you are working on.

## Standards this project is held to

- **Verify, don't assert.** Claims about behaviour must be backed by a test that actually runs it:
  real ffmpeg for encoding, a real browser for layout. The sampled estimate is tested by sampling a
  file and then encoding the whole thing and comparing.
- **A regression test must fail against the unfixed code.** Check it, do not assume it.
- **Be honest in the UI.** Never invent numbers. Modelled values say they are modelled, measured
  ones say what they measured and how far apart the samples were, and unknowns say so.
- **Fail closed and say why.** Every part is independent; a failure reports itself on the dashboard
  status panel with what to do about it, and never takes anything else down.
- **Never lose a file.** The original is untouched until the output passes verification, then it is
  renamed (not copied) and kept for a retention period.
- Comments explain *why*, not what. Match the existing style.

## Known constraints — do not rediscover these the hard way

- **jellyfin-web has no plugin API.** The 3-dot menu entry and player button are grafted onto
  private DOM. The dialog renders in a **shadow root** because jellyfin-web's stylesheet reaches
  arbitrary descendants and previously collapsed the layout. Do not move it out of the shadow root.
  `web-tests/hostile.mjs` proves the isolation.
- **File Transformation 2.5+ requires admin auth on its HTTP endpoint.** Registration goes through
  its in-process DI service by reflection instead (`Web/FileTransformationRegistrar.cs`). Do not
  add an HTTP callback endpoint back: the one that used to exist was anonymous and reflected HTML,
  which is a cross-site scripting sink. `ApiSurfaceTests` now fails the build if one reappears.
- **FFmpeg keeps only the last occurrence of an option.** Never emit one twice —
  `ArgumentVectorTests` walks the whole vector looking for duplicates.
- **Some containers tag the video stream with the file's overall bitrate.** Estimates are anchored
  on the source bitrate and sanity-clamped; without that every preset predicts a bigger file.
- **Moving a file across volumes copies every byte.** Originals are renamed in place with a
  `.mooriginal` suffix and encoding happens in the media folder, so both moves are renames.
- **ABI is pinned per Jellyfin minor version** (`targetAbi 10.11.0.0`). GPLv3 by convention.

## The one real gap

**This has never been run inside a live Jellyfin server.** Compilation against the real 10.11
packages, embedded resources, the served JS bundle and all rendering are verified here — but plugin
loading, DI resolution, controller routing, the File Transformation handshake, and the activity-feed
write are only verified structurally. Say so plainly rather than implying it is proven. If the user
reports what the dashboard status panel says, use that as the real signal.

## Backlog, roughly in order of value

1. **Per-library defaults** — different presets for Movies vs TV vs Home Videos. The rules engine
   covers much of this already; what is missing is scoping a rule to one library, which needs the
   virtual-folder lookup in `LibraryCandidateSource`.
2. **Concurrency by resource** — allow two 1080p jobs but only one 4K. The queue already has a
   concurrency cap; this needs a cost per job, which means recording the source height on the job.
3. **Rule ordering in the UI** — rules apply in list order and there is no way to reorder them.
4. **HDR10+ / Dolby Vision** via `dovi_tool` when present. Currently DV blocks a re-encode by
   design; making it work needs an external binary and a raw x265 pipeline.
5. **Per-title x265 tuning** and grain synthesis for film sources.
6. **A second opinion on quality** — VMAF or SSIM on the sampled segments, so "how much worse is
   it?" gets a number rather than a preset name.

Pick what is most valuable, say what you chose and why, build it properly with tests, bump the
version in `build.yaml` and the csproj, repackage, push, and tell me what you could not verify.
