/*
 * Renders the dashboard page in a real browser with stubbed Jellyfin globals, so the collapsible
 * panels and the compact status line are verified rather than assumed.
 */
import { launchChromium } from './browser.mjs';
import fs from 'node:fs'; import path from 'node:path'; import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const page_html = fs.readFileSync(
  path.join(here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Configuration', 'queuePage.html'), 'utf8');

const DIAG = {
  PluginVersion: '1.4.0.0', ServerVersion: '10.11.11', IsAdministrator: true,
  Overall: 'Ok', Summary: 'Everything is working.',
  Checks: [
    { Name: 'Plugin loaded', Status: 'Ok', Detail: 'Version 1.4.0.0. The API is responding.' },
    { Name: 'FFmpeg', Status: 'Ok', Detail: '7.0.2 at /usr/lib/jellyfin-ffmpeg/ffmpeg — 6 video encoders.' },
    { Name: 'Encoding speed', Status: 'Warning', Detail: 'Measured 12.4 megapixels/second: roughly 3.3h for a two-hour 1080p film, 13.4h at 4K. Hardware encoding is off.' },
    { Name: 'Conversion queue', Status: 'Ok', Detail: '1 active, 12 in history.' }
  ]
};
const ITEMS = Array.from({ length: 6 }, (_, i) => ({
  Id: 'id' + i, Name: 'Film ' + (i + 1) + ' (2021)', Type: 'Movie',
  Path: '/media/film' + i + '.mkv', Container: 'mkv', SizeBytes: (5 - i * 0.4) * 1024 ** 3,
  Height: [2160, 2160, 1080, 1080, 720, 1080][i], VideoCodec: 'hevc',
  IsWatched: i % 2 === 0, HasActiveJob: i === 5
}));
const JOBS = [
  { Id: 'j1', ItemName: 'Film 6', Status: 'Encoding', ProgressPercent: 43, Speed: 1.8,
    EtaSeconds: 4200, OutputPolicy: 'Replace', SourceSizeBytes: 5 * 1024 ** 3 },
  { Id: 'j2', ItemName: 'Film 9', Status: 'Completed', OutputPolicy: 'Replace',
    SourceSizeBytes: 6 * 1024 ** 3, OutputSizeBytes: 3 * 1024 ** 3, QuarantinePath: '/media/f9.mkv.mooriginal' },
  { Id: 'j3', ItemName: 'Film 4', Status: 'Failed', OutputPolicy: 'Replace', Error: 'FFmpeg failed: no space left' }
];
const STATS = {
  CompletedJobs: 12, BytesSaved: 41 * 1024 ** 3, SourceBytesProcessed: 90 * 1024 ** 3,
  AverageSavingFraction: 0.46, FailedJobs: 1, ActiveJobs: 1, RestorableOriginals: 3,
  QuarantineBytes: 15 * 1024 ** 3, MegapixelsPerSecond: 12.4,
  SpeedNote: 'Measured on this server: about 3.3 hours to re-encode a two-hour 1080p film.',
  IsPaused: false
};

let failures = 0;
const browser = await launchChromium();

for (const vp of [{ name: 'desktop', width: 1280, height: 1000 }, { name: 'mobile', width: 412, height: 915 }]) {
  const ctx = await browser.newContext({ viewport: vp });
  const page = await ctx.newPage();
  const logs = [];
  page.on('pageerror', e => logs.push('PAGEERROR: ' + e.message));

  await page.setContent('<!doctype html><html><head></head><body style="background:#0f0f0f;color:#eee;font-family:system-ui"></body></html>');
  await page.evaluate(({ diag, items, jobs, stats }) => {
    window.Dashboard = { alert: () => {}, confirm: () => Promise.resolve(), showLoadingMsg: () => {}, hideLoadingMsg: () => {} };
    window.ApiClient = {
      getUrl: p => '/' + p,
      ajax: o => {
        const u = o.url;
        if (u.includes('Diagnostics')) return Promise.resolve(JSON.stringify(diag));
        if (u.includes('Library/Facets')) return Promise.resolve(JSON.stringify({ containers: ['mkv', 'mp4'], codecs: ['hevc', 'h264'] }));
        if (u.includes('Library/Search')) return Promise.resolve(JSON.stringify(items));
        if (u.includes('Statistics')) return Promise.resolve(JSON.stringify(stats));
        if (u.includes('Jobs')) return Promise.resolve(JSON.stringify(jobs));
        return Promise.resolve('{}');
      }
    };
  }, { diag: DIAG, items: ITEMS, jobs: JOBS, stats: STATS });

  await page.setContent(await page.evaluate(() => document.documentElement.outerHTML));
  // Re-stub after the content swap, then inject the page markup and fire pageshow.
  await page.evaluate(({ diag, items, jobs, stats, html }) => {
    window.Dashboard = { alert: () => {}, confirm: () => Promise.resolve(), showLoadingMsg: () => {}, hideLoadingMsg: () => {} };
    window.__calls = [];
    window.ApiClient = {
      getUrl: p => '/' + p,
      ajax: o => {
        const u = o.url;
        window.__calls.push((o.type || 'GET') + ' ' + u);
        if (u.includes('Diagnostics')) return Promise.resolve(JSON.stringify(diag));
        if (u.includes('Library/Facets')) return Promise.resolve(JSON.stringify({ containers: ['mkv', 'mp4'], codecs: ['hevc', 'h264'] }));
        if (u.includes('Library/Search')) return Promise.resolve(JSON.stringify(items));
        if (u.includes('Statistics')) return Promise.resolve(JSON.stringify(stats));
        if (u.includes('Jobs')) return Promise.resolve(JSON.stringify(jobs));
        return Promise.resolve('{}');
      }
    };
    const body = html.slice(html.indexOf('<div id="MediaOptimizerQueuePage"'), html.lastIndexOf('</div>') + 6);
    document.body.innerHTML = body;
    document.querySelectorAll('script').forEach(old => {
      const s = document.createElement('script');
      s.textContent = old.textContent;
      old.replaceWith(s);
    });
    document.querySelector('#MediaOptimizerQueuePage').dispatchEvent(new Event('pageshow'));
  }, { diag: DIAG, items: ITEMS, jobs: JOBS, stats: STATS, html: page_html });

  await page.waitForTimeout(700);

  const r = await page.evaluate(() => {
    const q = s => document.querySelector(s);
    return {
      statusLine: (q('#moptDiagLine') || {}).textContent,
      statusCollapsed: q('#moptDiagPanel') ? !q('#moptDiagPanel').open : null,
      pickerOpen: q('#moptPickerPanel') ? q('#moptPickerPanel').open : null,
      pickerHint: (q('#moptPickerHint') || {}).textContent,
      historyCollapsed: q('#moptHistoryPanel') ? !q('#moptHistoryPanel').open : null,
      historyHint: (q('#moptHistoryHint') || {}).textContent,
      activeJobs: document.querySelectorAll('#moptActiveJobs .moptJob').length,
      historyJobs: document.querySelectorAll('#moptJobs .moptJob').length,
      stats: document.querySelectorAll('.moptStat').length,
      results: document.querySelectorAll('.moptItem').length,
      bodyScrollsSideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1
    };
  });

  console.log(`\n=== dashboard ${vp.name} (${vp.width}px) ===`);
  const check = (c, m) => { console.log(`  ${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failures++; };
  check(r.statusCollapsed === true, 'status is collapsed to one line when healthy');
  check(/Everything is working/.test(r.statusLine || ''), `status line summarises (${r.statusLine})`);
  check(r.pickerOpen === true, 'the file picker is open by default');
  check(/6 shown/.test(r.pickerHint || ''), `picker summarises its contents (${r.pickerHint})`);
  check(r.historyCollapsed === true, 'history is tucked into a collapsed panel');
  check(/2 finished/.test(r.historyHint || ''), `history summarises its contents (${r.historyHint})`);
  check(r.activeJobs === 1, `running jobs stay visible (${r.activeJobs})`);
  check(r.historyJobs === 2, `finished jobs move into history (${r.historyJobs})`);
  check(r.stats >= 4, `statistics render (${r.stats} tiles)`);
  check(r.results === 6, `file list renders (${r.results})`);
  check(!r.bodyScrollsSideways, 'page does not scroll sideways');
  check(logs.length === 0, `no page errors${logs.length ? ': ' + logs[0] : ''}`);

  // Jellyfin fires pageshow again every time the user navigates back to this page, reusing the
  // same element. Handlers bound on each pageshow stacked up, so after two visits one click on
  // "pause" sent two requests -- pausing and then immediately unpausing.
  const repeated = await page.evaluate(async () => {
    document.querySelector('#MediaOptimizerQueuePage').dispatchEvent(new Event('pageshow'));
    await new Promise(r => setTimeout(r, 300));
    window.__calls.length = 0;
    document.querySelector('#moptPause').click();
    await new Promise(r => setTimeout(r, 300));
    return window.__calls.filter(c => c.includes('Queue/Pause') || c.includes('Queue/Resume')).length;
  });
  check(repeated === 1, `one click still sends one request after re-navigating (sent ${repeated})`);

  fs.mkdirSync(path.join(here, 'shots'), { recursive: true });
  await page.screenshot({ path: path.join(here, 'shots', `dashboard-${vp.name}.png`), fullPage: true });
  await ctx.close();
}

await browser.close();
console.log(failures === 0 ? '\nAll dashboard checks passed.' : `\n${failures} dashboard check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
