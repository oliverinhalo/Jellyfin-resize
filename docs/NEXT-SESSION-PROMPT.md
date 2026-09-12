# Continuation prompt

Paste everything below into a new Claude Code session on this repository.

---

Continue building the **Media Optimizer** Jellyfin plugin in this repo. It is at v1.5.0.0 and
working; your job is to add features and improve it, not to rewrite it.

## What it is

A Jellyfin 10.11 server plugin (C#, net9.0) that inspects any media file from inside Jellyfin and
converts it with FFmpeg — by hand, in bulk, or by saved rules that run on a schedule. It also
measures what it is about to do: the size and the picture quality of a conversion, by encoding
short stretches of the real file, and it can search the quality scale for the smallest file that
still meets a stated target. Roughly 14,600 lines of C# (tests included), ~1,450 lines of injected client
JavaScript, two dashboard pages, **463 tests** and **eight browser suites**.

Read `README.md` first, then `docs/self-review-1.5.0.md` — the review is the honest account of what
is checked, what is not, and the thirty-three defects the last pass through this code found,
including the eleven that were mine. `docs/implementation-plan.html` explains why the architecture
is the way it is, including what Jellyfin genuinely does not allow.

## Set up the environment first (nothing is preinstalled)

```bash
# .NET 9 SDK — required, the project targets net9.0
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh && chmod +x /tmp/di.sh
/tmp/di.sh --channel 9.0 --install-dir /opt/dotnet --no-path
export DOTNET_ROOT=/opt/dotnet PATH=$PATH:/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Real ffmpeg — 22 tests drive it; they skip (not fail) without it, and the lossless, sampled and
# quality guarantees are only actually proven when it is present
curl -sSL -o /tmp/ff.tar.xz https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
cd /tmp && tar -xJf ff.tar.xz && cp ffmpeg-*-static/ffmpeg ffmpeg-*-static/ffprobe /usr/local/bin/

# Browser tests use the preinstalled Chromium; do NOT run `playwright install`
cd web-tests && npm install
```

## Verify like this, every time

```bash
dotnet build -c Release          # must be clean: no errors, no warnings
dotnet test                      # 463 tests, all pass, none skipped when ffmpeg is present
cd web-tests && npm test         # eight suites: DOM, lifecycle, hostile CSS, dialog, dashboard,
                                 # encoder selection, analysis, settings page
./tools/package.sh 1.5.x.0 claude/jellyfin-media-optimizer-92xiw8
```

`tools/package.sh` reads the version and changelog out of `build.yaml`, and
`ReleasePackagingTests` fails the build if the zip, the manifest and the version ever disagree.
**Never hand-edit `manifest.json`** — Jellyfin verifies the MD5 on download, so the zip and manifest must be
generated in one step or installs fail with a checksum error. Pass the **default branch** as the
second argument: that is the URL existing installs watch, not whatever branch you are working on.

## Standards this project is held to

- **Verify, don't assert.** Claims about behaviour must be backed by a test that actually runs it:
  real ffmpeg for encoding, a real browser for layout. The sampled estimate is tested by sampling a
  file and then encoding the whole thing and comparing; the quality search by holding its answer to
  the target it was given.
- **A regression test must fail against the unfixed code.** Check it, do not assume it. Two classes
  of bug here are only visible under repetition — a lost output line, a filter graph that deadlocks
  — so their tests repeat.
- **A check that claims something must do it.** The most valuable pass through this code asked one
  question of every check and every sentence of UI: does it do what it says? It found a
  verification step the settings page described in so many words that did not exist, a method whose
  purpose rested on a misconception, and a guess presented as a measurement. Read for that.
- **Be honest in the UI.** Never invent numbers. Every number says which kind it is: measured,
  estimated with a range, a rough guess, or none at all.
- **Fail closed and say why.** Every part is independent; a failure reports itself on the dashboard
  status panel with what to do about it, and never takes anything else down.
- **Never lose a file.** The original is untouched until the output passes verification, then it is
  renamed (not copied) and kept for a retention period.
- **Every setting is reachable and read.** `ConfigurationSurfaceTests` fails the build for a setting
  with no control, a control the save list forgets, or a setting nothing reads.
- Comments explain *why*, not what. Match the existing style.

## Known constraints — do not rediscover these the hard way

- **jellyfin-web has no plugin API.** The 3-dot menu entry and player button are grafted onto
  private DOM. The dialog renders in a **shadow root** because jellyfin-web's stylesheet reaches
  arbitrary descendants and previously collapsed the layout. Do not move it out of the shadow root.
  `web-tests/hostile.mjs` proves the isolation.
- **File Transformation 2.5+ requires admin auth on its HTTP endpoint.** Registration goes through
  its in-process DI service by reflection instead (`Web/FileTransformationRegistrar.cs`). Do not
  add an HTTP callback endpoint back: the one that used to exist was anonymous and reflected HTML,
  which is a cross-site scripting sink. `ApiSurfaceTests` fails the build if one reappears.
- **FFmpeg keeps only the last occurrence of an option.** Never emit one twice —
  `ArgumentVectorTests` walks the whole vector looking for duplicates, and two separate defects in
  this plugin were caused by ignoring it.
- **Nothing may hold an HTTP request open for minutes.** Sixty seconds is the read timeout in front
  of most Jellyfin servers. Work that takes longer goes through `IOperationRegistry`: the request
  starts it and answers with an id, the browser polls, closing the dialog cancels it, and it has a
  deadline of its own for the tab that simply disappears.
- **Everything this plugin writes into a working directory is named `.mo-…`.** Housekeeping deletes
  only those, because the working directory is a path an administrator types in and may well
  already have something else in it. Keep the convention for any new temporary file.
- **Some containers tag the video stream with the file's overall bitrate.** Estimates are anchored
  on the source bitrate and sanity-clamped; without that every preset predicts a bigger file.
- **Moving a file across volumes copies every byte.** Originals are renamed in place with a
  `.mooriginal` suffix and encoding happens in the media folder, so both moves are renames.
- **The capability probe is cached, but the server's own settings are not.** What ffmpeg can do
  changes when the binary changes; what Jellyfin permits changes when somebody clicks a checkbox.
  Read the second kind fresh (`IServerEncodingContext`).
- **ABI is pinned per Jellyfin minor version** (`targetAbi 10.11.0.0`). GPLv3 by convention.

## The one real gap

**This has never been run inside a live Jellyfin server.** Compilation against the real 10.11
packages, embedded resources, the served JS bundle and all rendering are verified here — but plugin
loading, DI resolution, controller routing, the File Transformation handshake, the scheduled-task
discovery and the activity-feed write are only verified structurally. Say so plainly rather than
implying it is proven. If the user reports what the dashboard status panel says, use that as the
real signal.

## Backlog, roughly in order of value

The obvious things are done: rules (with library scoping, ordering, and a preview that queues
nothing), weighted concurrency, measured size, measured quality, the quality search, content
tuning, activity-feed notifications, and the worklist ranked by what there is to gain.

1. **Dolby Vision via `dovi_tool`** — the last thing the plugin refuses outright. It needs an
   external binary the user installs, a raw x265 pipeline, and RPU extract/inject around the
   encode. Profile 7 (dual-layer) needs the enhancement layer demuxed as well. Do not ship this
   unverified: without a real DV file and a real `dovi_tool` it cannot be tested, and it rewrites
   the one piece of metadata nobody can check by eye.
2. **A search that runs during the conversion rather than before it.** The quality search costs
   eight to eleven short encodes up front. Sampling more of the film — or refining the setting
   against the encode already in progress — would be both more accurate and free.
3. **The rest of per-title tuning**: psy-rd, aq-mode, AV1 grain synthesis. These are numbers, and
   numbers want the search rather than a table: extend the search to them rather than adding
   dropdowns nobody can reason about.
4. **Per-library defaults outside the rules engine** — a library that always wants MP4, or always
   wants the originals kept, without writing a rule for it.

Pick what is most valuable, say what you chose and why, build it properly with tests, bump the
version in `build.yaml` and the csproj, repackage, push, and tell me what you could not verify.
