# Self-review: Media Optimizer 1.5.0.0

A review of the change that became 1.5.0.0, written by the same session that wrote the code. It is
here because a plugin that rewrites people's media files should carry a written account of what was
checked and what was not, and because the useful half of a self-review is the half that says what
is still wrong.

**Scope:** 59 files, ~6,200 lines added. Fifteen defect fixes, four features, and the tests for
both. Against the previous release the test suite goes from 174 to 319.

---

## How this was reviewed

1. **Read the whole plugin first, before changing anything.** Every C# file, both dashboard pages
   and the injected client script. Nine of the fifteen fixes below come from that pass, not from a
   bug report — they are things nobody had hit yet.
2. **Every regression test was run against the unfixed code.** A test that passes before the fix is
   not a regression test, and two of the ones written here initially did.
3. **A second review pass over my own diff.** That found six defects in code written earlier in
   this same branch, listed separately below, including one that repeated a mistake this branch
   had already fixed elsewhere.

---

## Defects found in the existing code

| # | What was wrong | Why it mattered |
|---|---|---|
| 1 | Bit-exactness was verified against the wrong stream: the *N*th lossless source track was compared with the *N*th audio stream of the output, which is only the same track when nothing ahead of it was merely copied. | A file with a copied AC-3 commentary in front of a FLAC-from-DTS-HD track compared two different tracks, reported a mismatch, and **threw away a conversion that was genuinely bit-exact**. |
| 2 | The nightly sweep read the job id out of a working file's name with a rule that never matched. | The only thing protecting a running job's output was the six-hour age check — and a deep verification scan writes nothing for longer than that on a large file. Housekeeping could delete the finished encode out from under the job about to apply it. |
| 3 | `-x265-params` was passed twice on a lossless HDR encode, once for `lossless=1` and once for the HDR10 signalling. FFmpeg keeps only the last occurrence. | The encode **quietly was not lossless**, while the job said it was. |
| 4 | `-preset` was passed twice on hardware encodes. | Whichever came last won, making the user's chosen speed a coin toss. |
| 5 | "Disk reclaimed" counted only `Replace`, not `ReplaceAndDelete`. | The one policy that frees space immediately read as zero. |
| 6 | The queue's paused flag was a static field. | A restart — the thing that follows every upgrade — resumed encoding behind the administrator's back. |
| 7 | The configuration copy used for batch language overrides silently dropped three settings. | A batch run with a language override reverted "keep originals beside the media" for that run. Now a reflection copy, with a test that cannot miss a property added later. |
| 8 | `POST /MediaOptimizer/Transform` accepted a document from anyone, unauthenticated, and returned it as `text/html` on the Jellyfin origin. | A cross-site scripting sink. Nothing called it — registration goes through File Transformation's in-process service — so it is gone, and a test now fails the build if an anonymous HTML endpoint reappears. |
| 9 | The conversion dialog's progress poll kept running after the dialog was closed with the X, Escape or the backdrop. | A request every 1.5 seconds for a job that finished hours ago, for as long as the tab stayed open. |

Also in that pass: the dashboard re-bound its event handlers on every `pageshow`, so after
navigating away and back one click on "pause queue" sent two requests; endpoints that reveal
filesystem paths, list the queue, or make the server read an entire media file were available to
any signed-in user; 10-bit files whose container omits the depth were being converted to 8-bit;
nonsensical settings reached FFmpeg instead of being refused with a sentence; and no plan gave the
muxer enough queue to interleave streams whose timestamps drift apart.

---

## Defects found in my own work on this branch

These were found by reviewing the diff after writing it. They are listed in full because they are
the part of a self-review that is actually worth reading.

1. **The activity feed told a sidecar conversion it had "freed 12 GiB."** A sidecar keeps both
   files; the disk went *up*. The identical mistake had been fixed in the statistics ten commits
   earlier on this same branch, which is precisely why repeating it is worth writing down.
2. **Reading the bit depth from the pixel format broke exotic sources.** 9-, 12- and 16-bit files
   started reporting depths no encoder here writes, and the validation added on this same branch
   then blocked every preset on those files with "16-bit video is not a thing" — for a value the
   user never chose. Two changes that were each right, and wrong together.
