// What the dialog does when it cannot describe the file, and whether its controls stay legible
// on a phone. Both come from a real report: a 22.7 GiB remux rendered with no video and no audio,
// a container dropdown reading "MP4 — plays on the", and "Start conversion" still enabled.
import { launchChromium } from './browser.mjs';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const res = path.join(here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Web', 'Resources');
const js = fs.readFileSync(path.join(res, 'client.js'), 'utf8');
const css = fs.readFileSync(path.join(res, 'client.css'), 'utf8');
const bundle = js.replace('/*__MEDIAOPTIMIZER_CSS__*/', JSON.stringify(css));

const ITEM = '0f9e8d7c6b5a49382716253443210fed';

const CAPS = {
  FfmpegVersion: '7.1', FfmpegPath: '/usr/lib/jellyfin-ffmpeg/ffmpeg',
  VideoEncoders: [
    { Name: 'libx264', Codec: 'h264', DisplayName: 'H.264 (x264, CPU)', IsHardware: false, Supports10Bit: true, Presets: ['veryfast', 'medium'] },
    { Name: 'libx265', Codec: 'hevc', DisplayName: 'H.265 / HEVC (x265, CPU)', IsHardware: false, Supports10Bit: true, Presets: ['veryfast', 'medium'] }
  ],
  AudioEncoders: [{ Name: 'libopus', Codec: 'opus', DisplayName: 'Opus' }, { Name: 'aac', Codec: 'aac', DisplayName: 'AAC' }],
  Containers: ['mkv', 'mp4'], HardwareAcceleration: 'none',
  AllowHevcEncoding: true, AllowAv1Encoding: true, CanConvert: true, ProbeError: null
};

// The file from the report, as it looked when nothing could be read from it.
const UNREADABLE = {
  ItemId: ITEM, Name: 'Kung Fu Panda 4', ItemType: 'Movie',
  Path: 'G:\\MEDIA\\Movies\\Kung Fu Panda 4 (2024)\\Kung Fu Panda 4 2024 BluRay 1080p TrueHD Atmos 7 1 AVC REMUX-FraMeSToR.mkv',
  Container: 'mkv', SizeBytes: 24374173696, DurationSeconds: null, RunTimeTicks: null,
  OverallBitrate: { Bps: null, Source: 'Unknown' },
  Video: null, Audio: [], Subtitles: [], AttachmentCount: 0, ChapterCount: 0,
  IsWritable: true, HasActiveJob: false, RecommendedStrategy: 'Standard',
  IsEligible: false,
  StreamInfoError: "'C:\\Program Files\\Jellyfin\\Server\\ffprobe.exe' exited with code 1 and returned nothing usable.",
  IneligibleReason: "Neither Jellyfin nor FFmpeg could report what is inside this file, so there is nothing safe to convert. 'C:\\Program Files\\Jellyfin\\Server\\ffprobe.exe' exited with code 1 and returned nothing usable. Scan the library for this item, and check Dashboard \u2192 Playback \u2192 Transcoding points at a working FFmpeg."
};

// The same file once the streams were recovered from ffprobe.
const RECOVERED = {
  ...UNREADABLE,
  DurationSeconds: 5640, RunTimeTicks: 56400000000,
  OverallBitrate: { Bps: 34573000, Source: 'Measured' },
  Video: { Index: 0, Codec: 'h264', Profile: 'High', Width: 1920, Height: 1080, BitDepth: 8,
    PixelFormat: 'yuv420p', FrameRate: 23.976, IsVariableFrameRate: false, Range: 'SDR',
    RangeType: 'SDR', IsDolbyVision: false, IsLosslessCodec: false,
    Bitrate: { Bps: 30000000, Source: 'Derived' } },
  Audio: [{ Index: 1, TypeIndex: 0, Codec: 'truehd', Profile: 'Dolby TrueHD + Dolby Atmos',
    Channels: 8, ChannelLayout: '7.1', SampleRate: 48000, Language: 'eng', IsDefault: true,
    IsLossless: true, HasObjectAudio: true, Bitrate: { Bps: 4500000, Source: 'Derived' } }],
  Subtitles: [{ Index: 2, Codec: 'hdmv_pgs_subtitle', Language: 'eng', IsGraphical: true, IsExternal: false, IsDefault: true }],
  ChapterCount: 16, IsEligible: true, StreamInfoError: null, IneligibleReason: null,
  RecommendedStrategy: 'Medium'
};

const ESTIMATE = {
  CurrentSizeBytes: 24374173696, EstimatedSizeBytes: 9000000000,
  EstimatedSizeLowBytes: 8000000000, EstimatedSizeHighBytes: 10000000000,
  SavingFraction: 0.63, Method: 'heuristic', Confidence: 'Medium',
  EstimatedSeconds: null, TimeBasis: 'unmeasured', IsLossless: false, SavingNote: null, Warnings: []
};

let failures = 0;
const check = (cond, msg) => {
  console.log(`  ${cond ? 'ok  ' : 'FAIL'} ${msg}`);
  if (!cond) { failures++; }
};

const browser = await launchChromium();

async function open(analysis, width, estimateOverride) {
  const context = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.setContent('<!doctype html><html><head></head><body></body></html>');

  await page.evaluate(({ analysis, caps, estimate }) => {
    window.ApiClient = {
      getUrl: p => '/' + p, deviceId: () => 'dev', serverInfo: () => ({ Id: 's' }),
      ajax: opts => {
        const u = opts.url;
        if (u.includes('Analyze')) { return Promise.resolve(JSON.stringify(analysis)); }
        if (u.includes('Capabilities')) { return Promise.resolve(JSON.stringify(caps)); }
        if (u.includes('ResolveStrategy')) {
          return Promise.resolve(JSON.stringify({
            ItemId: 'x', Strategy: 'Medium', Container: 'mkv', Video: 'Encode', VideoCodec: 'libx265',
            TargetHeight: 720, BitDepth: 8, RateControl: 'ConstantQuality', Quality: 28,
            Preset: 'medium', UseHardware: false,
            AudioTracks: [{ Index: 1, Action: 'Copy' }],
            KeepAttachments: true, KeepChapters: true, OutputPolicy: 'Replace'
          }));
        }
        if (u.includes('Estimate')) { return Promise.resolve(JSON.stringify(estimate)); }
        return Promise.resolve('{}');
      }
    };
  }, { analysis, caps: CAPS, estimate: estimateOverride || ESTIMATE });

  await page.addScriptTag({ content: bundle });
  await page.waitForFunction(() => window.MediaOptimizer && window.MediaOptimizer.ready, { timeout: 5000 });
  await page.evaluate(id => window.MediaOptimizer.open(id), ITEM);
  await page.waitForTimeout(900);

  const result = await page.evaluate(() => {
    const root = window.MediaOptimizer.shadowRoot();
    const buttons = Array.from(root.querySelectorAll('button'));
    const start = buttons.filter(b => /Start conversion/.test(b.textContent))[0];
    // A <select> clips its label rather than wrapping it, and reports no overflow while doing
    // so, so scrollWidth cannot see this. Measure the label in the select's own font instead and
    // compare against the usable width (the box, less padding and the dropdown arrow).
    const ruler = document.createElement('span');
    ruler.style.cssText = 'position:absolute;visibility:hidden;white-space:pre';
    document.body.appendChild(ruler);
    const clipped = [];
    for (const sel of root.querySelectorAll('select')) {
      const opt = sel.options[sel.selectedIndex];
      if (!opt) { continue; }
      const cs = getComputedStyle(sel);
      ruler.style.font = cs.font || `${cs.fontSize} ${cs.fontFamily}`;
      ruler.textContent = opt.textContent;
      const usable = sel.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight) - 18;
      if (ruler.offsetWidth > usable) { clipped.push(opt.textContent); }
    }
    ruler.remove();
    const dialog = root.querySelector('.mopt-dialog');
    return {
      blockers: Array.from(root.querySelectorAll('.mopt-warn-blocker')).map(b => b.textContent),
      hasStart: !!start,
      startDisabled: start ? start.disabled : null,
      clipped,
      overflows: dialog ? dialog.scrollWidth > dialog.clientWidth + 1 : false,
      kv: Array.from(root.querySelectorAll('.mopt-kv dd')).map(d => d.textContent),
      estimateSub: (root.querySelector('.mopt-estimate-sub') || {}).textContent,
      paneText: (root.querySelectorAll('.mopt-pane')[1] || {}).textContent,
      buttons: Array.from(root.querySelectorAll('.mopt-foot button')).map(b => b.textContent).join(' | ')
    };
  });
  result.pageErrors = errors;
  await context.close();
  return result;
}

