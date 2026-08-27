import { chromium } from 'playwright';
import fs from 'node:fs'; import path from 'node:path'; import { fileURLToPath } from 'node:url';
const here = path.dirname(fileURLToPath(import.meta.url));
const res = path.join(here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Web', 'Resources');
const bundle = fs.readFileSync(path.join(res, 'client.js'), 'utf8')
  .replace('/*__MEDIAOPTIMIZER_CSS__*/', JSON.stringify(fs.readFileSync(path.join(res, 'client.css'), 'utf8')));

// Rules of this shape exist in real app stylesheets. Any one of them wrecks a grid layout that
// is not isolated from the host page.
const HOSTILE = [
  ['grid children forced to one cell', 'div > div { grid-area: 1 / 1; }'],
  ['everything absolutely positioned', '.mopt-body > div { position: absolute; top: 0; left: 0; }'],
  ['display overridden', '.mopt-body { display: block !important; } .mopt-pane { position: absolute; top: 0; }']
];

const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });
for (const [label, css] of HOSTILE) {
  const ctx = await browser.newContext({ viewport: { width: 412, height: 915 } });
  const page = await ctx.newPage();
  await page.setContent(`<!doctype html><html><head><style>${css}</style></head><body></body></html>`);
  await page.evaluate(() => {
    window.ApiClient = { getUrl: p => '/' + p, deviceId: () => 'd', serverInfo: () => ({}),
      ajax: o => Promise.resolve(o.url.includes('Analyze')
        ? JSON.stringify({ ItemId:'x', Name:'Coco', Path:'/m/coco.mkv', Container:'mkv', SizeBytes:5368709120,
            DurationSeconds:6300, OverallBitrate:{Bps:6820000,Source:'Measured'}, IsEligible:true, IsWritable:true,
            Video:{Index:0,Codec:'hevc',Width:3840,Height:2160,BitDepth:10,FrameRate:23.976,Range:'SDR',Bitrate:{Bps:6820000,Source:'Measured'}},
            Audio:[{Index:1,TypeIndex:0,Codec:'aac',Channels:6,Bitrate:{Bps:320000,Source:'Measured'}}], Subtitles:[], AttachmentCount:0, ChapterCount:0, RecommendedStrategy:'Medium' })
        : o.url.includes('ResolveStrategy')
        ? JSON.stringify({ ItemId:'x', Strategy:'Standard', Container:'mp4', Video:'Encode', VideoCodec:'libx265',
            TargetHeight:null, BitDepth:10, RateControl:'ConstantQuality', Quality:28, Preset:'medium',
            UseHardware:false, AudioTracks:[{Index:1,Action:'Copy'}], KeepAttachments:true, KeepChapters:true,
            OutputPolicy:'Replace' })
        : o.url.includes('Capabilities')
        ? JSON.stringify({ VideoEncoders:[{Name:'libx265',Codec:'hevc',DisplayName:'x265',Supports10Bit:true,Presets:['medium']}],
            AudioEncoders:[{Name:'aac',Codec:'aac',DisplayName:'AAC'}], Containers:['mkv','mp4'], CanConvert:true })
        : JSON.stringify({ CurrentSizeBytes:5368709120, EstimatedSizeBytes:3000000000, EstimatedSizeLowBytes:2.5e9,
            EstimatedSizeHighBytes:3.5e9, SavingFraction:0.44, Warnings:[] })) };
  });
  await page.addScriptTag({ content: bundle });
  await page.waitForFunction(() => window.MediaOptimizer?.ready, { timeout: 5000 });
  await page.evaluate(() => window.MediaOptimizer.open('x'));
  await page.waitForTimeout(900);
  const r = await page.evaluate(() => {
    const root = window.MediaOptimizer.shadowRoot();
    if (!root) return { panes: 0 };
    const p = root.querySelectorAll('.mopt-pane');
    if (p.length < 2) return { panes: p.length };
    const a = p[0].getBoundingClientRect(), b = p[1].getBoundingClientRect();
    return { overlap: !(a.right<=b.left+1||b.right<=a.left+1||a.bottom<=b.top+1||b.bottom<=a.top+1) };
  });
  console.log(`${r.overlap ? 'BROKEN ' : 'ok     '} ${label}`);
  await ctx.close();
}
await browser.close();
