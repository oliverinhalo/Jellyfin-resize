// Covers the two ways the dialog could ask for a conversion with no video encoder attached to
// it, both of which ended with the server rejecting the job for "No video encoder was selected"
// after the form had looked perfectly normal.
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

// 1080p H.264 — the file from the report, where only a resolution change saves anything.
const ANALYSIS = {
  ItemId: ITEM, Name: 'Example', ItemType: 'Movie', Path: '/media/Example.mkv',
  Container: 'mkv', SizeBytes: 3221225472, DurationSeconds: 5400, RunTimeTicks: 54000000000,
  OverallBitrate: { Bps: 4770000, Source: 'Measured' },
  Video: { Index: 0, Codec: 'h264', Profile: 'High', Width: 1920, Height: 1080, BitDepth: 8,
    PixelFormat: 'yuv420p', FrameRate: 23.976, IsVariableFrameRate: false, Range: 'SDR',
    RangeType: 'SDR', IsDolbyVision: false, IsLosslessCodec: false,
    Bitrate: { Bps: 4500000, Source: 'Measured' } },
  Audio: [{ Index: 1, TypeIndex: 0, Codec: 'aac', Channels: 6, ChannelLayout: '5.1',
    SampleRate: 48000, IsDefault: true, IsLossless: false, HasObjectAudio: false,
    Bitrate: { Bps: 256000, Source: 'Measured' } }],
  Subtitles: [], AttachmentCount: 0, ChapterCount: 0, IsWritable: true, IsEligible: true,
  HasActiveJob: false, RecommendedStrategy: 'Medium'
};

const ESTIMATE = {
  CurrentSizeBytes: 3221225472, EstimatedSizeBytes: 1610612736,
  EstimatedSizeLowBytes: 1400000000, EstimatedSizeHighBytes: 1800000000,
  SavingFraction: 0.5, Method: 'heuristic', Confidence: 'Medium',
  EstimatedSeconds: null, TimeBasis: 'unmeasured', IsLossless: false, SavingNote: null,
  Warnings: []
};

const ENCODERS = [
  { Name: 'libx264', Codec: 'h264', DisplayName: 'H.264 (x264, CPU)', IsHardware: false, Supports10Bit: true, Presets: ['veryfast', 'medium', 'slow'] },
  { Name: 'libx265', Codec: 'hevc', DisplayName: 'H.265 / HEVC (x265, CPU)', IsHardware: false, Supports10Bit: true, Presets: ['veryfast', 'medium', 'slow'] }
];

const AUDIO_ENCODERS = [
  { Name: 'libopus', Codec: 'opus', DisplayName: 'Opus' },
  { Name: 'aac', Codec: 'aac', DisplayName: 'AAC' }
];

function caps(videoEncoders, probeError) {
  return {
    FfmpegVersion: '7.1', FfmpegPath: '/usr/lib/jellyfin-ffmpeg/ffmpeg',
    VideoEncoders: videoEncoders, AudioEncoders: AUDIO_ENCODERS,
    Containers: ['mkv', 'mp4'], HardwareAcceleration: 'none',
    AllowHevcEncoding: true, AllowAv1Encoding: true, CanConvert: true,
    ProbeError: probeError || null
  };
}

let failures = 0;
const check = (cond, msg) => {
  console.log(`  ${cond ? 'ok  ' : 'FAIL'} ${msg}`);
  if (!cond) { failures++; }
};

const browser = await launchChromium();

/** Opens the dialog against a given capability set and resolved request, and reports what it shows. */
async function open(capabilities, resolved) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));

  await page.setContent('<!doctype html><html><head></head><body></body></html>');
  await page.evaluate(({ analysis, capabilities, estimate, resolved }) => {
    window.__estimateBodies = [];
    window.ApiClient = {
      getUrl: p => '/' + p,
      deviceId: () => 'dev',
      serverInfo: () => ({ Id: 's' }),
      ajax: opts => {
        const u = opts.url;
        if (u.includes('Analyze')) { return Promise.resolve(JSON.stringify(analysis)); }
        if (u.includes('Capabilities')) { return Promise.resolve(JSON.stringify(capabilities)); }
        if (u.includes('ResolveStrategy')) { return Promise.resolve(JSON.stringify(resolved)); }
        if (u.includes('Estimate')) {
          window.__estimateBodies.push(JSON.parse(opts.data));
          return Promise.resolve(JSON.stringify(estimate));
        }
        return Promise.resolve('{}');
      }
    };
  }, { analysis: ANALYSIS, capabilities, estimate: ESTIMATE, resolved });

  await page.addScriptTag({ content: bundle });
  await page.waitForFunction(() => window.MediaOptimizer && window.MediaOptimizer.ready, { timeout: 5000 });
  await page.evaluate(id => window.MediaOptimizer.open(id), ITEM);
  await page.waitForTimeout(900);

  const result = await page.evaluate(() => {
    const root = window.MediaOptimizer.shadowRoot();
    const fields = {};
    root.querySelectorAll('.mopt-field').forEach(f => {
      const label = f.querySelector('label');
      const select = f.querySelector('select');
      if (!label || !select) { return; }
      // Several tracks can share a label; the video row is the first of its name.
      if (fields[label.textContent] !== undefined) { return; }
      fields[label.textContent] = {
        options: Array.from(select.options).map(o => o.value),
        value: select.value
      };
    });
    return {
      fields,
      blockers: Array.from(root.querySelectorAll('.mopt-warn-blocker')).map(b => b.textContent),
      lastEstimate: window.__estimateBodies[window.__estimateBodies.length - 1] || null
    };
  });

  result.pageErrors = errors;
  await context.close();
  return result;
}