console.log('\n=== file whose streams could not be read ===');
{
  const r = await open(UNREADABLE, 412);
  check(r.pageErrors.length === 0, `no page errors${r.pageErrors.length ? ': ' + r.pageErrors[0] : ''}`);
  check(r.blockers.length > 0, 'the dialog explains that nothing could be read');
  check(r.blockers.some(b => b.includes('ffprobe.exe')), 'the blocker names the actual FFmpeg failure');
  // This is the dangerous one: with no streams the job would map nothing and write an empty file.
  check(r.hasStart === false, '"Start conversion" is not offered for a file that cannot be described');
}

console.log('\n=== same file, streams recovered from ffprobe ===');
{
  const r = await open(RECOVERED, 412);
  check(r.pageErrors.length === 0, `no page errors${r.pageErrors.length ? ': ' + r.pageErrors[0] : ''}`);
  check(r.blockers.length === 0, 'no blocker once the file can be described');
  check(r.hasStart === true, '"Start conversion" is offered');
  check(r.kv.some(v => /1h 34m|94m/.test(v)), `duration is shown rather than a dash (got: ${r.kv.join(' | ')})`);
  check(!r.kv.slice(0, 4).includes('—'), 'no blank fields in the summary');
}

console.log('\n=== controls stay readable at phone width ===');
for (const width of [412, 360]) {
  const r = await open(RECOVERED, width);
  check(r.clipped.length === 0,
    `${width}px: no dropdown truncates its own label${r.clipped.length ? ' (got: ' + r.clipped.join(', ') + ')' : ''}`);
  check(r.overflows === false, `${width}px: the dialog does not scroll sideways`);
}

