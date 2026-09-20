/*
 * Renders the Move Media dashboard page in a real browser with stubbed Jellyfin globals.
 *
 * The page is the only place a whole library can be sent to another drive, so the things worth
 * proving here are the ones that decide whether that is safe: the drive cards render with their
 * free space, the preview turns a selection into an explicit "this many files, this much data,
 * this destination", and a plan the server refuses leaves the Move button disabled.
 */
import { launchChromium } from './browser.mjs';
import fs from 'node:fs'; import path from 'node:path'; import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const page_html = fs.readFileSync(
    path.join(here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Configuration', 'movePage.html'), 'utf8');

const GB = 1024 ** 3;

const LOCATIONS = [
    { Path: 'D:\\media\\movies', LibraryName: 'Movies', CollectionType: 'movies', Exists: true,
      IsWritable: true, TotalBytes: 500 * GB, FreeBytes: 12 * GB, ItemCount: 84, ItemBytes: 440 * GB },
    { Path: 'E:\\media\\movies', LibraryName: 'Movies', CollectionType: 'movies', Exists: true,
      IsWritable: true, TotalBytes: 2000 * GB, FreeBytes: 1700 * GB, ItemCount: 12, ItemBytes: 260 * GB },
    { Path: 'F:\\archive', LibraryName: null, CollectionType: null, Exists: true, IsWritable: true,
      IsExtraTarget: true, TotalBytes: 4000 * GB, FreeBytes: 3900 * GB, ItemCount: 0, ItemBytes: 0 }
];

const ITEMS = Array.from({ length: 5 }, (_, i) => ({
    Id: 'id' + i, Name: 'Film ' + (i + 1) + ' (2021)', Type: 'Movie',
    Path: 'D:\\media\\movies\\Film ' + (i + 1) + '\\film.mkv', Container: 'mkv',
    SizeBytes: (6 - i) * GB, Height: 2160, VideoCodec: 'hevc'
}));

const PLAN = {
    DestinationPath: 'E:\\media\\movies', MovableCount: 2, SkippedCount: 1, TotalBytes: 11 * GB,
    DestinationFreeBytes: 1700 * GB, FreeBytesAfter: 1689 * GB, IsRunnable: true,
    Blockers: [], Notes: ['Each file is copied to the new drive and checked before the original is deleted.'],
    Items: [
        { ItemId: 'id0', Name: 'Film 1 (2021)', SourcePath: 'D:\\media\\movies\\Film 1\\film.mkv',
          DestinationPath: 'E:\\media\\movies\\Film 1\\film.mkv', SizeBytes: 6 * GB, CanMove: true },
        { ItemId: 'id1', Name: 'Film 2 (2021)', SourcePath: 'D:\\media\\movies\\Film 2\\film.mkv',
          DestinationPath: 'E:\\media\\movies\\Film 2\\film.mkv', SizeBytes: 5 * GB, CanMove: true },
        { ItemId: 'id2', Name: 'Film 3 (2021)', SourcePath: 'D:\\media\\movies\\Film 3\\film.mkv',
          CanMove: false, SkippedReason: 'A conversion is queued or running for this file.' }
    ]
};

const JOBS = [
    { Id: 'm1', ItemName: 'Film 1 (2021)', Status: 'Copying', ProgressPercent: 38,
      SourcePath: 'D:\\media\\movies\\Film 1\\film.mkv', DestinationPath: 'E:\\media\\movies\\Film 1\\film.mkv',
      SizeBytes: 6 * GB, BytesCopied: 2.3 * GB, BytesPerSecond: 110 * 1024 * 1024, EtaSeconds: 36 },
    { Id: 'm2', ItemName: 'Film 9 (2019)', Status: 'Completed', SourcePath: 'D:\\media\\movies\\Film 9\\film.mkv',
      DestinationPath: 'E:\\media\\movies\\Film 9\\film.mkv', SizeBytes: 4 * GB, HashVerified: true,
      CompanionFilesMoved: 3 },
    { Id: 'm3', ItemName: 'Film 4 (2020)', Status: 'Failed', SourcePath: 'D:\\media\\movies\\Film 4\\film.mkv',
      DestinationPath: 'E:\\media\\movies\\Film 4\\film.mkv', Error: 'Not enough free space on the destination.' }
];

const stub = ({ locations, items, plan, jobs }) => {
    window.Dashboard = { alert: () => {}, confirm: () => Promise.resolve(), showLoadingMsg: () => {}, hideLoadingMsg: () => {} };
    window.ApiClient = {
        getUrl: p => '/' + p,
        ajax: o => {
            const u = o.url;
            if (u.includes('Move/Locations')) return Promise.resolve(JSON.stringify(locations));
            if (u.includes('Move/Preview')) return Promise.resolve(JSON.stringify(plan));
            if (u.includes('Move/Jobs')) return Promise.resolve(JSON.stringify(jobs));
            if (u.includes('Library/Search')) return Promise.resolve(JSON.stringify(items));
            return Promise.resolve('{}');
        }
    };
};

let failures = 0;
const browser = await launchChromium();

for (const vp of [{ name: 'desktop', width: 1280, height: 1000 }, { name: 'mobile', width: 412, height: 915 }]) {
    const ctx = await browser.newContext({ viewport: vp });
    const page = await ctx.newPage();
    const logs = [];
    page.on('pageerror', e => logs.push('PAGEERROR: ' + e.message));

    await page.setContent('<!doctype html><html><head></head><body style="background:#0f0f0f;color:#eee;font-family:system-ui"></body></html>');
    await page.evaluate(stub, { locations: LOCATIONS, items: ITEMS, plan: PLAN, jobs: JOBS });

    await page.evaluate(({ locations, items, plan, jobs, html, stubSource }) => {
        // eslint-disable-next-line no-eval
        eval('(' + stubSource + ')')({ locations, items, plan, jobs });
        const body = html.slice(html.indexOf('<div id="MediaOptimizerMovePage"'), html.lastIndexOf('</div>') + 6);
        document.body.innerHTML = body;
        document.querySelectorAll('script').forEach(old => {
            const s = document.createElement('script');
            s.textContent = old.textContent;
            old.replaceWith(s);
        });
        document.querySelector('#MediaOptimizerMovePage').dispatchEvent(new Event('pageshow'));
    }, { locations: LOCATIONS, items: ITEMS, plan: PLAN, jobs: JOBS, html: page_html, stubSource: stub.toString() });

    await page.waitForTimeout(700);

    // Pick a destination and a file, exactly as someone would.
    await page.evaluate(() => {
        document.querySelectorAll('.movLoc')[1].querySelector('button').click();
        document.querySelector('#movSelectAll').click();
    });
    await page.waitForTimeout(600);

    const r = await page.evaluate(() => {
        const q = s => document.querySelector(s);
        return {
            locations: document.querySelectorAll('.movLoc').length,
            chosen: document.querySelectorAll('.movLoc.dest').length,
            freeSpaceShown: Array.from(document.querySelectorAll('.movLocMeta'))
                .some(n => /free of/.test(n.textContent)),
            results: document.querySelectorAll('.movItem').length,
            selection: (q('#movSelectionInfo') || {}).textContent,
            previewOk: (document.querySelector('.movLine.ok') || {}).textContent || '',
            skips: document.querySelectorAll('.movSkip').length,
            startLabel: (q('#movStart') || {}).textContent,
            startDisabled: q('#movStart') ? q('#movStart').disabled : null,
            activeJobs: document.querySelectorAll('#movActive .movJob').length,
            historyJobs: document.querySelectorAll('#movHistory .movJob').length,
            progressBars: document.querySelectorAll('#movActive .movBar').length,
            bodyScrollsSideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1
        };
    });

    console.log(`\n=== move page ${vp.name} (${vp.width}px) ===`);
    const check = (c, m) => { console.log(`  ${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failures++; };
    check(r.locations === 3, `every drive is listed (${r.locations})`);
    check(r.freeSpaceShown, 'each drive shows how full it is');
    check(r.chosen === 1, 'the chosen destination is marked');
    check(r.results === 5, `the file list renders (${r.results})`);
    check(/5 selected/.test(r.selection || ''), `selecting all reports the total (${r.selection})`);
    check(/2 files/.test(r.previewOk), `the preview states what would move (${r.previewOk})`);
    check(/E:\\media\\movies/.test(r.previewOk), 'the preview names the destination');
    check(r.skips === 1, `files that cannot move are listed with a reason (${r.skips})`);
    check(/Move 2 files/.test(r.startLabel || ''), `the button says what it will do (${r.startLabel})`);
    check(r.startDisabled === false, 'a runnable plan enables the button');
    check(r.activeJobs === 1, `running moves stay visible (${r.activeJobs})`);
    check(r.historyJobs === 2, `finished moves go to history (${r.historyJobs})`);
    check(r.progressBars === 1, 'a copy in flight shows a progress bar');
    check(!r.bodyScrollsSideways, 'page does not scroll sideways');
    check(logs.length === 0, `no page errors${logs.length ? ': ' + logs[0] : ''}`);

    fs.mkdirSync(path.join(here, 'shots'), { recursive: true });
    await page.screenshot({ path: path.join(here, 'shots', `movepage-${vp.name}.png`), fullPage: true });
    await ctx.close();
}

await browser.close();
console.log(failures === 0 ? '\nAll move page checks passed.' : `\n${failures} move page check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