3. **Previewing a rule did not work.** A new rule is saved switched off, the dashboard says to
   preview it before switching it on, and the preview answered "nothing, because the rule is off".
   A test I had written asserted that behaviour.
4. **A preview could probe a whole library.** Matching is free; deciding is not — each matching item
   is probed with ffprobe and planned. A selective rule whose matches all fell below its saving
   floor would walk the entire library doing that, inside one HTTP request.
5. **A rule preview listed every already-converted item in the library**, including the thousands
   the rule could never take, and the list's own cap then hid the near-misses that say something
   about the rule.
6. **The measured encode speed divided successful seconds by all-attempts time**, so one failed
   sample reported the job as three times slower than it is — under a label reading "measured",
   which is the exact failure mode the sampled estimate exists to avoid.

Each has a regression test.

---

## What is verified, and how

- **319 tests**, none skipped when ffmpeg is present. The suite includes 13 that drive a real
  ffmpeg: lossless FLAC round-trips verified by hash, a truncated output being rejected, a planned
  downscale producing exactly the requested resolution, upscaling being refused, cancellation
  actually killing the process, MP4 muxing with text subtitles, and the sampled estimate being
  compared against a full encode of the same file.
- **Six browser tests** in real Chromium: the injected UI grafting onto real jellyfin-web markup,
  the dialog surviving deliberately hostile host CSS, dialog and dashboard layout at 412px and
  1280px, the dialog's teardown, and the dashboard's rules panel.
- **The settings round-trip through `XmlSerializer`**, which is how Jellyfin persists them — the
  rules list, its nullable numbers and its nullable enum, plus a settings file written before rules
  existed. A settings file that will not load is a user losing every rule they wrote.
- **The authorization surface is asserted by reflection**: every state-changing endpoint requires an
  administrator, exactly one endpoint is anonymous, and no anonymous endpoint answers with HTML.

## What is not verified

This is the honest part, and it has not changed in kind since 1.4:

- **Nothing here has run inside a live Jellyfin server.** Compilation against the real 10.11
  packages, the embedded resources, the served bundle and all rendering are checked here. Plugin
  loading, DI resolution, controller routing and the File Transformation handshake are verified
  structurally only.
- **New in this release, and unverified:** whether Jellyfin discovers the new scheduled task
  (`RuleTask`) the same way it discovers the existing one; whether an activity-log entry written
  with an empty user id appears as a system entry as intended; whether `IActivityManager` resolves
  in this DI container at all. All three fail closed — the notifier swallows its own errors so a
  conversion that worked is never reported as failed because a log line could not be written — but
  "fails closed" is not "works".
- **The automatic rules have never run against a real library.** The matcher and the engine are
  tested exhaustively over plain data, and the layer that turns a Jellyfin item into that data is
  the part with no test, because it needs a server.
- **The dashboard status panel is the real signal.** If it disagrees with anything above, it is
  right.

## Risks I am leaving in, on purpose

- **A rule can queue conversions unattended.** That is what it is for. The mitigations are stated
  where the rule is written: a per-run ceiling, a minimum saving, a grace period, never touching an
  item twice, never accepting a Dolby Vision loss on a person's behalf, off by default, and a
  preview that queues nothing. The queue's own guarantees still apply underneath — nothing replaces
  an original until the result passes verification.
- **The worklist's saving figure is a model.** It is computed from size, resolution and codec with
  no probing, so a whole library can be ranked in one page load. It is shown with a "≈" and its
  basis, and the per-file estimate is the better number — "Measure it" is the true one.
- **Three eight-second samples cannot represent a whole film.** The spread between them is reported
  rather than averaged away for exactly that reason.
- **The injected UI depends on private jellyfin-web selectors** and will break on some future web
  release. It fails closed and says so on the dashboard; the DOM test is the early warning.

## What I would do next

1. Scope a rule to one library — the last real gap between rules and "per-library defaults".
2. Concurrency by resource: two 1080p jobs or one 4K, rather than a flat count.
3. Reordering rules in the dashboard; they apply in list order and there is no way to change it.
4. A quality number to go with the size number — VMAF or SSIM on the sampled segments — so "how
   much worse does it look?" stops being answered with a preset name.