// --- a file that is already converting --------------------------------------------------------
// Opening the dialog on one used to show the form, offer "Start conversion", and have the server
// refuse it. The job that is running right now is a better answer than that.
console.log('\n=== a file that is already converting ===');
{
  const converting = JSON.parse(JSON.stringify(RECOVERED));
  converting.HasActiveJob = true;
  converting.ActiveJobId = 'job-1';

  const r = await open(converting, 1280);

  check(r.pageErrors.length === 0, `no page errors${r.pageErrors.length ? ': ' + r.pageErrors[0] : ''}`);
  check(/Conversion queued|Encoding/.test(r.paneText || ''),
    `the dialog shows the conversion rather than the form (${(r.paneText || '').slice(0, 60)})`);
  check(!r.hasStart, 'and does not offer to start a second one');
  check(/Cancel conversion/.test(r.buttons || ''), 'it offers to cancel the one that is running');
}

// --- every number says what kind of number it is ----------------------------------------------
// A figure with no label reads as a fact. The weakest estimate here is a guess made without the
// file's own bitrate, and the dialog used to show it exactly as confidently as a measurement.
console.log('\n=== a weak estimate says so ===');
{
  const rough = await open(RECOVERED, 1280, {
    CurrentSizeBytes: 24374173696, EstimatedSizeBytes: 12000000000,
    EstimatedSizeLowBytes: 9000000000, EstimatedSizeHighBytes: 15000000000,
    SavingFraction: 0.5, Method: 'heuristic', Confidence: 'Low',
    EstimatedSeconds: null, TimeBasis: 'unmeasured', IsLossless: false, SavingNote: null, Warnings: []
  });

  check(rough.pageErrors.length === 0,
    `no page errors${rough.pageErrors.length ? ': ' + rough.pageErrors[0] : ''}`);
  check(/rough guess/.test(rough.estimateSub || ''),
    `a guess is labelled a guess (got: ${rough.estimateSub})`);
  check(!/estimate \d/.test(rough.estimateSub || ''),
    'and it does not also claim a range it cannot support');

  const none = await open(RECOVERED, 1280, {
    CurrentSizeBytes: 0, EstimatedSizeBytes: 0, EstimatedSizeLowBytes: 0, EstimatedSizeHighBytes: 0,
    SavingFraction: 0, Method: 'heuristic', Confidence: 'Unknown',
    EstimatedSeconds: null, TimeBasis: 'unmeasured', IsLossless: false, SavingNote: null, Warnings: []
  });

  check(/no estimate/.test(none.estimateSub || ''),
    `a file nothing can be predicted from says that too (got: ${none.estimateSub})`);
}

