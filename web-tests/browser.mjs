/*
 * Resolves a Chromium for the browser-based tests.
 *
 * These two environments need opposite things, which is what broke CI:
 *
 *  - A GitHub runner installs Playwright's own browser with `playwright install`, and Playwright
 *    already knows where that is. Passing an executablePath there is wrong.
 *  - The development sandbox ships a preinstalled Chromium under PLAYWRIGHT_BROWSERS_PATH and
 *    blocks the download, so it must be pointed at explicitly — and the version directory is not
 *    the one Playwright expects, so its own lookup fails.
 *
 * So: use an explicit path only when one is actually there, otherwise let Playwright decide.
 */
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

/**
 * Finds a Chromium binary to launch.
 * @returns {string | undefined} An executable path, or undefined to use Playwright's own.
 */
export function resolveChromium() {
    if (process.env.MEDIAOPTIMIZER_CHROMIUM) {
        return process.env.MEDIAOPTIMIZER_CHROMIUM;
    }

    const root = process.env.PLAYWRIGHT_BROWSERS_PATH;
    if (!root || !fs.existsSync(root)) {
        return undefined;
    }

    // Directory names carry a build number that moves, so match on the prefix.
    for (const entry of fs.readdirSync(root)) {
        if (!entry.startsWith('chromium-')) {
            continue;
        }

        for (const relative of [['chrome-linux', 'chrome'], ['chrome-win', 'chrome.exe'],
                                ['chrome-mac', 'Chromium.app', 'Contents', 'MacOS', 'Chromium']]) {
            const candidate = path.join(root, entry, ...relative);
            if (fs.existsSync(candidate)) {
                return candidate;
            }
        }
    }

    return undefined;
}

/**
 * Launches Chromium, using a local binary when one is present.
 * @param {object} options Playwright launch options.
 * @returns {Promise<import('playwright').Browser>} The browser.
 */
export async function launchChromium(options = {}) {
    const executablePath = resolveChromium();

    try {
        return await chromium.launch(executablePath ? { ...options, executablePath } : options);
    } catch (error) {
        // A missing browser is a setup problem, not a test failure; say which one it is.
        throw new Error(
            'Could not launch Chromium for the UI tests.\n' +
            (executablePath
                ? `Tried the local binary at ${executablePath}.\n`
                : 'No local binary found, so Playwright\'s own was used.\n') +
            'In CI, run "npx playwright install --with-deps chromium" first.\n' +
            'Elsewhere, set MEDIAOPTIMIZER_CHROMIUM to a Chromium executable.\n\n' +
            String(error && error.message ? error.message : error));
    }
}
