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
  IsWatched: i % 2 === 0,
  // One of them is already converting: its row offers to show that rather than to start another.
  HasActiveJob: i === 5,
  PotentialSavingBytes: i < 2 ? 1.8 * 1024 ** 3 : null,
  SavingBasis: i < 2 ? '1440p instead of 2160p' : null
}));
const JOBS = [
  { Id: 'j1', ItemName: 'Film 6', Status: 'Encoding', ProgressPercent: 43, Speed: 1.8,
    EtaSeconds: 4200, OutputPolicy: 'Replace', SourceSizeBytes: 5 * 1024 ** 3 },
  { Id: 'j2', ItemName: 'Film 9', Status: 'Completed', OutputPolicy: 'Replace',
    SourceSizeBytes: 6 * 1024 ** 3, OutputSizeBytes: 3 * 1024 ** 3, QuarantinePath: '/media/f9.mkv.mooriginal',
    QualityMetric: 'SSIM', QualityScore: 0.9831,
    QualityNote: 'measured SSIM 0.9831 at its worst across 3 point(s) of the finished file: very hard to tell apart from the source (average 0.9880)' },
  { Id: 'j3', ItemName: 'Film 4', Status: 'Failed', OutputPolicy: 'Replace', Error: 'FFmpeg failed: no space left' },
  { Id: 'j4', ItemName: 'Film 12', Status: 'Completed', OutputPolicy: 'Sidecar',
    SourceSizeBytes: 8 * 1024 ** 3, OutputSizeBytes: 4 * 1024 ** 3, IsLossless: true,
    LosslessVerified: true, QueuedByRule: 'Big 4K films' }
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
    MinSizeMb: 20480, LibraryName: 'Films', Watched: 'Watched', AddedMoreThanDaysAgo: 30, Strategy: 'Medium',
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
        if (u.includes('Library/Facets')) return Promise.resolve(JSON.stringify({ containers: ['mkv', 'mp4'], codecs: ['hevc', 'h264'], libraries: ['Films', 'TV'] }));
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
    window.__alerts = [];
    window.Dashboard = {
      alert: options => { window.__alerts.push(options); },
      // A declined confirmation rejects, exactly as it does in Jellyfin, which is what makes
      // "the user said no" and "the request failed" land in the same place.
      confirm: () => (window.__declineConfirm ? Promise.reject() : Promise.resolve()),
      showLoadingMsg: () => {},
      hideLoadingMsg: () => {}
    };
    window.__calls = [];
    window.ApiClient = {
      getUrl: p => '/' + p,
      ajax: o => {
        const u = o.url;
        window.__calls.push((o.type || 'GET') + ' ' + u);
        if (u.includes('Diagnostics')) return Promise.resolve(JSON.stringify(diag));
        if (u.includes('Library/Facets')) return Promise.resolve(JSON.stringify({ containers: ['mkv', 'mp4'], codecs: ['hevc', 'h264'], libraries: ['Films', 'TV'] }));
        if (u.includes('Library/Search')) return Promise.resolve(JSON.stringify(items));
        if (u.includes('Statistics')) {
          return window.__failStats
            ? Promise.reject({ status: 503, statusText: 'Service Unavailable' })
            : Promise.resolve(JSON.stringify(stats));
        }
        if (u.includes('Queue/ReleaseQuarantine')) {
          return window.__failRelease
            ? Promise.reject({ status: 500, statusText: 'Internal Server Error' })
            : Promise.resolve(JSON.stringify({ released: 3, freedBytes: 15 * 1024 ** 3 }));
        }
        if (u.endsWith('Rules/Preview')) {
          return Promise.resolve(JSON.stringify({
            DryRun: true, Considered: 912, Queued: 2, EstimatedSavingBytes: 26 * 1024 ** 3,
            Items: [
              { RuleId: 'r1', RuleName: 'Big 4K films', ItemId: 'i1', Name: 'A Long Film (2016)',
                Queued: true, EstimatedSavingBytes: 22 * 1024 ** 3 },
              { RuleId: 'r2', RuleName: 'Old DVD rips', ItemId: 'i9', Name: 'An Old Rip (1998)',
                Queued: true, EstimatedSavingBytes: 4 * 1024 ** 3 },
              { RuleId: 'r2', RuleName: 'Old DVD rips', ItemId: 'i8', Name: 'Already Done (2011)',
                Queued: false, SkippedReason: 'Already converted by this plugin.' }
            ]
          }));
        }
        if (u.includes('Rules/') && u.includes('Preview')) return Promise.resolve(JSON.stringify(preview));
        if (u.includes('Rules/') && u.includes('/Move')) {
          // The server swaps the rule with its neighbour and answers with the new order; the
          // dashboard has to render what came back rather than what it had.
          const id = u.split('Rules/')[1].split('/')[0];
          const at = rules.findIndex(r => r.Id === id);
          const to = u.includes('direction=up') ? at - 1 : at + 1;
          if (at >= 0 && to >= 0 && to < rules.length) {
            const moved = rules[at];
            rules[at] = rules[to];
            rules[to] = moved;
          }

          return Promise.resolve(JSON.stringify(rules));
        }
        if (u.includes('Rules')) return Promise.resolve(JSON.stringify(rules));
        if (u.includes('Jobs')) {
          return window.__failJobs
            ? Promise.reject({ status: 500, statusText: 'Internal Server Error' })
            : Promise.resolve(JSON.stringify(jobs));
        }
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
      itemButtons: Array.from(document.querySelectorAll('.moptItem button'))
        .map(b => b.textContent + (b.disabled ? ' (disabled)' : '')),
      savingLines: Array.from(document.querySelectorAll('.moptItemMeta'))
        .filter(n => n.textContent.includes('to gain')).map(n => n.textContent),
      sortOptions: Array.from(document.querySelectorAll('#moptSort option')).map(o => o.value),
      bodyScrollsSideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
      // What each job row actually says, and what it offers to do about it. These are the rows
      // somebody reads the morning after an overnight run.
      rows: (function () {
        const rows = {};
        for (const box of document.querySelectorAll('.moptJob')) {
          const name = (box.querySelector('.moptJobName') || {}).textContent;
          rows[name] = {
            meta: (box.querySelector('.moptJobMeta') || {}).textContent || '',
            quality: (box.querySelector('.moptJobQuality') || {}).textContent || '',
            error: (box.querySelector('.moptErr') || {}).textContent || '',
            progress: box.querySelector('.moptBar i') ? box.querySelector('.moptBar i').style.width : null,
            actions: Array.from(box.querySelectorAll('.moptActions button')).map(b => b.textContent)
          };
        }

        return rows;
      })()
    };
  });

  console.log(`\n=== dashboard ${vp.name} (${vp.width}px) ===`);
  const check = (c, m) => { console.log(`  ${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failures++; };
  check(r.statusCollapsed === true, 'status is collapsed to one line when healthy');
  check(/Everything is working/.test(r.statusLine || ''), `status line summarises (${r.statusLine})`);
  check(r.pickerOpen === true, 'the file picker is open by default');
  check(/6 shown/.test(r.pickerHint || ''), `picker summarises its contents (${r.pickerHint})`);
  check(r.historyCollapsed === true, 'history is tucked into a collapsed panel');
  check(/3 finished/.test(r.historyHint || ''), `history summarises its contents (${r.historyHint})`);
  check(r.activeJobs === 1, `running jobs stay visible (${r.activeJobs})`);
  check(r.historyJobs === 3, `finished jobs move into history (${r.historyJobs})`);
  check(r.stats >= 4, `statistics render (${r.stats} tiles)`);
  check(r.results === 6, `file list renders (${r.results})`);
  check(r.savingLines.length === 2, `files with something to gain say so (${r.savingLines.length})`);
  check(/≈ 1\.80 GiB to gain · 1440p instead of 2160p/.test(r.savingLines[0] || ''),
    `the gain is shown as an approximation with its reason (${r.savingLines[0]})`);
  check(r.sortOptions[0] === 'SavingDescending', 'the list can be ordered by what there is to gain');
  check(!r.bodyScrollsSideways, 'page does not scroll sideways');
  check(logs.length === 0, `no page errors${logs.length ? ': ' + logs[0] : ''}`);

  // --- what a job row says -------------------------------------------------------------------
  // The counts above say the rows are there. These say what is in them, which is what somebody
  // actually reads the morning after an overnight run.
  const running = r.rows['Film 6'] || {};
  const undoable = r.rows['Film 9'] || {};
  const failed = r.rows['Film 4'] || {};
  const byRule = r.rows['Film 12'] || {};

  check(running.progress === '43%', `a running job shows its progress (${running.progress})`);
  check(/1\.80× realtime/.test(running.meta), `and how fast it is going (${running.meta})`);
  check(/70 min remaining/.test(running.meta), 'and how long is left');
  check((running.actions || []).includes('Cancel'), 'a running job can be cancelled');

  check(/6\.00 GiB → 3\.00 GiB/.test(undoable.meta), `a finished job shows what it produced (${undoable.meta})`);
  check((undoable.actions || []).includes('Put the original back'),
    'a job still holding its original offers to put it back');
  check(!(undoable.actions || []).includes('Remove from history'),
    'and is not offered a "forget this" that would orphan the original it is keeping');

  check(/no space left/.test(failed.error), `a failed job shows the reason (${failed.error})`);
  check((failed.actions || []).includes('Try again'), 'a failed job can be retried');

  // The only number on this page measured after the conversion rather than predicted before it,
  // so it gets its own line and names the metric: SSIM 0.98 and VMAF 98 are different claims.
  check(/SSIM 0\.9831/.test(undoable.quality),
    `a measured conversion says what it came out looking like (${undoable.quality})`);
  check(/very hard to tell apart/.test(undoable.quality), 'and what that means in words');
  check(byRule.quality === '', 'and a job nobody measured makes no claim about quality');

  check(/queued by "Big 4K films"/.test(byRule.meta), `a job queued by a rule says which (${byRule.meta})`);
  check(/hash verified/.test(byRule.meta), 'and a bit-exact conversion says it was verified');

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
      firstHasNoMoveUp: !Array.from(boxes[0].querySelectorAll('button')).some(b => b.textContent === 'Move up'),
      lastHasNoMoveDown: !Array.from(boxes[boxes.length - 1].querySelectorAll('button')).some(b => b.textContent === 'Move down'),
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

  // The libraries are whatever this server has, so the editor has to take them from the server
  // rather than from a hard-coded list.
  const libraryOptions = await page.evaluate(() => {
    const boxes = Array.from(document.querySelectorAll('#moptRuleEditor .moptFilter'));
    const box = boxes.find(f => f.querySelector('label').textContent === 'Library');
    return box ? Array.from(box.querySelectorAll('option')).map(o => o.textContent) : [];
  });
  check(libraryOptions.length === 3 && libraryOptions.includes('Films') && libraryOptions.includes('TV'),
    `the rule editor offers this server's libraries (${libraryOptions.join(', ')})`);

  check(/2 saved, 1 on/.test(rulesView.hint || ''), `rules panel summarises (${rulesView.hint})`);
  check(rulesView.count === 2, `both rules render (${rulesView.count})`);
  check(/in Films/.test(rulesView.firstDescription) && /20 GB/.test(rulesView.firstDescription)
    && /at most 3 per run/i.test(rulesView.firstDescription),
    `a rule is described in words (${rulesView.firstDescription})`);
  check(rulesView.secondIsOff, 'a rule that is switched off looks switched off');
  check(/7 job\(s\) queued so far/.test(rulesView.firstStats), `a rule reports what it has done (${rulesView.firstStats})`);
  check(/2 file\(s\) would be converted/.test(rulesView.preview), 'preview says how many files it would take');
  check(/Already converted/.test(rulesView.preview), 'preview explains the near-misses too');
  check(/912/.test(rulesView.preview), 'preview says how many items it looked at');
  check(rulesView.firstHasNoMoveUp, 'the first rule is not offered "Move up"');
  check(rulesView.lastHasNoMoveDown, 'the last rule is not offered "Move down"');

  // Which rule is above the other decides which of two overlapping rules converts a file, so the
  // dashboard has to reorder them, not just display them.
  const reordered = await page.evaluate(async () => {
    const before = Array.from(document.querySelectorAll('.moptRuleName')).map(n => n.textContent);
    const first = document.querySelectorAll('.moptRule')[0];
    Array.from(first.querySelectorAll('button')).find(b => b.textContent === 'Move down').click();
    await new Promise(r => setTimeout(r, 300));
    return {
      before: before,
      after: Array.from(document.querySelectorAll('.moptRuleName')).map(n => n.textContent)
    };
  });

  check(reordered.before[0] === reordered.after[1] && reordered.before[1] === reordered.after[0],
    `moving a rule down reorders the list (${reordered.before.join(', ')} -> ${reordered.after.join(', ')})`);

  // What every rule would do tonight, in one answer — and which rule gets which file, since they
  // are applied in order and the first to take a file keeps it.
  const tonight = await page.evaluate(async () => {
    document.querySelector('#moptPreviewRules').click();
    await new Promise(r => setTimeout(r, 500));
    const host = document.querySelector('#moptRulePreview');
    return {
      text: host.textContent,
      rows: Array.from(host.querySelectorAll('.moptPreviewRow')).map(r => r.textContent)
    };
  });

  check(/2 file\(s\) would be converted/.test(tonight.text || ''),
    `tonight's run can be previewed in one go (${(tonight.text || '').slice(0, 60)})`);
  check(tonight.rows.some(r => /Big 4K films/.test(r)) && tonight.rows.some(r => /Old DVD rips/.test(r)),
    'and each file says which rule would take it');
  check(tonight.rows.some(r => /Already converted/.test(r)),
    'with the near-misses explained as well');

  check((r.itemButtons || []).includes('Show progress'),
    `a file already converting offers to show that (${(r.itemButtons || []).join(', ')})`);
  check(!(r.itemButtons || []).some(b => b.includes('(disabled)')),
    'and no row is left with a button that cannot be pressed');

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

  // A read that fails and a queue with nothing in it looked identical: both left the panel empty.
  // That is the one failure on this page that matters, because the person looking at it is
  // usually checking whether an overnight run is happening.
  const failedRead = await page.evaluate(async () => {
    window.__failJobs = true;
    window.__failStats = true;
    document.querySelector('#MediaOptimizerQueuePage').dispatchEvent(new Event('pageshow'));
    await new Promise(r => setTimeout(r, 400));
    return {
      jobs: (document.querySelector('#moptJobs') || {}).textContent || '',
      stats: (document.querySelector('#moptStats') || {}).textContent || '',
      errors: document.querySelectorAll('.moptErr').length
    };
  });

  check(/Could not read the conversion queue/.test(failedRead.jobs),
    `a queue read that fails says so (${failedRead.jobs.slice(0, 80)})`);
  check(/Internal Server Error/.test(failedRead.jobs),
    'and quotes what the server said, so it can be looked up');
  check(/unaffected/.test(failedRead.jobs),
    'and says the queue itself is not the thing that broke');
  check(/Could not read the totals/.test(failedRead.stats),
    `a statistics read that fails says so too (${failedRead.stats.slice(0, 60)})`);
  check(logs.length === 0,
    `and neither leaves an unhandled error behind${logs.length ? ': ' + logs[0] : ''}`);

  // Put the page back into its working state so the screenshot below is of the dashboard rather
  // than of the banner just tested.
  await page.evaluate(async () => {
    window.__failJobs = false;
    window.__failStats = false;
    document.querySelector('#MediaOptimizerQueuePage').dispatchEvent(new Event('pageshow'));
    await new Promise(r => setTimeout(r, 400));
  });

  // "Free up space" deletes every original held for undo. A failure used to say nothing, so the
  // page looked exactly as it does when it worked — on the one action here that cannot be undone.
  const release = await page.evaluate(async () => {
    window.__failRelease = true;
    window.__alerts.length = 0;
    document.querySelector('#moptRelease').click();
    await new Promise(r => setTimeout(r, 400));
    const failed = window.__alerts.map(a => (a.title || '') + ' ' + (a.message || '')).join(' ');

    // And the other half of the same catch: saying no to the confirmation is not a failure.
    window.__declineConfirm = true;
    window.__alerts.length = 0;
    document.querySelector('#moptRelease').click();
    await new Promise(r => setTimeout(r, 400));
    const declined = window.__alerts.length;
    window.__declineConfirm = false;
    window.__failRelease = false;
    return { failed: failed, declined: declined };
  });

  check(/No space was freed/.test(release.failed),
    `a destructive action that fails says so (${release.failed.slice(0, 70)})`);
  check(/Internal Server Error/.test(release.failed), 'and quotes what the server said');
  check(release.declined === 0, `and declining the confirmation stays silent (${release.declined} message(s))`);

  fs.mkdirSync(path.join(here, 'shots'), { recursive: true });
  await page.screenshot({ path: path.join(here, 'shots', `dashboard-${vp.name}.png`), fullPage: true });
  await ctx.close();
}

await browser.close();
console.log(failures === 0 ? '\nAll dashboard checks passed.' : `\n${failures} dashboard check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
