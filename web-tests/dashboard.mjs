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
  IsWatched: i % 2 === 0, HasActiveJob: i === 5,
  PotentialSavingBytes: i < 2 ? 1.8 * 1024 ** 3 : null,
  SavingBasis: i < 2 ? '1440p instead of 2160p' : null
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

const RULES = [
  { Id: 'r1', Name: 'Big 4K films', Enabled: true, Kinds: 'MoviesOnly', MinHeight: 2160,
    MinSizeMb: 20480, Watched: 'Watched', AddedMoreThanDaysAgo: 30, Strategy: 'Medium',
    MaxItemsPerRun: 3, MinSavingPercent: 15, UseHardware: false,
    LastRunAt: '2026-05-30T03:00:00Z', TotalQueued: 7 },
  { Id: 'r2', Name: 'Old DVD rips', Enabled: false, Kinds: 'Everything', VideoCodec: 'mpeg4',
    Strategy: 'Standard', MaxItemsPerRun: 5, MinSavingPercent: 25, UseHardware: true,
    TotalQueued: 0 }
];
const PREVIEW = {
  DryRun: true, Considered: 912, Queued: 2, EstimatedSavingBytes: 31 * 1024 ** 3,
  Items: [
    { RuleId: 'r1', RuleName: 'Big 4K films', ItemId: 'i1', Name: 'A Long Film (2016)', Queued: true, EstimatedSavingBytes: 22 * 1024 ** 3 },
    { RuleId: 'r1', RuleName: 'Big 4K films', ItemId: 'i2', Name: 'Another Film (2019)', Queued: true, EstimatedSavingBytes: 9 * 1024 ** 3 },
    { RuleId: 'r1', RuleName: 'Big 4K films', ItemId: 'i3', Name: 'Already Done (2011)', Queued: false, SkippedReason: 'Already converted by this plugin.' }
  ]
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
  await page.evaluate(({ diag, items, jobs, stats, rules, preview, html }) => {
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
        if (u.includes('Rules/') && u.includes('Preview')) return Promise.resolve(JSON.stringify(preview));
        if (u.includes('Rules')) return Promise.resolve(JSON.stringify(rules));
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
  }, { diag: DIAG, items: ITEMS, jobs: JOBS, stats: STATS, rules: RULES, preview: PREVIEW, html: page_html });

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
      savingLines: Array.from(document.querySelectorAll('.moptItemMeta'))
        .filter(n => n.textContent.includes('to gain')).map(n => n.textContent),
      sortOptions: Array.from(document.querySelectorAll('#moptSort option')).map(o => o.value),
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
  check(r.savingLines.length === 2, `files with something to gain say so (${r.savingLines.length})`);
  check(/≈ 1\.80 GiB to gain · 1440p instead of 2160p/.test(r.savingLines[0] || ''),
    `the gain is shown as an approximation with its reason (${r.savingLines[0]})`);
  check(r.sortOptions[0] === 'SavingDescending', 'the list can be ordered by what there is to gain');
  check(!r.bodyScrollsSideways, 'page does not scroll sideways');
  check(logs.length === 0, `no page errors${logs.length ? ': ' + logs[0] : ''}`);

  const rulesView = await page.evaluate(async () => {
    const panel = document.querySelector('#moptRulesPanel');
    panel.open = true;
    const boxes = Array.from(document.querySelectorAll('.moptRule'));
    const preview = await new Promise(resolve => {
      const buttons = Array.from(boxes[0].querySelectorAll('button'));
      const previewButton = buttons.find(b => b.textContent === 'Preview');
      previewButton.click();
      setTimeout(() => resolve(document.querySelector('#moptRulePreview').textContent), 400);
    });
    return {
      hint: document.querySelector('#moptRulesHint').textContent,
      count: boxes.length,
      firstDescription: boxes[0].querySelector('.moptRuleWhen').textContent,
      secondIsOff: boxes[1].classList.contains('off'),
      firstStats: boxes[0].querySelector('.moptRuleStats').textContent,
      preview: preview,
      editorEmptyBeforeAdding: document.querySelector('#moptRuleEditor').textContent === '',
      fieldsAfterAdding: (document.querySelector('#moptAddRule').click(),
        document.querySelectorAll('#moptRuleEditor .moptFilter').length)
    };
  });

  // Clearing a box that has no "unset" — a rule always has a per-run limit — used to post null
  // into a non-nullable field, which fails model binding before the server's own validation can
  // explain anything. The form restores the default instead.
  const clearedRequiredField = await page.evaluate(() => {
    const labels = Array.from(document.querySelectorAll('#moptRuleEditor .moptFilter'));
    const box = labels.find(f => f.querySelector('label').textContent.includes('Files per run'));
    const input = box.querySelector('input');
    input.value = '';
    input.dispatchEvent(new Event('change'));
    return input.value;
  });
  check(clearedRequiredField === '3', `clearing a required number restores its default (got "${clearedRequiredField}")`);

  check(/2 saved, 1 on/.test(rulesView.hint || ''), `rules panel summarises (${rulesView.hint})`);
  check(rulesView.count === 2, `both rules render (${rulesView.count})`);
  check(/films/.test(rulesView.firstDescription) && /20 GB/.test(rulesView.firstDescription)
    && /at most 3 per run/i.test(rulesView.firstDescription),
    `a rule is described in words (${rulesView.firstDescription})`);
  check(rulesView.secondIsOff, 'a rule that is switched off looks switched off');
  check(/7 job\(s\) queued so far/.test(rulesView.firstStats), `a rule reports what it has done (${rulesView.firstStats})`);
  check(/2 file\(s\) would be converted/.test(rulesView.preview), 'preview says how many files it would take');
  check(/Already converted/.test(rulesView.preview), 'preview explains the near-misses too');
  check(/912/.test(rulesView.preview), 'preview says how many items it looked at');
  check(rulesView.editorEmptyBeforeAdding, 'the rule editor is closed until asked for');
  check(rulesView.fieldsAfterAdding >= 12, `the editor offers the rule's fields (${rulesView.fieldsAfterAdding})`);

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