// --- 1. The server found no video encoders at all --------------------------------------------
// This is what a broken or unreachable ffmpeg looks like from the dialog. It used to render an
// empty Codec dropdown with nothing to pick, and the reason only appeared after the job failed.
console.log('\n=== no video encoders reported ===');
{
  const probeError = "'/usr/lib/jellyfin-ffmpeg/ffmpeg -encoders' exited with code 127 and listed no encoders.";
  const r = await open(caps([], probeError), {
    ItemId: ITEM, Strategy: 'Medium', Container: 'mkv', Video: 'Copy', VideoCodec: null,
    RateControl: 'ConstantQuality', AudioTracks: [{ Index: 1, Action: 'Copy' }],
    KeepAttachments: true, KeepChapters: true, OutputPolicy: 'Replace'
  });

  check(r.pageErrors.length === 0, `no page errors${r.pageErrors.length ? ': ' + r.pageErrors[0] : ''}`);
  check(r.blockers.length > 0, 'the dialog says why nothing can be re-encoded');
  check(r.blockers.some(b => b.includes('no video encoders')), 'the blocker names the missing encoders');
  check(r.blockers.some(b => b.includes('exited with code 127')),
    'the blocker passes on the specific reason from the server');
  check(r.fields.Action && !r.fields.Action.options.includes('Encode'),
    '"Re-encode" is not offered when it cannot work');
  check(r.fields.Codec === undefined, 'no empty Codec dropdown is rendered');
}

// --- 2. The resolved request arrives without a codec ------------------------------------------
// A <select> displays its first option when nothing matches, and re-picking that option fires no
// change event — so the request kept a null the form was not showing.
console.log('\n=== resolved request carries no codec ===');
{
  const r = await open(caps(ENCODERS), {
    ItemId: ITEM, Strategy: 'Medium', Container: 'mkv', Video: 'Encode', VideoCodec: null,
    TargetHeight: 720, BitDepth: 8, RateControl: 'ConstantQuality', Quality: 23,
    UseHardware: false, AudioTracks: [{ Index: 1, Action: 'Copy' }],
    KeepAttachments: true, KeepChapters: true, OutputPolicy: 'Replace'
  });

  check(r.pageErrors.length === 0, `no page errors${r.pageErrors.length ? ': ' + r.pageErrors[0] : ''}`);
  check(!!r.fields.Codec, 'a Codec dropdown is rendered');
  check(r.fields.Codec.value === 'libx264', `the dropdown shows the first encoder (got: ${r.fields.Codec && r.fields.Codec.value})`);
  check(r.lastEstimate !== null, 'an estimate was requested');
  check(r.lastEstimate && r.lastEstimate.VideoCodec === r.fields.Codec.value,
    `the request carries the codec the form is showing (got: ${r.lastEstimate && r.lastEstimate.VideoCodec})`);
}

// --- 3. The chosen codec is no longer available ------------------------------------------------
// Silently swapping it for another one would change what the user asked for without saying so.
console.log('\n=== chosen codec missing from this server ===');
{
  const r = await open(caps(ENCODERS), {
    ItemId: ITEM, Strategy: 'Medium', Container: 'mkv', Video: 'Encode', VideoCodec: 'libsvtav1',
    TargetHeight: 720, BitDepth: 8, RateControl: 'ConstantQuality', Quality: 32,
    UseHardware: false, AudioTracks: [{ Index: 1, Action: 'Copy' }],
    KeepAttachments: true, KeepChapters: true, OutputPolicy: 'Replace'
  });

  check(r.pageErrors.length === 0, `no page errors${r.pageErrors.length ? ': ' + r.pageErrors[0] : ''}`);
  check(r.lastEstimate && r.lastEstimate.VideoCodec === 'libsvtav1',
    'the request is not quietly rewritten to a different codec');
  check(r.fields.Codec && r.fields.Codec.value === 'libsvtav1',
    'the dropdown shows the codec the request actually carries');
}

await browser.close();
console.log(failures === 0 ? '\nAll encoder-selection checks passed.' : `\n${failures} encoder-selection check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
