/*
 * The settings page, which had no test at all.
 *
 * Everything here is about the round trip: a value the server sent has to appear in the right
 * control, and a value the user typed has to arrive back at the server as the right type. The
 * failures this catches are silent — a checkbox saved as the string "on", a number saved as text,
 * a cleared box saved as zero, or a setting the page does not show being wiped by a save that
 * does not know about it, which for the rules would mean deleting every rule somebody wrote.
 */
import { JSDOM } from 'jsdom';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const pagePath = path.join(
    here, '..', 'Jellyfin.Plugin.MediaOptimizer', 'Configuration', 'configPage.html');
const html = fs.readFileSync(pagePath, 'utf8');

let failures = 0;
const ok = (condition, message) => {
    console.log((condition ? '  ok   ' : '  FAIL ') + message);
    if (!condition) { failures++; }
};
const tick = (ms = 40) => new Promise(resolve => setTimeout(resolve, ms));

// What the server would send back: every field set to something distinguishable, plus a rule,
// which the page never shows.
const stored = {
    Injection: 'FileTransformation',
    DefaultContainer: 'mkv',
    DefaultOutputPolicy: 'Replace',
    SidecarDirectory: '/media/optimized',
    TempDirectory: '/scratch/work',
    QuarantineDirectory: '/scratch/quarantine',
    QuarantineRetentionDays: 14,
    MaxConcurrentJobs: 2,
    EncodingThreadCount: 0,
    FileStabilitySeconds: 120,
    FreeSpaceSafetyFactor: 1.5,
    JobHistoryLimit: 200,
    KeepAudioLanguages: 'eng, jpn',
    KeepSubtitleLanguages: 'eng',
    Speed: 'Balanced',
    RemoveFinishedJobsAfterDays: 30,
    PauseWhilePlaybackActive: true,
    LowProcessPriority: false,
    DeepVerifyBeforeReplace: false,
    AllowNonAdminAnalysis: true,
    KeepUntaggedTracks: true,
    DropCommentaryTracks: false,
    PreferHardwareEncoding: false,
    ResumeJobsAfterRestart: true,
    RegenerateTrickplayAfterReplace: true,
    KeepOriginalsBesideMedia: false,
    EncodeBesideMedia: true,
    NotifyOnCompletion: true,
    NotifyOnFailure: true,
    MeasureQualityWhenSampling: true,
    Rules: [{ Id: 'r1', Name: 'The anime library', Enabled: true, MaxItemsPerRun: 3 }]
};

const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', {
    runScripts: 'outside-only',
    url: 'http://localhost:8096/web/'
});
const { window } = dom;
const doc = window.document;

let saved = null;

const alerts = [];
let spinner = 0;

window.Dashboard = {
    showLoadingMsg: () => { spinner++; },
    hideLoadingMsg: () => { spinner--; },
    processPluginConfigurationUpdateResult: () => {},
    alert: options => { alerts.push(options); }
};

window.ApiClient = {
    getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(stored))),
    updatePluginConfiguration: (id, config) => {
        saved = config;
        return Promise.resolve({ MessageType: 'ok' });
    }
};

// The page is a fragment: take its body and run its script the way Jellyfin does.
const body = html.slice(html.indexOf('<div id="MediaOptimizerConfigPage"'), html.lastIndexOf('</body>'));
doc.body.innerHTML = body;

const scripts = Array.from(doc.querySelectorAll('script')).map(s => s.textContent);
for (const source of scripts) {
    window.eval(source);
}

console.log('\n=== the settings page loads what the server sent ===');

doc.querySelector('#MediaOptimizerConfigPage').dispatchEvent(new window.Event('pageshow'));
await tick();

const value = id => {
    const input = doc.querySelector('#' + id);
    return input ? (input.type === 'checkbox' ? input.checked : input.value) : undefined;
};

ok(value('TempDirectory') === '/scratch/work', `a path field is populated (got: ${value('TempDirectory')})`);
ok(value('MaxConcurrentJobs') === '2', `a number field is populated (got: ${value('MaxConcurrentJobs')})`);
ok(value('KeepAudioLanguages') === 'eng, jpn', 'a list field is populated');
ok(value('PauseWhilePlaybackActive') === true, 'a checkbox that is on shows as on');
ok(value('LowProcessPriority') === false, 'a checkbox that is off shows as off');
ok(value('MeasureQualityWhenSampling') === true, 'the quality-measurement checkbox is populated too');

