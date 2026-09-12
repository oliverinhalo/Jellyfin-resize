import { launchChromium } from './browser.mjs';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const res = path.join(here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Web', 'Resources');
const js = fs.readFileSync(path.join(res, 'client.js'), 'utf8');
const css = fs.readFileSync(path.join(res, 'client.css'), 'utf8');
const bundle = js.replace('/*__MEDIAOPTIMIZER_CSS__*/', JSON.stringify(css));

// Modelled on the real file from the bug report: 4K HEVC Main 10, 5 GiB, 8 PGS subtitle tracks.
const ANALYSIS = {
  ItemId: '0f9e8d7c6b5a49382716253443210fed', Name: 'Coco', ItemType: 'Movie',
  Path: 'G:\\MEDIA\\Movies\\Coco.2017.2160p.4K.BluRay.x265.10bit.AAC5.1-[YTS.MX]\\Coco.2017.2160p.mkv',
  Container: 'mkv', SizeBytes: 5368709120, DurationSeconds: 6300, RunTimeTicks: 63000000000,
  OverallBitrate: { Bps: 6820000, Source: 'Measured' },
  Video: { Index: 0, Codec: 'hevc', Profile: 'Main 10', Width: 3840, Height: 2160, BitDepth: 10,
    PixelFormat: 'yuv420p10le', FrameRate: 23.976, IsVariableFrameRate: false, Range: 'SDR',
    RangeType: 'SDR', IsDolbyVision: false, IsLosslessCodec: false,
    Bitrate: { Bps: 6500000, Source: 'Measured' } },
  Audio: [{ Index: 1, TypeIndex: 0, Codec: 'aac', Channels: 6, ChannelLayout: '5.1',
    SampleRate: 48000, IsDefault: true, IsLossless: false, HasObjectAudio: false,
    Bitrate: { Bps: 320000, Source: 'Measured' } }],
  Subtitles: Array.from({ length: 8 }, (_, i) => ({
    Index: 2 + i, Codec: 'hdmv_pgs_subtitle', Language: ['eng','fra','spa','eng','eng','por','fra','deu'][i],
    IsGraphical: true, IsExternal: false, IsDefault: i === 0 })),
  AttachmentCount: 0, ChapterCount: 0, IsWritable: true, IsEligible: true, HasActiveJob: false,
  RecommendedStrategy: 'Medium'
};

const CAPS = {
  FfmpegVersion: '7.0.2', FfmpegPath: '/usr/lib/jellyfin-ffmpeg/ffmpeg',
  VideoEncoders: [
    { Name: 'libx264', Codec: 'h264', DisplayName: 'H.264 (x264, CPU)', IsHardware: false, Supports10Bit: true, Presets: ['veryfast','fast','medium','slow'] },
    { Name: 'libx265', Codec: 'hevc', DisplayName: 'H.265 / HEVC (x265, CPU)', IsHardware: false, Supports10Bit: true, Presets: ['veryfast','fast','medium','slow'] },
    { Name: 'libsvtav1', Codec: 'av1', DisplayName: 'AV1 (SVT-AV1, CPU)', IsHardware: false, Supports10Bit: true, Presets: ['6','8','10'] }
  ],
  AudioEncoders: [
    { Name: 'libopus', Codec: 'opus', DisplayName: 'Opus' },
    { Name: 'aac', Codec: 'aac', DisplayName: 'AAC' },
    { Name: 'flac', Codec: 'flac', DisplayName: 'FLAC (lossless)' }
  ],
  Containers: ['mkv','mp4'], HardwareAcceleration: 'none',
  AllowHevcEncoding: true, AllowAv1Encoding: false, CanConvert: true
};

const ESTIMATE = {
  CurrentSizeBytes: 5368709120, EstimatedSizeBytes: 2952790016,
  EstimatedSizeLowBytes: 2214592512, EstimatedSizeHighBytes: 3690987520,
  SavingFraction: 0.45, Method: 'heuristic', Confidence: 'Medium',
  EstimatedSeconds: null, TimeBasis: 'unmeasured', IsLossless: false, SavingNote: null,
  Warnings: [
    { Level: 'Warning', Code: 'SUBTITLE_INCOMPATIBLE', Message: 'MP4 cannot store image-based subtitles, so 8 track(s) will be dropped. Switch the container to MKV to keep them.' },
    { Level: 'Info', Code: 'AUDIO_DOWNMIX', Message: 'Track 1 will be re-encoded to Opus at 192 kb/s.' },
    { Level: 'Info', Code: 'VFR_SOURCE', Message: 'Frame timing is passed through unchanged.' }
  ]
};

const widths = [
  { name: 'mobile', width: 412, height: 915 },
  { name: 'desktop', width: 1440, height: 900 }
];

let failures = 0;

const browser = await launchChromium();

for (const vp of widths) {
  const context = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
  const page = await context.newPage();
  const logs = [];
  page.on('console', m => logs.push(m.type() + ': ' + m.text()));
  page.on('pageerror', e => logs.push('PAGEERROR: ' + e.message));

  await page.setContent('<!doctype html><html><head></head><body style="background:#101010"></body></html>');

  await page.evaluate(({ analysis, caps, estimate }) => {
    window.ApiClient = {
      getUrl: p => '/' + p,
      deviceId: () => 'dev',
      serverInfo: () => ({ Id: 's' }),
      ajax: opts => {
        const u = opts.url;
        if (u.includes('Analyze')) return Promise.resolve(JSON.stringify(analysis));
        if (u.includes('Capabilities')) return Promise.resolve(JSON.stringify(caps));
        if (u.includes('ResolveStrategy')) return Promise.resolve(JSON.stringify({
          ItemId:'x', Strategy: u.indexOf('Medium')>=0 ? 'Medium':'Standard', Container:'mkv', Video:'Encode',
          VideoCodec:'libx265', TargetHeight: u.indexOf('Medium')>=0 ? 1440 : null, BitDepth:10,
          RateControl:'ConstantQuality', Quality:28, Preset:'medium', UseHardware:false,
          AudioTracks:[{Index:1,Action:'Copy'}], KeepAttachments:true, KeepChapters:true, OutputPolicy:'Replace' }));
        // Measuring and searching are started, not awaited: the server answers with an id and
        // the dialog polls. The stub answers "running" once so the polling path is exercised
        // rather than skipped.
        if (u.includes('FindQuality')) return Promise.resolve(JSON.stringify({ Id: 'op-search' }));
        if (u.includes('Operations/op-search')) {
          window.__searchPolls = (window.__searchPolls || 0) + 1;
          if (window.__searchPolls < 2) {
            return Promise.resolve(JSON.stringify({
              Id: 'op-search', Kind: 'search', Status: 'Running', ElapsedSeconds: 12 }));
          }

          return Promise.resolve(JSON.stringify({
            Id: 'op-search', Kind: 'search', Status: 'Completed', ElapsedSeconds: 140,
            Result: {
              Quality: 24, Metric: 'SSIM', WorstScore: 0.9821, AverageScore: 0.9863,
              WorstScoreText: '0.9821', Verdict: 'very hard to tell apart from the source',
              Probes: 8, SecondsConfirmed: 24, EstimatedSizeBytes: 2952790016,
              SavingFraction: 0.45, Note: null, FailureReason: null } }));
        }
        if (u.includes('Estimate')) return Promise.resolve(JSON.stringify(estimate));
        return Promise.resolve('{}');
      }
    };
  }, { analysis: ANALYSIS, caps: CAPS, estimate: ESTIMATE });

  await page.addScriptTag({ content: bundle });
  await page.waitForFunction(() => window.MediaOptimizer && window.MediaOptimizer.ready, { timeout: 5000 });
  await page.evaluate(() => window.MediaOptimizer.open('0f9e8d7c6b5a49382716253443210fed'));
  await page.waitForTimeout(1100);

  const shot = path.join(here, 'shots', `dialog-${vp.name}.png`);
  fs.mkdirSync(path.dirname(shot), { recursive: true });
  await page.screenshot({ path: shot, fullPage: true });

  // Measure whether the two panes actually overlap.
  const geom = await page.evaluate(() => {
    const root = window.MediaOptimizer.shadowRoot();
    if (!root) return { error: 'no shadow root' };
    const body = root.querySelector('.mopt-body');
    const panes = root.querySelectorAll('.mopt-pane');
    if (!body || panes.length < 2) return { error: 'panes missing', paneCount: panes.length };
    const cs = getComputedStyle(body);
    const a = panes[0].getBoundingClientRect();
    const b = panes[1].getBoundingClientRect();
    const overlap = !(a.right <= b.left + 1 || b.right <= a.left + 1 || a.bottom <= b.top + 1 || b.bottom <= a.top + 1);
    return {
      display: cs.display,
      cols: cs.gridTemplateColumns,
      left: { x: Math.round(a.x), y: Math.round(a.y), w: Math.round(a.width), h: Math.round(a.height) },
      right: { x: Math.round(b.x), y: Math.round(b.y), w: Math.round(b.width), h: Math.round(b.height) },
      overlap,
      estimate: (root.querySelector('.mopt-estimate-main') || {}).textContent,
      estimateSub: (root.querySelector('.mopt-estimate-sub') || {}).textContent,
      visibleWarnings: root.querySelectorAll('.mopt-warn').length,
      collapsedNotes: !!root.querySelector('.mopt-warn-more'),
      // Does any child render outside the pane that owns it?
      // A scrolling pane is meant to clip its content, so only a pane that cannot scroll can
      // actually spill onto whatever is underneath it.
      contentSpill: (function () {
        let worst = 0;
        for (const pane of panes) {
          const style = getComputedStyle(pane);
          if (style.overflowY !== 'visible') continue;
          const box = pane.getBoundingClientRect();
          for (const child of pane.children) {
            const c = child.getBoundingClientRect();
            worst = Math.max(worst, Math.round(c.bottom - box.bottom), Math.round(box.top - c.top));
          }
        }
        return worst;
      })()
    };
  });

  console.log(`\n=== ${vp.name} (${vp.width}px) ===`);
  const check = (cond, msg) => {
    console.log(`  ${cond ? 'ok  ' : 'FAIL'} ${msg}`);
    if (!cond) { failures++; }
  };

  check(!geom.error, 'dialog rendered inside its shadow root');
  check(geom.overlap === false, 'the two panes do not overlap');
  check(geom.contentSpill === 0, 'no pane spills its content over the next section');
  check(/saves 45%/.test(geom.estimate || ''), `estimate shows a saving (got: ${geom.estimate})`);
  check(!/grows/.test(geom.estimate || ''), 'the default preset does not predict a bigger file');
  check(geom.collapsedNotes === true, 'minor notes are collapsed behind a toggle');
  check(geom.visibleWarnings <= 5, `warnings are not spammed (${geom.visibleWarnings} nodes)`);
  check(!/\d+h \d+m to encode/.test(geom.estimateSub || ''),
    'no invented encode time before any measurement exists');

  // Finding a setting by measuring: the answer has to reach both the form and the screen, or it
  // is a five-minute encode whose result the user has to copy in by hand.
  const search = await page.evaluate(async () => {
    const root = window.MediaOptimizer.shadowRoot();
    const find = Array.from(root.querySelectorAll('.mopt-btn'))
      .find(b => b.textContent.indexOf('Find the setting') === 0);
    if (!find) { return { error: 'no find button' }; }

    find.click();
    await new Promise(r => setTimeout(r, 100));

    const targets = Array.from(root.querySelectorAll('.mopt-targets .mopt-btn')).map(b => b.textContent);
    const choice = Array.from(root.querySelectorAll('.mopt-targets .mopt-btn'))
      .find(b => b.textContent === 'Very hard to tell apart');
    if (!choice) { return { error: 'no target buttons', targets }; }

    choice.click();
    // Two polling rounds at two seconds each, plus room to render.
    await new Promise(r => setTimeout(r, 4600));

    const quality = Array.from(root.querySelectorAll('input, select'))
      .map(i => ({ id: i.id || i.name || '', value: i.value }));

    return {
      targets,
      notes: Array.from(root.querySelectorAll('.mopt-warn')).map(w => w.textContent).join(' | '),
      quality
    };
  });

  check(!search.error, `the quality search offers its targets (${search.error || search.targets.join(', ')})`);
  check(/Quality 24/.test(search.notes || ''), 'the searched setting is reported');
  check(/SSIM 0\.9821/.test(search.notes || ''), 'it is reported in the metric that measured it');
  check(/24 seconds/.test(search.notes || ''), 'it says how much of the film it was confirmed on');
  check((search.quality || []).some(f => f.value === '24'),
    'the setting it found is applied to the form, not just described');

  const errors = logs.filter(l => l.startsWith('PAGEERROR'));
  check(errors.length === 0, `no page errors${errors.length ? ': ' + errors[0] : ''}`);

  await context.close();
}

await browser.close();
console.log(failures === 0 ? '\nAll render checks passed.' : `\n${failures} render check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
