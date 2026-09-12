/*
 * What the dialog leaves behind when it closes.
 *
 * The dialog polls the server while a conversion runs. Closing it with the X, with Escape or by
 * clicking the backdrop all have to stop that poll: a page left open in a browser tab would
 * otherwise keep asking the server for a job that finished hours ago, once every second and a
 * half, for as long as the tab lives.
 *
 * Also checks the keyboard contract, which cannot be seen by looking at the rendered page: focus
 * enters the dialog when it opens and goes back where it came from when it closes.
 */
import { JSDOM } from 'jsdom';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const resources = path.join(here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Web', 'Resources');

const js = fs.readFileSync(path.join(resources, 'client.js'), 'utf8');
const css = fs.readFileSync(path.join(resources, 'client.css'), 'utf8');
const bundle = js.replace('/*__MEDIAOPTIMIZER_CSS__*/', JSON.stringify(css));

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
const jobId = 'aaaaaaaabbbbccccddddeeeeeeeeeeee';

const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', {
    runScripts: 'outside-only',
    pretendToBeVisual: true,
    url: 'http://localhost:8096/web/'
});
const { window } = dom;
const doc = window.document;

const analysis = {
    ItemId: itemId, Name: 'Test', Path: '/m/t.mkv', Container: 'mkv',
    SizeBytes: 8_000_000_000, DurationSeconds: 5400, IsEligible: true, IsWritable: true,
    OverallBitrate: { Bps: 12_000_000, Source: 'Measured' }, Audio: [], Subtitles: [],
    RecommendedStrategy: 'Standard',
    Video: { Index: 0, Codec: 'h264', Width: 1920, Height: 1080, Bitrate: { Bps: 11_000_000 } }
};

const capabilities = {
    VideoEncoders: [{ Name: 'libx265', DisplayName: 'HEVC (x265)', Codec: 'hevc', Supports10Bit: true, Presets: ['medium'] }],
    AudioEncoders: [{ Name: 'libopus', DisplayName: 'Opus', Codec: 'opus' }],
    Containers: ['mkv', 'mp4'],
    CanConvert: true
};

const resolved = {
    ItemId: itemId, Strategy: 'Standard', Container: 'mkv', Video: 'Encode', VideoCodec: 'libx265',
    RateControl: 'ConstantQuality', Quality: 28, BitDepth: 8, Preset: 'medium',
    AudioTracks: [], KeepAttachments: true, KeepChapters: true, OutputPolicy: 'Replace'
};

const estimate = {
    CurrentSizeBytes: 8_000_000_000, EstimatedSizeBytes: 4_000_000_000,
    EstimatedSizeLowBytes: 3_000_000_000, EstimatedSizeHighBytes: 5_000_000_000,
    SavingFraction: 0.5, Confidence: 'Medium', Warnings: [], IsLossless: false,
    TimeBasis: 'unmeasured'
};

let jobPolls = 0;

window.ApiClient = {
    getUrl: p => 'http://localhost:8096/' + p,
    deviceId: () => 'test-device',
    serverInfo: () => ({ Id: 'srv' }),
    ajax: options => {
        const url = options.url;
        if (url.includes('MediaOptimizer/Jobs/' + jobId)) {
            jobPolls++;
            return Promise.resolve(JSON.stringify({
                Id: jobId, Status: 'Encoding', ProgressPercent: 12, Speed: 1.5, EtaSeconds: 900
            }));
        }
        if (url.includes('MediaOptimizer/Jobs')) {
            return Promise.resolve(JSON.stringify({ Id: jobId, Status: 'Queued' }));
        }
        if (url.includes('Analyze')) { return Promise.resolve(JSON.stringify(analysis)); }
        if (url.includes('Capabilities')) { return Promise.resolve(JSON.stringify(capabilities)); }
        if (url.includes('ResolveStrategy')) { return Promise.resolve(JSON.stringify(resolved)); }
        if (url.includes('Estimate')) { return Promise.resolve(JSON.stringify(estimate)); }
        return Promise.resolve('{}');
    }
};

window.eval(bundle);
await tick();

// --- focus ------------------------------------------------------------------------------------
console.log('\nKeyboard focus');

const opener = doc.createElement('button');
opener.textContent = 'Optimize…';
doc.body.appendChild(opener);
opener.focus();

window.MediaOptimizer.open(itemId);
await tick(120);

const shadow = window.MediaOptimizer.shadowRoot();
ok(!!shadow, 'the dialog opened');
ok(doc.activeElement !== opener, 'focus leaves the page behind the dialog');
ok(shadow.activeElement === shadow.querySelector('.mopt-dialog'),
    'the dialog itself takes focus, so the next Tab lands inside it');

doc.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
await tick(60);

ok(!window.MediaOptimizer.shadowRoot(), 'Escape closes the dialog');
ok(doc.activeElement === opener, 'focus returns to whatever opened the dialog');

// --- the progress poll --------------------------------------------------------------------------
console.log('\nProgress polling stops with the dialog');

window.MediaOptimizer.open(itemId);
await tick(400);

const root = window.MediaOptimizer.shadowRoot();
const start = Array.prototype.find.call(root.querySelectorAll('button'),
    b => b.textContent.includes('Start conversion'));
ok(!!start, 'the form offers to start a conversion');
ok(start && !start.disabled, 'the button is enabled once the estimate arrives');

start.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await tick(200);

ok(jobPolls > 0, 'the queued job is polled while the dialog is open (polls: ' + jobPolls + ')');

// Close the way a user does when they have seen enough: the X in the header, which is not the
// "Close" button the progress view adds and which used to leave the poll running forever.
const closeButton = root.querySelector('.mopt-close');
ok(!!closeButton, 'the header close button exists');
closeButton.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await tick(60);

ok(!window.MediaOptimizer.shadowRoot(), 'the close button closes the dialog');

const pollsAtClose = jobPolls;
await tick(3600);
ok(jobPolls === pollsAtClose,
    `polling stops when the dialog closes (${pollsAtClose} before, ${jobPolls} after two more intervals)`);

console.log(failures === 0 ? '\nAll lifecycle checks passed.' : `\n${failures} lifecycle check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