// Every control the page renders for a setting has to have been filled in by the load, or it
// shows a default the server never sent — and then saves it.
const rendered = Array.from(doc.querySelectorAll('[id]'))
    .filter(el => Object.prototype.hasOwnProperty.call(stored, el.id));
const unpopulated = rendered
    .filter(el => (el.type === 'checkbox' ? el.checked !== !!stored[el.id] : String(el.value) !== String(stored[el.id])))
    .map(el => el.id);
ok(unpopulated.length === 0, `every control shows the stored value (${unpopulated.join(', ') || 'all match'})`);

console.log('\n=== and saves what the user changed, as the right type ===');

doc.querySelector('#DeepVerifyBeforeReplace').checked = true;
doc.querySelector('#MaxConcurrentJobs').value = '4';
doc.querySelector('#TempDirectory').value = '/faster/disk';

// A cleared number box: the user did not mean "zero days".
doc.querySelector('#QuarantineRetentionDays').value = '';

doc.querySelector('#MediaOptimizerConfigForm').dispatchEvent(
    new window.Event('submit', { bubbles: true, cancelable: true }));
await tick(80);

ok(saved !== null, 'the form saved something');
ok(saved && saved.DeepVerifyBeforeReplace === true, 'a ticked checkbox saves as true, not "on"');
ok(saved && saved.MaxConcurrentJobs === 4, `a number saves as a number (got: ${JSON.stringify(saved && saved.MaxConcurrentJobs)})`);
ok(saved && saved.TempDirectory === '/faster/disk', 'a typed path saves');
ok(saved && saved.QuarantineRetentionDays === 14,
    `a cleared number box leaves the setting alone rather than saving zero (got: ${JSON.stringify(saved && saved.QuarantineRetentionDays)})`);

// The page does not show the rules, so a save must not be able to delete them.
ok(saved && Array.isArray(saved.Rules) && saved.Rules.length === 1,
    'settings the page does not show survive a save');
ok(saved && saved.Rules[0].Name === 'The anime library', 'and survive intact');

console.log('\n=== and says so when the server does not answer ===');

// A save that fails silently is indistinguishable from one that worked: the spinner stops, the
// page looks the same, and the settings are the old ones. Both calls here used to have no
// rejection handler at all, so a failure also left Jellyfin's loading overlay up for ever.
alerts.length = 0;
spinner = 0;
window.ApiClient.updatePluginConfiguration = () =>
    Promise.reject({ status: 500, statusText: 'Internal Server Error' });

doc.querySelector('#MediaOptimizerConfigForm').dispatchEvent(
    new window.Event('submit', { bubbles: true, cancelable: true }));
await tick(80);

ok(alerts.length === 1, `a save that fails says so (${alerts.length} message(s))`);
ok(/Nothing was saved/.test((alerts[0] || {}).title || ''),
    `and says plainly that nothing changed (${(alerts[0] || {}).title})`);
ok(/Internal Server Error/.test((alerts[0] || {}).message || ''),
    'and quotes what the server said');
ok(spinner <= 0, `and does not leave the loading overlay up (${spinner} outstanding)`);

alerts.length = 0;
spinner = 0;
window.ApiClient.getPluginConfiguration = () =>
    Promise.reject({ status: 503, statusText: 'Service Unavailable' });

doc.querySelector('#MediaOptimizerConfigPage').dispatchEvent(new window.Event('pageshow'));
await tick(80);

ok(alerts.length === 1, `settings that cannot be read say so (${alerts.length} message(s))`);
ok(/defaults/.test((alerts[0] || {}).message || ''),
    'and warn that the form is showing defaults, not the saved settings');
ok(spinner <= 0, `and the overlay comes down here too (${spinner} outstanding)`);

console.log(failures === 0 ? '\nAll settings-page checks passed.' : `\n${failures} settings-page check(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