// --- the progress view when the server stops answering ----------------------------------------
// A bar that has stopped moving reads as an encode that is still going, and a Cancel button that
// goes grey reads as a cancel that worked. Both were silent.
console.log('\n=== the progress view when the server stops answering ===');
{
  const converting = JSON.parse(JSON.stringify(RECOVERED));
  converting.HasActiveJob = true;
  converting.ActiveJobId = 'job-1';

  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.setContent('<!doctype html><html><head></head><body></body></html>');

  await page.evaluate(({ analysis, caps }) => {
    window.__failPoll = false;
    window.__failCancel = false;
    window.ApiClient = {
      getUrl: p => '/' + p, deviceId: () => 'dev', serverInfo: () => ({ Id: 's' }),
      ajax: opts => {
        const u = opts.url;
        if (u.includes('Analyze')) { return Promise.resolve(JSON.stringify(analysis)); }
        if (u.includes('Capabilities')) { return Promise.resolve(JSON.stringify(caps)); }
        if (u.includes('Jobs/job-1')) {
          if (opts.type === 'DELETE') {
            return window.__failCancel
              ? Promise.reject({ status: 409, statusText: 'Conflict' })
              : Promise.resolve('{}');
          }

          if (window.__failPoll) {
            return Promise.reject({ status: 502, statusText: 'Bad Gateway' });
          }

          return Promise.resolve(JSON.stringify(window.__finished
            ? {
              Id: 'job-1', ItemName: 'Kung Fu Panda 4', Status: 'Completed',
              ProgressPercent: 100, SourceSizeBytes: 24374173696, OutputSizeBytes: 9000000000,
              QualityMetric: 'SSIM', QualityScore: 0.9831,
              QualityNote: 'measured SSIM 0.9831 at its worst across 3 point(s) of the finished '
                + 'file: very hard to tell apart from the source'
            }
            : {
              Id: 'job-1', ItemName: 'Kung Fu Panda 4', Status: 'Encoding',
              ProgressPercent: 43, Speed: 1.8, EtaSeconds: 1800
            }));
        }

        return Promise.resolve('{}');
      }
    };
  }, { analysis: converting, caps: CAPS });

  await page.addScriptTag({ content: bundle });
  await page.waitForFunction(() => window.MediaOptimizer && window.MediaOptimizer.ready, { timeout: 5000 });
  await page.evaluate(id => window.MediaOptimizer.open(id), ITEM);
  await page.waitForTimeout(900);

  const running = await page.evaluate(() => {
    const root = window.MediaOptimizer.shadowRoot();
    const bar = root.querySelector('.mopt-bar i') || root.querySelector('.mopt-bar > *');
    const shown = Array.from(root.querySelectorAll('.mopt-warn-blocker'))
      .filter(n => n.style.display !== 'none');
    return {
      progress: bar ? bar.style.width : null,
      blockers: shown.map(n => n.textContent)
    };
  });

  check(/43/.test(running.progress || ''), `the bar shows the progress it was given (${running.progress})`);
  check(running.blockers.length === 0, 'and says nothing is wrong while the polls are answered');

  // Four ticks at 1.5s, so three consecutive failures have certainly happened.
  const lost = await page.evaluate(async () => {
    window.__failPoll = true;
    await new Promise(r => setTimeout(r, 5200));
    const root = window.MediaOptimizer.shadowRoot();
    return Array.from(root.querySelectorAll('.mopt-warn-blocker'))
      .filter(n => n.style.display !== 'none').map(n => n.textContent).join(' ');
  });

  check(/cannot reach the server/.test(lost),
    `a progress view that has lost contact says so (${lost.slice(0, 70)})`);
  check(/unaffected/.test(lost), 'and says the conversion itself is unaffected');
  check(/Bad Gateway/.test(lost), 'and quotes what went wrong');

  // And it goes away again when the server comes back, rather than becoming furniture.
  const recovered = await page.evaluate(async () => {
    window.__failPoll = false;
    await new Promise(r => setTimeout(r, 2000));
    const root = window.MediaOptimizer.shadowRoot();
    return Array.from(root.querySelectorAll('.mopt-warn-blocker'))
      .filter(n => n.style.display !== 'none').length;
  });

  check(recovered === 0, `and takes it back when the server answers again (${recovered} left)`);

  const refused = await page.evaluate(async () => {
    window.__failCancel = true;
    const root = window.MediaOptimizer.shadowRoot();
    const button = Array.from(root.querySelectorAll('.mopt-foot button'))
      .find(b => /Cancel conversion/.test(b.textContent));
    button.click();
    await new Promise(r => setTimeout(r, 400));
    return {
      disabled: button.disabled,
      text: Array.from(root.querySelectorAll('.mopt-warn-blocker'))
        .filter(n => n.style.display !== 'none').map(n => n.textContent).join(' ')
    };
  });

  check(/Could not cancel/.test(refused.text),
    `a cancel the server refuses says so (${refused.text.slice(0, 70)})`);
  check(/still running/.test(refused.text), 'and that the conversion is still running');
  check(refused.disabled === false, 'and the button can be pressed again');
  check(errors.length === 0, `no page errors${errors.length ? ': ' + errors[0] : ''}`);

  // And when it finishes: what it came out looking like, measured against the original rather
  // than predicted from samples — the only figure in this dialog that is not a forecast.
  const finished = await page.evaluate(async () => {
    window.__finished = true;
    await new Promise(r => setTimeout(r, 2000));
    const root = window.MediaOptimizer.shadowRoot();
    return Array.from(root.querySelectorAll('.mopt-sub'))
      .map(n => n.textContent).join(' | ');
  });

  check(/SSIM 0\.9831/.test(finished),
    `a finished conversion reports its measured quality (${finished.slice(-90)})`);
  check(/very hard to tell apart/.test(finished), 'and says what the number means');

  await context.close();
}

await browser.close();
console.log(failures === 0 ? '\nAll analysis checks passed.' : `\n${failures} analysis check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
