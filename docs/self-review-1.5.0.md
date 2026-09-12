# Self-review: Media Optimizer 1.5.0.0

A review of the change that became 1.5.0.0, written by the same session that wrote the code. It is
here because a plugin that rewrites people's media files should carry a written account of what was
checked and what was not, and because the useful half of a self-review is the half that says what
is still wrong.

**Scope:** ~70 files, ~8,200 lines added. Twenty-one defect fixes, seven features, and the tests
for both. Against the previous release the test suite goes from 174 to 383.

---

## How this was reviewed

1. **Read the whole plugin first, before changing anything.** Every C# file, both dashboard pages
   and the injected client script. Nine of the seventeen fixes below come from that pass, not from
   a bug report — they are things nobody had hit yet.
2. **Every regression test was run against the unfixed code.** A test that passes before the fix is
   not a regression test, and two of the ones written here initially did.
3. **A second review pass over my own diff.** That found eight defects in code written earlier in
   this same branch, listed separately below, including one that repeated a mistake this branch
   had already fixed elsewhere.
4. **A security pass, reading the diff as somebody looking for a way in.** What can an
   unauthenticated caller reach; what can a signed-in non-administrator learn; where does user
   input become a filesystem path, an argument vector or markup; what does this code delete. Three
   of the defects below came from it, and the two it did *not* find are worth saying: nothing here
   builds a shell command (every FFmpeg call is passed as an argument vector, so a filename with a
   semicolon in it is a filename), and nothing renders server data as HTML — the dialog and the
   dashboard both write text through `textContent`, and the only `innerHTML` in either is the empty
   string.

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
| 10 | FFmpeg's last line of output was sometimes lost: the runner waited for the process and collected its output through the completion events, which return before what is still in flight has been delivered. | With ffmpeg the last line *is* the answer — the hash a lossless check compares, the reason a job failed, a measured score. A harness that ran one command 25 times lost it once or twice a run: exactly the frequency that gets written off as "flaky" for years. Both pipes are now read to the end and those reads awaited; 100 consecutive runs, no losses. It was found only because a missing score is obvious in a way a slightly truncated error message is not. |
| 11 | Housekeeping deleted **every** file in the working directory older than six hours, not only the ones this plugin wrote. | The working directory is a path an administrator types into a settings box, and the obvious thing to type is a directory that already exists — a scratch disk, the server's own transcoding folder. This plugin would then quietly destroy other people's files, once a night, for as long as it was installed. Everything it writes is named `.mo-…`; that is now the only thing it deletes. |
| 12 | The analysis dialog handed a non-administrator the absolute path of the file on the server, and any FFmpeg error quoting it. | Every other read on that controller is administrator-only for exactly this reason, and the diagnostics page added on this same branch deliberately hides server paths from non-administrators. The one endpoint a non-administrator can actually open was the one giving it away. They now see the file name. |
| 13 | A cross-volume move copied the whole file to `<destination>.mopt-partial` first — a name the library scanner does not hide and housekeeping does not recognise. | A power cut in the middle of that copy left a full-size duplicate of a film next to it, forever. It is now named like every other working file: hidden from the scanner, swept by housekeeping, and unique per move so two conversions to one destination cannot overwrite each other's staging file. |

The last three are the security pass's. The first ten came from reading the plugin end to end.

Also in that first pass: the dashboard re-bound its event handlers on every `pageshow`, so after
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
7. **The first quality-comparison filter graph deadlocked ffmpeg.** It used `scale2ref`, and on
   ffmpeg 7 that hangs — not fails, hangs — often enough to appear within fifteen runs. Inside the
   plugin that would have been a stuck queue rather than a missing number. It is now an ordinary
   scale with the dimensions passed in, both inputs cut to the same length, and `shortest` set;
   there is a hard timeout underneath it regardless.
8. **"Try again" built a job without its own resolution or size**, which the new
   concurrency weighting then read as "unknown, assume expensive". Found by writing the test that
   every path queueing a job records what it will cost.

Each has a regression test. Two of them — the lost output line and the deadlock — are only visible
under repetition, so their tests repeat.

---

## What is verified, and how

- **383 tests**, none skipped when ffmpeg is present. The suite includes 17 that drive a real
  ffmpeg: lossless FLAC round-trips verified by hash, a truncated output being rejected, a planned
  downscale producing exactly the requested resolution, upscaling being refused, cancellation
  actually killing the process, MP4 muxing with text subtitles, and the sampled estimate being
  compared against a full encode of the same file, and a worse encode actually scoring worse on
  VMAF than a better one.
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

## Added after this review was first written

Three things, each with the same treatment — tests over plain data for the decisions, and the
Jellyfin-facing layer left honestly unverified:

- **A rule can be confined to one library.** The library is worked out from the file's path against
  the folders Jellyfin says each library is made of, so what a rule means is checkable by hand.
- **Concurrency is counted in ordinary jobs rather than in job slots.** A 4K encode counts as two,
  because two at once is not twice the work; a job that does not fit is skipped rather than
  blocking the queue behind it, and one job always starts on an idle server.
- **The quality measurement described above.**

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
  rather than averaged away for exactly that reason, and the quality score reports its worst sample
  alongside its average.
- **VMAF is a model of human opinion, not a measurement of one.** It is the best available answer
  to "how much worse does this look", and it is reported with its name attached so it can be
  weighed as such.
- **The injected UI depends on private jellyfin-web selectors** and will break on some future web
  release. It fails closed and says so on the dashboard; the DOM test is the early warning.

## What I would do next

1. Reordering rules in the dashboard; they apply in list order and there is no way to change it.
2. Per-title encoder tuning: the settings that suit animation are not the ones that suit film
   grain, and the plugin currently offers one answer for both.
3. Use the measured quality to *choose* settings rather than only to report them — "find me the
   smallest file that still scores 95" is a search the sampler could run.
4. Dolby Vision via `dovi_tool`, which is the last thing the plugin refuses outright.
