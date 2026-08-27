/*
 * Verifies that the injected client script grafts itself onto real jellyfin-web markup.
 *
 * The web client has no plugin API, so these selectors are private and will move. This test is
 * the early-warning system: when Jellyfin restructures its markup, this fails here rather than
 * silently in someone's browser.
 *
 * Run with:  npm install && npm test
 * Requires the plugin to have been built first, so client.js and client.css exist.
 */
import { JSDOM } from 'jsdom';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const resources = path.join(here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Web', 'Resources');

// Reproduce exactly what ClientAssetController does when it serves the bundle.
const js = fs.readFileSync(path.join(resources, 'client.js'), 'utf8');
const css = fs.readFileSync(path.join(resources, 'client.css'), 'utf8');
const bundle = js.replace('/*__MEDIAOPTIMIZER_CSS__*/', JSON.stringify(css));

const osd = fs.readFileSync(path.join(here, 'fixtures', 'videoosd-10.11.0.html'), 'utf8');

let failures = 0;
const ok = (condition, message) => {
    if (condition) {
        console.log('  ok   ' + message);
    } else {
        console.log('  FAIL ' + message);
        failures++;
    }
};
const tick = (ms = 60) => new Promise(resolve => setTimeout(resolve, ms));

const itemId = '0f9e8d7c6b5a49382716253443210fed';

const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', {
    runScripts: 'outside-only',
    pretendToBeVisual: true,
    url: 'http://localhost:8096/web/'
});
const { window } = dom;
const doc = window.document;

// A stand-in for Jellyfin's ApiClient: just enough for the script to boot and issue requests.
const calls = [];
window.ApiClient = {
    getUrl: p => 'http://localhost:8096/' + p,
    deviceId: () => 'test-device',
    serverInfo: () => ({ Id: 'srv' }),
    ajax: options => {
        calls.push(options);
        if (options.url.includes('Analyze')) {
            return Promise.resolve(JSON.stringify({
                ItemId: itemId, Name: 'Test', Path: '/m/t.mkv', Container: 'mkv',
                SizeBytes: 1000, DurationSeconds: 60, IsEligible: true, IsWritable: true,
                OverallBitrate: {}, Audio: [], Subtitles: [], RecommendedStrategy: 'Standard',
                Video: { Index: 0, Codec: 'h264', Width: 1920, Height: 1080, Bitrate: {} }
            }));
        }
        if (options.url.includes('Capabilities')) {
            return Promise.resolve(JSON.stringify({
                VideoEncoders: [], AudioEncoders: [], Containers: ['mkv'], CanConvert: true
            }));
        }
        return Promise.resolve('{}');
    }
};

window.eval(bundle);
await tick();

// --- The action sheet, built to match src/components/actionSheet/actionSheet.ts -------------
console.log('\nContext menu (3-dot / right-click)');

const sheet = doc.createElement('div');
sheet.className = 'actionSheet actionsheet-not-fullscreen';
sheet.innerHTML =
    '<div class="actionSheetContent">' +
    '<div class="actionSheetScroller">' +
    '<button is="emby-button" type="button" class="listItem listItem-button actionSheetMenuItem" data-id="play">' +
    '<div class="listItemBody actionsheetListItemBody">' +
    '<div class="listItemBodyText actionSheetItemText">Play</div></div></button>' +
    '</div></div>';

// The sheet does not carry the item it was opened against, so the script captures the id from
// the element that was clicked. Simulate that click first.
const card = doc.createElement('div');
card.setAttribute('data-id', itemId);
doc.body.appendChild(card);
// MouseEvent rather than PointerEvent: jsdom does not implement the latter, and the script
// listens by event type name, so this exercises the same code path.
card.dispatchEvent(new window.MouseEvent('pointerdown', { bubbles: true }));

doc.body.appendChild(sheet);
await tick();

const entry = sheet.querySelector('[data-id="mediaoptimizer"]');
ok(!!entry, 'entry is grafted into .actionSheetScroller');
ok(entry?.classList.contains('actionSheetMenuItem'), 'entry reuses jellyfin-web item classes, so themes apply');
ok(!!entry?.querySelector('.listItemBodyText.actionSheetItemText'), 'entry matches the native inner structure');
ok(entry?.textContent.includes('Optimize'), 'entry is labelled');

sheet.appendChild(doc.createElement('span'));
await tick();
ok(sheet.querySelectorAll('[data-id="mediaoptimizer"]').length === 1, 'further mutations do not duplicate the entry');

// --- The player OSD, from the real v10.11.0 template -----------------------------------------
console.log('\nPlayer OSD button');

const player = doc.createElement('div');
player.innerHTML = osd.replace(/\$\{[A-Za-z]+\}/g, 'x');
doc.body.appendChild(player);
await tick();

const button = player.querySelector('.btnMediaOptimizer');
ok(!!button, 'button is grafted into .videoOsdBottom .buttons');
ok(button?.nextElementSibling?.classList.contains('btnVideoOsdSettings'), 'button sits immediately before Settings');
ok(!!button?.querySelector('.material-icons.tune'), 'button renders its icon');
ok(player.querySelectorAll('.btnMediaOptimizer').length === 1, 'button is not duplicated');

// --- Opening the dialog ----------------------------------------------------------------------
console.log('\nDialog');

calls.length = 0;
entry.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await tick(80);

// The dialog renders inside a shadow root, which is what makes it immune to jellyfin-web's CSS.
const shadow = window.MediaOptimizer.shadowRoot();
ok(!!shadow, 'dialog is rendered inside a shadow root, isolated from host styles');
ok(!!shadow?.querySelector('.mopt-overlay'), 'clicking the entry opens the dialog');
ok(calls.some(c => c.url.includes('MediaOptimizer/Analyze/' + itemId)), 'analysis is requested for the clicked item');
ok(calls.some(c => c.url.includes('MediaOptimizer/Capabilities')), 'server ffmpeg capabilities are requested');

doc.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
await tick(40);
ok(!window.MediaOptimizer.shadowRoot(), 'Escape closes the dialog');

console.log(failures === 0 ? '\nAll DOM checks passed.' : `\n${failures} DOM check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
