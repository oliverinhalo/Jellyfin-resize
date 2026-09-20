/*
 * Media Optimizer — Jellyfin web client integration.
 *
 * Jellyfin has no plugin API for its web client, so this script grafts itself onto the DOM.
 * Two deliberate design decisions follow from that:
 *
 *  1. The dialog renders inside a shadow root. jellyfin-web's stylesheet is broad enough to
 *     reposition arbitrary descendants, which previously collapsed the two-column layout into
 *     one overlapping stack. A shadow root is the only reliable way to be immune to that.
 *  2. Every graft is optional and fails closed. If a selector moves, the buttons vanish but
 *     window.MediaOptimizer.open still works, and the dashboard page keeps using it.
 *
 * Selectors verified against jellyfin-web 10.11.x.
 */
(function () {
    'use strict';

    if (window.__mediaOptimizerLoaded) { return; }
    window.__mediaOptimizerLoaded = true;

    var CSS = /*__MEDIAOPTIMIZER_CSS__*/;
    var GUID_RE = /^[0-9a-f]{32}$|^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

    var state = { lastItemId: null, capabilities: null, warned: {} };

    function warnOnce(key, message) {
        if (state.warned[key]) { return; }
        state.warned[key] = true;
        console.warn('[MediaOptimizer] ' + message);
    }

    // ------------------------------------------------------------------ helpers

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) { node.className = className; }
        if (text !== undefined && text !== null) { node.textContent = String(text); }
        return node;
    }

    function bytes(n) {
        if (n === null || n === undefined || isNaN(n)) { return '—'; }
        var neg = n < 0; n = Math.abs(n);
        if (n < 1024) { return (neg ? '-' : '') + n + ' B'; }
        var units = ['KiB', 'MiB', 'GiB', 'TiB'], v = n / 1024, i = 0;
        while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
        return (neg ? '-' : '') + v.toFixed(v < 10 ? 2 : 1) + ' ' + units[i];
    }

    function bitrate(info) {
        if (!info || info.Bps === null || info.Bps === undefined) { return '—'; }
        var mbps = info.Bps / 1000000;
        var text = mbps >= 1 ? mbps.toFixed(2) + ' Mb/s' : Math.round(info.Bps / 1000) + ' kb/s';
        return (info.Source === 'Derived' ? '≈ ' : '') + text;
    }

    function duration(seconds) {
        if (!seconds || seconds < 1) { return '—'; }
        var s = Math.round(seconds);
        var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
        if (h > 0) { return h + 'h ' + m + 'm'; }
        if (m > 0) { return m + 'm'; }
        return s + 's';
    }

    function apiClient() {
        return window.ApiClient || (window.Emby && window.Emby.ApiClient) || null;
    }

    function request(method, path, body) {
        var client = apiClient();
        if (!client) { return Promise.reject(new Error('ApiClient is not available')); }

        var options = { type: method, url: client.getUrl(path), headers: { accept: 'application/json' } };
        if (body !== undefined) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
        }

        return client.ajax(options).then(function (response) {
            if (typeof response === 'string') { return response.length ? JSON.parse(response) : null; }
            return response;
        }, function (error) {
            var message = 'Request failed';
            if (error && error.status === 403) {
                message = 'You need administrator rights to do that.';
            } else if (error && error.status === 404) {
                message = 'The server could not find that item.';
            } else if (error && error.statusText) {
                message = error.statusText;
            }
            var wrapped = new Error(message);
            wrapped.status = error && error.status;
            return Promise.reject(wrapped);
        });
    }

    // ------------------------------------------------------- item id resolution

    function rememberItemFromEvent(event) {
        var node = event.target;
        while (node && node !== document.body) {
            if (node.getAttribute) {
                var id = node.getAttribute('data-id');
                if (id && GUID_RE.test(id)) { state.lastItemId = id; return; }
            }
            node = node.parentNode;
        }
    }

    document.addEventListener('pointerdown', rememberItemFromEvent, true);
    document.addEventListener('contextmenu', rememberItemFromEvent, true);

    function currentlyPlayingItemId() {
        var client = apiClient();
        if (!client) { return Promise.resolve(null); }
        var deviceId = client.deviceId && client.deviceId();
        return request('GET', 'Sessions' + (deviceId ? '?deviceId=' + encodeURIComponent(deviceId) : ''))
            .then(function (sessions) {
                for (var i = 0; sessions && i < sessions.length; i++) {
                    if (sessions[i].NowPlayingItem && sessions[i].NowPlayingItem.Id) {
                        return sessions[i].NowPlayingItem.Id;
                    }
                }
                return null;
            }).catch(function () { return null; });
    }

    // ------------------------------------------------------------- DOM grafting

    function addContextMenuEntry(sheet) {
        var scroller = sheet.querySelector('.actionSheetScroller') || sheet.querySelector('.actionSheetContent');
        if (!scroller) { warnOnce('sheet', 'Action sheet layout not recognised.'); return; }
        if (!state.lastItemId) { return; }

        var itemId = state.lastItemId;

        // Each entry is added independently: the move entry waits on the capability check, so a
        // sheet that already carries the optimize entry may still be owed the move one.
        if (!sheet.querySelector('[data-id="mediaoptimizer"]')) {
            scroller.insertAdjacentHTML('beforeend',
                '<button is="emby-button" type="button" class="listItem listItem-button actionSheetMenuItem" data-id="mediaoptimizer">' +
                '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons tune" aria-hidden="true"></span>' +
                '<div class="listItemBody actionsheetListItemBody">' +
                '<div class="listItemBodyText actionSheetItemText">Optimize file…</div>' +
                '</div></button>');

            var button = scroller.querySelector('[data-id="mediaoptimizer"]');
            if (button) {
                button.addEventListener('click', function (event) {
                    event.preventDefault();
                    event.stopPropagation();
                    closeActionSheet(sheet);
                    openDialog(itemId);
                }, true);
            }
        }

        addMoveEntry(sheet, scroller, itemId);
    }

    /**
     * Relocating a file is a separate action from converting it — nothing is re-encoded — so it
     * gets its own entry rather than a mode inside the optimize dialog. It is only offered to
     * administrators, because the API behind it is: an ordinary user would get a 403 and no
     * explanation of why the entry was there at all.
     */
    function addMoveEntry(sheet, scroller, itemId) {
        if (!state.capabilities || !state.capabilities.CanConvert) { return; }
        if (sheet.querySelector('[data-id="mediaoptimizer-move"]')) { return; }

        scroller.insertAdjacentHTML('beforeend',
            '<button is="emby-button" type="button" class="listItem listItem-button actionSheetMenuItem" data-id="mediaoptimizer-move">' +
            '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons drive_file_move" aria-hidden="true"></span>' +
            '<div class="listItemBody actionsheetListItemBody">' +
            '<div class="listItemBodyText actionSheetItemText">Move to another drive\u2026</div>' +
            '</div></button>');

        var move = scroller.querySelector('[data-id="mediaoptimizer-move"]');
        if (!move) { return; }
        move.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            closeActionSheet(sheet);
            openMoveDialog(itemId);
        }, true);
    }

    function closeActionSheet(sheet) {
        var cancel = sheet.querySelector('.btnCloseActionSheet');
        if (cancel) { cancel.click(); return; }
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    }

    function addPlayerButton(bar) {
        if (bar.querySelector('.btnMediaOptimizer')) { return; }
        var anchor = bar.querySelector('.btnVideoOsdSettings');
        var markup =
            '<button is="paper-icon-button-light" class="btnMediaOptimizer autoSize" title="Optimize file">' +
            '<span class="largePaperIconButton material-icons tune" aria-hidden="true"></span></button>';

        if (anchor) { anchor.insertAdjacentHTML('beforebegin', markup); }
        else { bar.insertAdjacentHTML('beforeend', markup); }

        var button = bar.querySelector('.btnMediaOptimizer');
        if (!button) { return; }
        button.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            currentlyPlayingItemId().then(function (id) {
                if (id) { openDialog(id); }
                else { warnOnce('nowplaying', 'Could not determine the currently playing item.'); }
            });
        });
    }

    function scan() {
        try {
            var sheets = document.querySelectorAll('.actionSheet');
            for (var i = 0; i < sheets.length; i++) { addContextMenuEntry(sheets[i]); }
            var bars = document.querySelectorAll('.videoOsdBottom .buttons');
            for (var j = 0; j < bars.length; j++) { addPlayerButton(bars[j]); }
        } catch (e) {
            warnOnce('scan', 'DOM integration failed: ' + e.message);
        }
    }

    var scanQueued = false;
    var observer = new MutationObserver(function () {
        if (scanQueued) { return; }
        scanQueued = true;
        requestAnimationFrame(function () { scanQueued = false; scan(); });
    });

    // ----------------------------------------------------------- dialog shell

    var PRESETS = [
        { key: 'Standard', label: 'Standard',
          hint: 'Keeps the resolution and re-encodes to a more efficient codec. Best when the file is older H.264 or MPEG.' },
        { key: 'Medium', label: 'Medium reduction',
          hint: 'Standard, plus one step down in resolution (4K to 1440p, 1080p to 720p) and leaner audio.' },
        { key: 'HighReduction', label: 'High reduction',
          hint: 'One step down in resolution and a lower bitrate on top. Smallest file; quality loss is visible.' },
        { key: 'LosslessOnly', label: 'Lossless only',
          hint: 'Bit-exact changes only — dropping tracks and re-packing lossless audio. Verified by hash afterwards.' },
        { key: 'Custom', label: 'Custom', hint: 'Set everything yourself.' }
    ];

    /**
     * Creates the overlay inside a shadow root so that no stylesheet on the host page can
     * reposition, hide or restyle any part of it.
     */
    function createShell(title) {
        var host = el('div');
        host.id = 'mopt-host';
        // A high z-index on the host too: the shadow boundary stops CSS, not stacking context.
        host.style.cssText = 'position:fixed;inset:0;z-index:2147483000';
        var root = host.attachShadow({ mode: 'open' });

        var style = document.createElement('style');
        style.textContent = CSS;
        root.appendChild(style);

        var overlay = el('div', 'mopt-overlay');
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', title || 'Optimize media file');

        var dialog = el('div', 'mopt-dialog');
        overlay.appendChild(dialog);
        root.appendChild(overlay);
        document.body.appendChild(host);

        function close() {
            document.removeEventListener('keydown', onKey, true);
            if (host.parentNode) { host.parentNode.removeChild(host); }
        }
        function onKey(e) { if (e.key === 'Escape') { e.stopPropagation(); close(); } }

        overlay.addEventListener('click', function (e) { if (e.target === overlay) { close(); } });
        document.addEventListener('keydown', onKey, true);

        return { host: host, root: root, overlay: overlay, dialog: dialog, close: close };
    }

    function buildHeader(shell, title, close) {
        var head = el('div', 'mopt-head');
        head.appendChild(el('h2', null, title));
        var closeBtn = el('button', 'mopt-close', '×');
        closeBtn.setAttribute('aria-label', 'Close');
        closeBtn.addEventListener('click', close);
        head.appendChild(closeBtn);
        return head;
    }

    function skeleton(lines) {
        var host = el('div', 'mopt-skeleton');
        var widths = ['w80', 'w60', '', 'w40', 'w80', 'w60'];
        for (var i = 0; i < lines; i++) {
            host.appendChild(el('div', 'mopt-skel ' + widths[i % widths.length]));
        }
        return host;
    }

    function showError(dialog, title, message) {
        dialog.innerHTML = '';
        var box = el('div', 'mopt-error');
        box.appendChild(el('h3', null, title));
        box.appendChild(el('p', null, message));
        dialog.appendChild(box);
    }

    // ------------------------------------------------------------- single item

    function openDialog(itemId) {
        // The shell appears immediately so the click always feels like it did something; the
        // panes fill in as the analysis and capabilities arrive.
        var shell = createShell('Optimize media file');
        shell.dialog.appendChild(buildHeader(shell, 'Analysing file…', shell.close));

        var body = el('div', 'mopt-body');
        var left = el('div', 'mopt-pane mopt-pane-left');
        var right = el('div', 'mopt-pane');
        left.appendChild(skeleton(8));
        right.appendChild(skeleton(6));
        body.appendChild(left);
        body.appendChild(right);
        shell.dialog.appendChild(body);

        var capsPromise = state.capabilities
            ? Promise.resolve(state.capabilities)
            : request('GET', 'MediaOptimizer/Capabilities').then(function (c) { state.capabilities = c; return c; });

        Promise.all([request('GET', 'MediaOptimizer/Analyze/' + itemId), capsPromise])
            .then(function (r) { renderDialog(shell, r[0], r[1]); })
            .catch(function (error) {
                showError(shell.dialog, 'Could not analyse this file', error.message);
            });
    }

    function renderDialog(shell, analysis, caps) {
        shell.dialog.innerHTML = '';
        shell.dialog.appendChild(buildHeader(shell, analysis.Name || 'Media file', shell.close));

        var body = el('div', 'mopt-body');
        var left = el('div', 'mopt-pane mopt-pane-left');
        var right = el('div', 'mopt-pane');
        body.appendChild(left);
        body.appendChild(right);
        shell.dialog.appendChild(body);

        var foot = el('div', 'mopt-foot');
        shell.dialog.appendChild(foot);

        renderCurrentFile(left, analysis);

        if (!analysis.IsEligible) {
            right.appendChild(warningBox('blocker', analysis.IneligibleReason || 'This item cannot be converted.'));
            return;
        }
        if (!caps.CanConvert) {
            right.appendChild(warningBox('info',
                'You are signed in without administrator rights, so this is a read-only view. ' +
                'Converting rewrites files in the library and is restricted to administrators.'));
            return;
        }

        renderTargetForm(shell, right, foot, analysis, caps);
    }

    function renderCurrentFile(pane, a) {
        pane.appendChild(el('div', 'mopt-sec', 'Current file'));

        var d = el('dl', 'mopt-kv');
        function kv(k, v) { d.appendChild(el('dt', null, k)); d.appendChild(el('dd', null, v)); }
        kv('Container', (a.Container || '—').toUpperCase());
        kv('File size', bytes(a.SizeBytes));
        kv('Duration', duration(a.DurationSeconds));
        kv('Overall bitrate', bitrate(a.OverallBitrate));
        pane.appendChild(d);

        if (a.Video) {
            pane.appendChild(el('div', 'mopt-sec', 'Video'));
            var v = el('dl', 'mopt-kv');
            function vkv(k, val) { v.appendChild(el('dt', null, k)); v.appendChild(el('dd', null, val)); }
            vkv('Codec', (a.Video.Codec || '—').toUpperCase() + (a.Video.Profile ? ' · ' + a.Video.Profile : ''));
            vkv('Resolution', a.Video.Width && a.Video.Height ? a.Video.Width + ' × ' + a.Video.Height : '—');
            vkv('Bit depth', a.Video.BitDepth ? a.Video.BitDepth + '-bit' : '—');
            vkv('Frame rate', a.Video.FrameRate
                ? a.Video.FrameRate.toFixed(3).replace(/\.?0+$/, '') + ' fps' + (a.Video.IsVariableFrameRate ? ' (VFR)' : '')
                : '—');
            vkv('Dynamic range', a.Video.Range || 'SDR');
            vkv('Video bitrate', bitrate(a.Video.Bitrate));
            pane.appendChild(v);
        }

        if (a.Audio && a.Audio.length) {
            pane.appendChild(el('div', 'mopt-sec', 'Audio (' + a.Audio.length + ')'));
            a.Audio.forEach(function (t) {
                var box = el('div', 'mopt-track');
                var h = el('div', 'mopt-track-head');
                h.appendChild(el('strong', null, (t.Codec || '?').toUpperCase()));
                if (t.IsLossless) { h.appendChild(el('span', 'mopt-chip mopt-chip-good', 'lossless')); }
                if (t.HasObjectAudio) { h.appendChild(el('span', 'mopt-chip mopt-chip-warn', 'objects')); }
                if (t.IsDefault) { h.appendChild(el('span', 'mopt-chip', 'default')); }
                box.appendChild(h);
                box.appendChild(el('div', 'mopt-track-meta', [
                    t.ChannelLayout || (t.Channels ? t.Channels + 'ch' : null),
                    t.Language,
                    t.SampleRate ? Math.round(t.SampleRate / 1000) + ' kHz' : null,
                    bitrate(t.Bitrate)
                ].filter(Boolean).join(' · ')));
                pane.appendChild(box);
            });
        }

        if (a.Subtitles && a.Subtitles.length) {
            pane.appendChild(el('div', 'mopt-sec', 'Subtitles (' + a.Subtitles.length + ')'));
            var graphical = a.Subtitles.filter(function (s) { return s.IsGraphical; }).length;
            var langs = {};
            a.Subtitles.forEach(function (s) { langs[s.Language || 'und'] = true; });
            pane.appendChild(el('div', 'mopt-track-meta',
                Object.keys(langs).join(', ') +
                (graphical ? ' · ' + graphical + ' image-based' : '')));
        }

        pane.appendChild(el('div', 'mopt-sec', 'Storage'));
        var s = el('dl', 'mopt-kv');
        s.appendChild(el('dt', null, 'Attachments')); s.appendChild(el('dd', null, a.AttachmentCount || 0));
        s.appendChild(el('dt', null, 'Chapters')); s.appendChild(el('dd', null, a.ChapterCount || 0));
        s.appendChild(el('dt', null, 'Folder writable')); s.appendChild(el('dd', null, a.IsWritable ? 'yes' : 'no'));
        pane.appendChild(s);
        pane.appendChild(el('div', 'mopt-path', a.Path || ''));
    }

    function warningBox(level, message) {
        var box = el('div', 'mopt-warn mopt-warn-' + String(level || 'info').toLowerCase());
        box.appendChild(el('div', null, message));
        return box;
    }

    // --------------------------------------------------------- the target form

    function renderTargetForm(shell, pane, foot, a, caps) {
        var req = null;
        var strategy = a.RecommendedStrategy || 'Standard';

        pane.appendChild(el('div', 'mopt-sec', 'How much to reduce'));
        var presetRow = el('div', 'mopt-presets');
        var hint = el('div', 'mopt-preset-hint');
        var formHost = el('div');
        var warnHost = el('div');

        pane.appendChild(presetRow);
        pane.appendChild(hint);
        pane.appendChild(warnHost);
        pane.appendChild(formHost);

        PRESETS.forEach(function (preset) {
            var b = el('button', 'mopt-preset');
            b.type = 'button';
            b.textContent = preset.label;
            if (preset.key === strategy) {
                b.setAttribute('aria-pressed', 'true');
                if (a.RecommendedStrategy === preset.key) {
                    b.appendChild(el('span', 'mopt-recommend', 'best here'));
                }
            } else {
                b.setAttribute('aria-pressed', 'false');
            }
            b.addEventListener('click', function () {
                Array.prototype.forEach.call(presetRow.children, function (c) { c.setAttribute('aria-pressed', 'false'); });
                b.setAttribute('aria-pressed', 'true');
                strategy = preset.key;
                hint.textContent = preset.hint;
                if (preset.key === 'Custom') { req.Strategy = 'Custom'; rebuild(); }
                else { loadStrategy(preset.key); }
            });
            presetRow.appendChild(b);
        });

        var selected = PRESETS.filter(function (p) { return p.key === strategy; })[0];
        hint.textContent = selected ? selected.hint : '';

        var estimate = el('div', 'mopt-estimate');
        var estMain = el('div', 'mopt-estimate-main', 'Working out the saving…');
        var estSub = el('div', 'mopt-estimate-sub', '');
        estimate.appendChild(estMain);
        estimate.appendChild(estSub);
        foot.appendChild(estimate);

        var cancelBtn = el('button', 'mopt-btn', 'Cancel');
        cancelBtn.addEventListener('click', shell.close);
        foot.appendChild(cancelBtn);

        var startBtn = el('button', 'mopt-btn mopt-btn-primary', 'Start conversion');
        startBtn.disabled = true;
        foot.appendChild(startBtn);

        function loadStrategy(key) {
            formHost.innerHTML = '';
            formHost.appendChild(skeleton(5));
            request('GET', 'MediaOptimizer/ResolveStrategy/' + a.ItemId + '?strategy=' + encodeURIComponent(key))
                .then(function (resolved) { req = resolved; rebuild(); })
                .catch(function (e) {
                    formHost.innerHTML = '';
                    formHost.appendChild(warningBox('blocker', 'Could not load these settings: ' + e.message));
                });
        }

        function rebuild() {
            formHost.innerHTML = '';
            buildFields(formHost, req, a, caps, rebuild, setContainer);
            refreshEstimate();
        }

        function setContainer(value) { req.Container = value; rebuild(); }

        var estimateTimer = null;
        function refreshEstimate() {
            clearTimeout(estimateTimer);
            estimateTimer = setTimeout(function () {
                request('POST', 'MediaOptimizer/Estimate', req)
                    .then(renderEstimate)
                    .catch(function (e) {
                        estMain.textContent = 'Estimate unavailable';
                        estSub.textContent = e.message;
                        startBtn.disabled = false;
                    });
            }, 200);
        }

        function renderEstimate(result) {
            warnHost.innerHTML = '';
            renderWarnings(warnHost, result.Warnings || [], req, setContainer, refreshEstimate);

            var blockers = (result.Warnings || []).filter(function (w) { return w.Level === 'Blocker'; }).length;
            var savedBytes = result.CurrentSizeBytes - result.EstimatedSizeBytes;
            var pct = Math.round((result.SavingFraction || 0) * 100);

            estMain.innerHTML = '';
            estMain.appendChild(document.createTextNode(
                bytes(result.CurrentSizeBytes) + '  →  ' + bytes(result.EstimatedSizeBytes) + '   '));
            var delta = el('span', pct > 0 ? 'mopt-delta-good' : pct < 0 ? 'mopt-delta-bad' : null,
                pct > 0 ? 'saves ' + pct + '%' : pct < 0 ? 'grows ' + Math.abs(pct) + '%' : 'about the same');
            estMain.appendChild(delta);

            var parts = [];
            if (pct > 0) { parts.push('frees ' + bytes(savedBytes)); }
            if (result.Confidence === 'Medium') { parts.push('estimate ' + bytes(result.EstimatedSizeLowBytes) + ' – ' + bytes(result.EstimatedSizeHighBytes)); }
            if (result.EstimatedSeconds && result.TimeBasis === 'measured on this server') {
                parts.push('about ' + duration(result.EstimatedSeconds) + ' to encode');
            } else if (result.TimeBasis === 'unmeasured') {
                parts.push('encode time shown once it starts');
            }
            parts.push(result.IsLossless ? 'bit-exact — hash verified afterwards' : 'lossy');
            estSub.textContent = parts.join(' · ');

            if (result.SavingNote) {
                warnHost.insertBefore(warningBox('info', result.SavingNote), warnHost.firstChild);
            }

            startBtn.disabled = blockers > 0;
            startBtn.title = blockers > 0 ? 'Resolve the blocking issue above first.' : '';
        }

        startBtn.addEventListener('click', function () {
            startBtn.disabled = true;
            startBtn.textContent = 'Queueing…';
            request('POST', 'MediaOptimizer/Jobs', req).then(function (job) {
                showProgress(shell, pane, foot, job);
            }).catch(function (e) {
                startBtn.disabled = false;
                startBtn.textContent = 'Start conversion';
                warnHost.insertBefore(warningBox('blocker', e.message), warnHost.firstChild);
            });
        });

        loadStrategy(strategy);
    }

    /**
     * Renders planner messages, collapsing a long tail behind a toggle. Eight identical
     * subtitle warnings used to bury the one that mattered.
     */
    function renderWarnings(host, warnings, req, setContainer, refresh) {
        var ordered = warnings.slice().sort(function (x, y) {
            var rank = { Blocker: 0, Warning: 1, Info: 2 };
            return (rank[x.Level] || 3) - (rank[y.Level] || 3);
        });

        var important = ordered.filter(function (w) { return w.Level !== 'Info'; });
        var minor = ordered.filter(function (w) { return w.Level === 'Info'; });

        important.forEach(function (w) { host.appendChild(buildWarning(w, req, setContainer, refresh)); });

        if (!minor.length) { return; }

        var shown = false;
        var toggle = el('button', 'mopt-warn-more', 'Show ' + minor.length + ' more note' + (minor.length === 1 ? '' : 's'));
        toggle.type = 'button';
        var extra = el('div');
        extra.style.display = 'none';
        minor.forEach(function (w) { extra.appendChild(buildWarning(w, req, setContainer, refresh)); });
        toggle.addEventListener('click', function () {
            shown = !shown;
            extra.style.display = shown ? '' : 'none';
            toggle.textContent = shown ? 'Hide notes'
                : 'Show ' + minor.length + ' more note' + (minor.length === 1 ? '' : 's');
        });
        host.appendChild(toggle);
        host.appendChild(extra);
    }

    function buildWarning(w, req, setContainer, refresh) {
        var box = warningBox(w.Level, w.Message);
        var actions = el('div', 'mopt-warn-actions');

        // Warnings that name a fix get a button that applies it.
        if (w.Code === 'SUBTITLE_INCOMPATIBLE' || w.Code === 'ATTACHMENTS_LOST') {
            var toMkv = el('button', 'mopt-btn mopt-btn-small', 'Use MKV instead');
            toMkv.type = 'button';
            toMkv.addEventListener('click', function () { setContainer('mkv'); });
            actions.appendChild(toMkv);
        }
        if (w.Code === 'DOLBY_VISION_LOSS') {
            var accept = el('label', 'mopt-check');
            var input = document.createElement('input');
            input.type = 'checkbox';
            input.addEventListener('change', function () { req.AcceptDolbyVisionLoss = input.checked; refresh(); });
            accept.appendChild(input);
            accept.appendChild(el('span', null, 'I understand Dolby Vision will be lost'));
            box.appendChild(accept);
        }

        if (actions.children.length) { box.appendChild(actions); }
        return box;
    }

    /**
     * Picks the encoder a codec dropdown should start on. A <select> falls back to showing its
     * first option when nothing matches the current value, and choosing that same option fires
     * no change event — so an unset codec has to be written into the request here, or the
     * request keeps a null the form is not showing and the job is rejected for having no
     * encoder selected.
     */
    function defaultEncoder(value, encoders) {
        if (value) { return value; }
        return encoders.length ? encoders[0].Name : null;
    }

    /**
     * Builds the option list for a codec dropdown. A codec the server no longer offers is kept
     * in the list rather than quietly swapped for another one: the dropdown then shows what the
     * request actually says, and the estimate explains why it cannot run.
     */
    function encoderOptions(encoders, value) {
        var options = encoders.map(function (e) { return { value: e.Name, label: e.DisplayName }; });
        var present = encoders.some(function (e) { return e.Name === value; });
        if (value && !present) {
            options.unshift({ value: value, label: value + ' — not available on this server' });
        }
        return options;
    }

    function selectField(host, label, options, value, onChange, hints) {
        var field = el('div', 'mopt-field');
        field.appendChild(el('label', null, label));
        var select = document.createElement('select');
        options.forEach(function (o) {
            var opt = document.createElement('option');
            opt.value = o.value === null || o.value === undefined ? '' : String(o.value);
            opt.textContent = o.label;
            if (String(o.value) === String(value)
                || ((o.value === null || o.value === undefined) && (value === null || value === undefined))) {
                opt.selected = true;
            }
            select.appendChild(opt);
        });
        select.addEventListener('change', function () { onChange(select.value === '' ? null : select.value); });
        field.appendChild(select);

        // A <select> clips its text rather than wrapping it, so an option label long enough to
        // explain itself gets cut off mid-sentence on a phone. The label stays short and the
        // explanation goes underneath, where it can wrap.
        if (hints) {
            var note = el('div', 'mopt-sub');
            note.textContent = hints[String(select.value)] || '';
            field.appendChild(note);
            select.addEventListener('change', function () {
                note.textContent = hints[String(select.value)] || '';
            });
        }

        host.appendChild(field);
        return select;
    }

    function textField(host, label, value, onChange, placeholder) {
        var field = el('div', 'mopt-field');
        field.appendChild(el('label', null, label));
        var input = document.createElement('input');
        input.type = 'text';
        if (placeholder) { input.placeholder = placeholder; }
        input.value = value === null || value === undefined ? '' : value;
        input.addEventListener('change', function () {
            onChange(input.value.trim() === '' ? null : input.value.trim());
        });
        field.appendChild(input);
        host.appendChild(field);
    }

    function numberField(host, label, value, onChange, attrs) {
        var field = el('div', 'mopt-field');
        field.appendChild(el('label', null, label));
        var input = document.createElement('input');
        input.type = 'number';
        Object.keys(attrs || {}).forEach(function (k) { input.setAttribute(k, attrs[k]); });
        input.value = value === null || value === undefined ? '' : value;
        input.addEventListener('change', function () {
            onChange(input.value === '' ? null : Number(input.value));
        });
        field.appendChild(input);
        host.appendChild(field);
    }

    // Mirrors the server's matcher closely enough for the form to update as you type; the server
    // is still the authority when the job is actually queued.
    function normalizeLanguage(value) {
        if (!value) { return null; }
        var v = String(value).trim().toLowerCase().split(/[-_]/)[0];
        var map = { en: 'eng', english: 'eng', eng: 'eng', fr: 'fra', french: 'fra', fra: 'fra', fre: 'fra',
            de: 'deu', german: 'deu', deu: 'deu', ger: 'deu', es: 'spa', spanish: 'spa', spa: 'spa',
            it: 'ita', italian: 'ita', ita: 'ita', pt: 'por', portuguese: 'por', por: 'por',
            ja: 'jpn', japanese: 'jpn', jpn: 'jpn', nl: 'nld', dutch: 'nld', nld: 'nld', dut: 'nld',
            zh: 'zho', chinese: 'zho', zho: 'zho', chi: 'zho', ko: 'kor', korean: 'kor', kor: 'kor',
            ru: 'rus', russian: 'rus', rus: 'rus', pl: 'pol', polish: 'pol', pol: 'pol' };
        if (v === 'und' || v === 'unknown' || v === '') { return null; }
        return map[v] || v;
    }

    function applyLanguageFilter(req, a) {
        var keep = (req.KeepAudioLanguages || '').split(/[,;\s]+/)
            .map(normalizeLanguage).filter(Boolean);

        if (!keep.length) {
            req.AudioTracks.forEach(function (t) { if (t.Action === 'Drop') { t.Action = 'Copy'; } });
            return;
        }

        var survivors = 0;
        req.AudioTracks.forEach(function (t) {
            var source = (a.Audio || []).filter(function (x) { return x.Index === t.Index; })[0];
            var lang = source ? normalizeLanguage(source.Language) : null;
            var wanted = lang === null ? true : keep.indexOf(lang) >= 0;
            t.Action = wanted ? (t.Action === 'Drop' ? 'Copy' : t.Action) : 'Drop';
            if (wanted) { survivors++; }
        });

        // Never leave a file with no audio at all.
        if (survivors === 0 && req.AudioTracks.length) { req.AudioTracks[0].Action = 'Copy'; }
    }

    function buildFields(host, req, a, caps, rebuild, setContainer) {
        if (!req) { return; }
        var lossless = req.Strategy === 'LosslessOnly';

        if (lossless) {
            host.appendChild(warningBox('info',
                a.Video && a.Video.IsLosslessCodec
                    ? 'This file uses a lossless video codec, so the video can be re-encoded losslessly too.'
                    : 'The video stream is copied untouched. Re-encoding already-compressed video "losslessly" ' +
                      'stores the decoded frames and always produces a much larger file, so it is not offered.'));
        }

        if (a.Video && !lossless) {
            var videoEncoders = caps.VideoEncoders.filter(function (e) { return e.Codec !== 'ffv1'; });

            host.appendChild(el('div', 'mopt-sec', 'Video'));

            // With no encoders there is nothing to put in the Codec dropdown, and offering
            // "Re-encode" would lead straight to an empty list and a blocker at the end. Say what
            // is wrong here instead.
            if (!videoEncoders.length) {
                req.Video = 'Copy';
                host.appendChild(warningBox('blocker',
                    'This server\'s FFmpeg reported no video encoders, so the video cannot be re-encoded. '
                    + (caps.ProbeError || 'Check that Dashboard \u2192 Playback \u2192 Transcoding points at a working FFmpeg.')
                    + ' Media Optimizer\'s dashboard page has a diagnostics panel with the full detail.'));
            }

            var row1 = el('div', 'mopt-row');
            var actions = videoEncoders.length
                ? [{ value: 'Encode', label: 'Re-encode' }, { value: 'Copy', label: 'Keep exactly as it is' }]
                : [{ value: 'Copy', label: 'Keep exactly as it is' }];
            selectField(row1, 'Action', actions, req.Video, function (v) { req.Video = v; rebuild(); });

            if (req.Video === 'Encode') {
                // A <select> shows its first option when nothing matches the current value, but
                // picking that same option fires no change event — so without this the request
                // would keep the null the form is not showing, and the job would be rejected for
                // having no encoder selected.
                req.VideoCodec = defaultEncoder(req.VideoCodec, videoEncoders);
                selectField(row1, 'Codec', encoderOptions(videoEncoders, req.VideoCodec),
                    req.VideoCodec, function (v) { req.VideoCodec = v; rebuild(); });
            }
            host.appendChild(row1);

            if (req.Video === 'Encode') {
                var row2 = el('div', 'mopt-row');
                var heights = [{ value: null, label: 'Keep original (' + (a.Video.Height || '?') + 'p)' }];
                [2160, 1440, 1080, 720, 480].forEach(function (h) {
                    if (!a.Video.Height || h < a.Video.Height) {
                        heights.push({ value: h, label: h + 'p' + (h === 2160 ? ' (4K)' : h === 1440 ? ' (2K)' : '') });
                    }
                });
                selectField(row2, 'Resolution', heights, req.TargetHeight, function (v) {
                    req.TargetHeight = v === null ? null : parseInt(v, 10); rebuild();
                });

                var enc = caps.VideoEncoders.filter(function (e) { return e.Name === req.VideoCodec; })[0];
                var depths = [{ value: 8, label: '8-bit' }];
                if (!enc || enc.Supports10Bit) { depths.push({ value: 10, label: '10-bit' }); }
                selectField(row2, 'Bit depth', depths, req.BitDepth, function (v) {
                    req.BitDepth = parseInt(v, 10); rebuild();
                });
                if (enc && enc.Presets && enc.Presets.length) {
                    selectField(row2, 'Encoder speed',
                        enc.Presets.map(function (p) { return { value: p, label: p }; }),
                        req.Preset, function (v) { req.Preset = v; rebuild(); });
                }
                host.appendChild(row2);

                var row3 = el('div', 'mopt-row');
                selectField(row3, 'Quality mode', [
                    { value: 'ConstantQuality', label: 'Constant quality' },
                    { value: 'AverageBitrate', label: 'Fixed bitrate' },
                    { value: 'TargetSize', label: 'Target file size' }
                ], req.RateControl, function (v) { req.RateControl = v; rebuild(); }, {
                    ConstantQuality: 'Recommended. Spends bits where the picture needs them.',
                    AverageBitrate: 'Every second gets the same bitrate, whatever the scene.',
                    TargetSize: 'Aims at a size you choose; quality follows from it.'
                });

                if (req.RateControl === 'ConstantQuality') {
                    numberField(row3, 'Quality (lower = better, bigger)', req.Quality,
                        function (v) { req.Quality = v; rebuild(); }, { min: 0, max: 51 });
                } else if (req.RateControl === 'AverageBitrate') {
                    numberField(row3, 'Video bitrate (Mb/s)',
                        req.VideoBitrateBps ? (req.VideoBitrateBps / 1000000) : '',
                        function (v) { req.VideoBitrateBps = v === null ? null : Math.round(v * 1000000); rebuild(); },
                        { min: 0.1, step: 0.1 });
                } else {
                    numberField(row3, 'Target size (MiB)',
                        req.TargetSizeBytes ? Math.round(req.TargetSizeBytes / 1048576) : '',
                        function (v) { req.TargetSizeBytes = v === null ? null : v * 1048576; rebuild(); },
                        { min: 1 });
                }
                host.appendChild(row3);

                if (caps.VideoEncoders.some(function (e) { return e.IsHardware; })) {
                    var hw = el('label', 'mopt-check');
                    var hwi = document.createElement('input');
                    hwi.type = 'checkbox';
                    hwi.checked = !!req.UseHardware;
                    hwi.addEventListener('change', function () { req.UseHardware = hwi.checked; rebuild(); });
                    hw.appendChild(hwi);
                    var hwText = el('span', null, 'Encode on the graphics card');
                    hwText.appendChild(el('span', 'mopt-sub',
                        'Finishes several times faster, but needs roughly 50% more space to look as good. ' +
                        'Use it when you want speed; leave it off when you want the smallest file.'));
                    hw.appendChild(hwText);
                    host.appendChild(hw);
                }
            }
        }

        if (a.Audio && a.Audio.length) {
            host.appendChild(el('div', 'mopt-sec', 'Audio'));
            a.Audio.forEach(function (track) {
                var entry = req.AudioTracks.filter(function (x) { return x.Index === track.Index; })[0];
                if (!entry) { return; }
                var box = el('div', 'mopt-track');
                var head = el('div', 'mopt-track-head');
                head.appendChild(el('strong', null,
                    (track.Codec || '?').toUpperCase() + ' · ' +
                    (track.ChannelLayout || (track.Channels || '?') + 'ch') +
                    (track.Language ? ' · ' + track.Language : '')));
                box.appendChild(head);

                var row = el('div', 'mopt-row');
                row.style.marginBottom = '0';
                selectField(row, 'Action', [
                    { value: 'Copy', label: 'Keep as-is' },
                    { value: 'Encode', label: 'Re-encode' },
                    { value: 'Drop', label: 'Remove' }
                ], entry.Action, function (v) {
                    entry.Action = v;
                    if (v !== 'Encode') { entry.Codec = null; entry.BitrateBps = null; }
                    rebuild();
                });

                if (entry.Action === 'Encode') {
                    entry.Codec = defaultEncoder(entry.Codec, caps.AudioEncoders);
                    selectField(row, 'Codec', encoderOptions(caps.AudioEncoders, entry.Codec),
                        entry.Codec, function (v) { entry.Codec = v; rebuild(); });
                    if (entry.Codec !== 'flac') {
                        numberField(row, 'Bitrate (kb/s)', entry.BitrateBps ? entry.BitrateBps / 1000 : '',
                            function (v) { entry.BitrateBps = v === null ? null : v * 1000; rebuild(); },
                            { min: 32, step: 32 });
                    }
                }
                box.appendChild(row);
                host.appendChild(box);
            });
        }

        if (a.Audio && a.Audio.length > 1) {
            host.appendChild(el('div', 'mopt-sec', 'Languages'));
            var langRow2 = el('div', 'mopt-row');
            textField(langRow2, 'Keep audio languages', req.KeepAudioLanguages,
                function (v) { req.KeepAudioLanguages = v; applyLanguageFilter(req, a); rebuild(); },
                'empty keeps all');
            textField(langRow2, 'Keep subtitle languages', req.KeepSubtitleLanguages,
                function (v) { req.KeepSubtitleLanguages = v; applyLanguageFilter(req, a); rebuild(); },
                'empty keeps all');
            host.appendChild(langRow2);
        }

        host.appendChild(el('div', 'mopt-sec', 'Output'));
        var outRow = el('div', 'mopt-row');
        selectField(outRow, 'Container', [
            { value: 'mp4', label: 'MP4' },
            { value: 'mkv', label: 'MKV' }
        ], req.Container, function (v) { setContainer(v); }, {
            mp4: 'Plays on the most devices.',
            mkv: 'Keeps every subtitle track and embedded font.'
        });

        selectField(outRow, 'The original file', [
            { value: 'Replace', label: 'Replace it' },
            { value: 'ReplaceAndDelete', label: 'Replace and delete now' },
            { value: 'Sidecar', label: 'Keep, save alongside' },
            { value: 'AlternateVersion', label: 'Keep, add as a version' }
        ], req.OutputPolicy, function (v) { req.OutputPolicy = v; rebuild(); }, {
            Replace: 'The old file is kept for a while so this can be undone.',
            ReplaceAndDelete: 'The old file is deleted as soon as the result is verified.',
            Sidecar: 'A new file is written next to the original.',
            AlternateVersion: 'Added to Jellyfin as another version of this item.'
        });
        host.appendChild(outRow);

        var keepRow = el('div');
        [['KeepAttachments', 'Keep embedded subtitle fonts and cover art'],
         ['KeepChapters', 'Keep chapter markers']].forEach(function (pair) {
            var wrap = el('label', 'mopt-check');
            var input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = !!req[pair[0]];
            input.addEventListener('change', function () { req[pair[0]] = input.checked; rebuild(); });
            wrap.appendChild(input);
            wrap.appendChild(el('span', null, pair[1]));
            keepRow.appendChild(wrap);
        });
        host.appendChild(keepRow);

        if (req.OutputPolicy === 'Replace') {
            host.appendChild(warningBox('info',
                'The old file is renamed and left in the same folder, so it can be put back ' +
                'instantly from Dashboard → Media Optimizer. It is deleted automatically once the ' +
                'retention period in the plugin settings has passed. Nothing is touched at all ' +
                'unless the new file passes verification.'));
        }

        if (req.OutputPolicy === 'ReplaceAndDelete') {
            host.appendChild(warningBox('warning',
                'The old file is deleted as soon as the new one passes verification. That frees ' +
                'the space immediately, but there is no undo — choose "keep the old file for a ' +
                'while" if you want to be able to change your mind.'));
        }
    }

    // ------------------------------------------------------------------- batch

    function openBatch(items, caps) {
        var shell = createShell('Convert multiple files');
        shell.dialog.appendChild(buildHeader(shell, 'Convert ' + items.length + ' file' + (items.length === 1 ? '' : 's'), shell.close));

        var body = el('div', 'mopt-body');
        body.style.gridTemplateColumns = 'minmax(0,1fr)';
        var pane = el('div', 'mopt-pane');
        body.appendChild(pane);
        shell.dialog.appendChild(body);
        var foot = el('div', 'mopt-foot');
        shell.dialog.appendChild(foot);

        var batch = { ItemIds: items.map(function (i) { return i.Id; }), Strategy: 'Medium',
            TargetHeight: null, Container: null, OutputPolicy: null, UseHardware: false,
            AcceptDolbyVisionLoss: false, KeepAudioLanguages: null, KeepSubtitleLanguages: null };

        pane.appendChild(el('div', 'mopt-sec', 'Files (' + items.length + ')'));
        var list = el('div', 'mopt-batch-list');
        items.forEach(function (i) {
            list.appendChild(el('div', 'mopt-batch-item',
                i.Name + '  ·  ' + bytes(i.SizeBytes) + (i.Height ? '  ·  ' + i.Height + 'p' : '')));
        });
        pane.appendChild(list);

        pane.appendChild(el('div', 'mopt-sec', 'Apply to all of them'));
        var presetRow = el('div', 'mopt-presets');
        var hint = el('div', 'mopt-preset-hint');
        PRESETS.filter(function (p) { return p.key !== 'Custom'; }).forEach(function (preset) {
            var b = el('button', 'mopt-preset', preset.label);
            b.type = 'button';
            b.setAttribute('aria-pressed', preset.key === batch.Strategy ? 'true' : 'false');
            b.addEventListener('click', function () {
                Array.prototype.forEach.call(presetRow.children, function (c) { c.setAttribute('aria-pressed', 'false'); });
                b.setAttribute('aria-pressed', 'true');
                batch.Strategy = preset.key;
                hint.textContent = preset.hint;
            });
            presetRow.appendChild(b);
        });
        pane.appendChild(presetRow);
        hint.textContent = PRESETS.filter(function (p) { return p.key === batch.Strategy; })[0].hint;
        pane.appendChild(hint);

        var row = el('div', 'mopt-row');
        selectField(row, 'Force a resolution', [
            { value: null, label: 'Let each preset decide' },
            { value: 2160, label: '2160p (4K)' }, { value: 1440, label: '1440p (2K)' },
            { value: 1080, label: '1080p' }, { value: 720, label: '720p' }, { value: 480, label: '480p' }
        ], batch.TargetHeight, function (v) { batch.TargetHeight = v === null ? null : parseInt(v, 10); });

        selectField(row, 'Container', [
            { value: null, label: 'Best for each file' },
            { value: 'mp4', label: 'MP4 everywhere' },
            { value: 'mkv', label: 'MKV everywhere' }
        ], batch.Container, function (v) { batch.Container = v; });

        selectField(row, 'The original files', [
            { value: null, label: 'Plugin default' },
            { value: 'Replace', label: 'Replace them' },
            { value: 'ReplaceAndDelete', label: 'Replace and delete now' },
            { value: 'Sidecar', label: 'Keep, save alongside' },
            { value: 'AlternateVersion', label: 'Keep, add as versions' }
        ], batch.OutputPolicy, function (v) { batch.OutputPolicy = v; });
        pane.appendChild(row);

        pane.appendChild(el('div', 'mopt-sec', 'Tracks to keep'));
        var langRow = el('div', 'mopt-row');
        textField(langRow, 'Audio languages', batch.KeepAudioLanguages,
            function (v) { batch.KeepAudioLanguages = v; }, 'Leave empty for the plugin default');
        textField(langRow, 'Subtitle languages', batch.KeepSubtitleLanguages,
            function (v) { batch.KeepSubtitleLanguages = v; }, 'Leave empty for the plugin default');
        pane.appendChild(langRow);
        pane.appendChild(el('div', 'mopt-preset-hint',
            'Comma separated, e.g. "eng". Every other track is removed, which is bit-exact for the ' +
            'ones you keep and is often the largest saving of all. A file is never left without audio.'));

        pane.appendChild(warningBox('info',
            'Each file is analysed on its own, so the preset adapts to what it actually is. ' +
            'Files that cannot be converted, or that are already queued, are skipped and listed afterwards. ' +
            'Forcing MP4 is ignored for any file whose subtitles or fonts it could not carry.'));

        var status = el('div', 'mopt-estimate');
        var statusMain = el('div', 'mopt-estimate-main', items.length + ' file' + (items.length === 1 ? '' : 's') + ' selected');
        var statusSub = el('div', 'mopt-estimate-sub', 'Jobs run one at a time by default.');
        status.appendChild(statusMain);
        status.appendChild(statusSub);
        foot.appendChild(status);

        var cancel = el('button', 'mopt-btn', 'Cancel');
        cancel.addEventListener('click', shell.close);
        foot.appendChild(cancel);

        var go = el('button', 'mopt-btn mopt-btn-primary', 'Queue all');
        foot.appendChild(go);

        go.addEventListener('click', function () {
            go.disabled = true;
            go.textContent = 'Queueing…';
            request('POST', 'MediaOptimizer/Jobs/Batch', batch).then(function (result) {
                pane.innerHTML = '';
                pane.appendChild(el('div', 'mopt-sec', 'Queued'));
                statusMain.textContent = result.QueuedCount + ' queued, ' + result.SkippedCount + ' skipped';
                statusSub.textContent = result.EstimatedSavingBytes > 0
                    ? 'Expected to free about ' + bytes(result.EstimatedSavingBytes)
                    : 'No overall saving predicted.';

                var skipped = result.Items.filter(function (i) { return !i.Queued; });
                if (skipped.length) {
                    pane.appendChild(el('div', 'mopt-sec', 'Skipped (' + skipped.length + ')'));
                    var box = el('div', 'mopt-batch-list');
                    skipped.forEach(function (i) {
                        box.appendChild(el('div', 'mopt-batch-item', (i.Name || i.ItemId) + ' — ' + (i.SkippedReason || 'skipped')));
                    });
                    pane.appendChild(box);
                }
                go.textContent = 'Done';
                go.disabled = false;
                go.addEventListener('click', shell.close);
            }).catch(function (e) {
                go.disabled = false;
                go.textContent = 'Queue all';
                pane.insertBefore(warningBox('blocker', e.message), pane.firstChild);
            });
        });
    }

    // -------------------------------------------------------------------- move

    /**
     * Moving a file to another drive. Deliberately a separate dialog from the optimizer: this
     * one never re-encodes anything, it only changes where the bytes live, and mixing the two
     * would make a destructive re-encode one mis-click away from "I just wanted more space".
     */
    function openMoveDialog(itemId) {
        var shell = createShell('Move to another drive');
        shell.dialog.classList.add('mopt-dialog-narrow');
        shell.dialog.appendChild(buildHeader(shell, 'Move to another drive', shell.close));

        var pane = el('div', 'mopt-pane mopt-pane-solo');
        pane.appendChild(skeleton(6));
        shell.dialog.appendChild(pane);

        Promise.all([
            request('GET', 'MediaOptimizer/Move/Items/' + itemId),
            request('GET', 'MediaOptimizer/Move/Locations?includeUsage=false')
        ]).then(function (r) {
            renderMoveDialog(shell, pane, r[0], r[1] || []);
        }).catch(function (error) {
            showError(shell.dialog, 'Could not read where this file lives', error.message);
        });
    }

    function renderMoveDialog(shell, pane, info, locations) {
        pane.innerHTML = '';

        var foot = el('div', 'mopt-foot');
        shell.dialog.appendChild(foot);

        pane.appendChild(el('div', 'mopt-sec', 'This file'));
        var kv = el('dl', 'mopt-kv');
        function row(k, v) { kv.appendChild(el('dt', null, k)); kv.appendChild(el('dd', null, v)); }
        row('Name', info.Name);
        row('Size', bytes(info.SizeBytes));
        kv.appendChild(el('dt', null, 'Now at'));
        kv.appendChild(el('dd', null, info.Path));
        pane.appendChild(kv);

        if (info.HasActiveMove) {
            pane.appendChild(warningBox('blocker', 'This file is already queued to move.'));
            return;
        }

        pane.appendChild(el('div', 'mopt-sec', 'Move it to'));

        // A folder the file is already in is not a destination; offering it would only produce
        // an "it is already there" message once the plan came back.
        var candidates = locations.filter(function (l) {
            return l.Path !== info.CurrentRoot;
        });

        if (!candidates.length) {
            pane.appendChild(warningBox('info',
                'There is nowhere else to put it. Add another library folder under Dashboard → ' +
                'Libraries, or list extra destinations in the Media Optimizer settings.'));
            return;
        }

        var status = el('div', 'mopt-estimate');
        var statusMain = el('div', 'mopt-estimate-main', 'Choose a drive');
        var statusSub = el('div', 'mopt-estimate-sub', '');
        status.appendChild(statusMain);
        status.appendChild(statusSub);
        foot.appendChild(status);

        var cancel = el('button', 'mopt-btn', 'Cancel');
        cancel.addEventListener('click', shell.close);
        foot.appendChild(cancel);

        var go = el('button', 'mopt-btn mopt-btn-primary', 'Move file');
        go.disabled = true;
        foot.appendChild(go);

        var chosen = null;
        var list = el('div');
        pane.appendChild(list);

        candidates.forEach(function (loc) {
            var entry = el('button', 'mopt-loc');
            entry.type = 'button';
            entry.setAttribute('aria-pressed', 'false');
            entry.appendChild(el('div', 'mopt-loc-path', loc.Path));
            entry.appendChild(el('div', 'mopt-loc-meta', [
                loc.LibraryName,
                loc.FreeBytes !== null && loc.FreeBytes !== undefined ? bytes(loc.FreeBytes) + ' free' : null,
                loc.Exists === false ? 'not reachable' : (loc.IsWritable === false ? 'read only' : null)
            ].filter(Boolean).join(' · ')));

            entry.addEventListener('click', function () {
                Array.prototype.forEach.call(list.children, function (c) { c.setAttribute('aria-pressed', 'false'); });
                entry.setAttribute('aria-pressed', 'true');
                chosen = loc.Path;
                preview();
            });

            list.appendChild(entry);
        });

        var notes = el('div');
        pane.appendChild(notes);

        function preview() {
            go.disabled = true;
            notes.innerHTML = '';
            statusMain.textContent = 'Checking…';
            statusSub.textContent = '';

            request('POST', 'MediaOptimizer/Move/Preview', { ItemIds: [info.ItemId], DestinationPath: chosen })
                .then(function (plan) {
                    (plan.Blockers || []).forEach(function (b) { notes.appendChild(warningBox('blocker', b)); });

                    var entry = (plan.Items || [])[0];
                    if (entry && !entry.CanMove && entry.SkippedReason) {
                        notes.appendChild(warningBox('blocker', entry.SkippedReason));
                    }

                    if (!plan.IsRunnable) {
                        statusMain.textContent = 'Cannot move it there';
                        statusSub.textContent = '';
                        return;
                    }

                    (plan.Notes || []).forEach(function (n) { notes.appendChild(warningBox('info', n)); });

                    statusMain.textContent = bytes(plan.TotalBytes) + ' → ' + chosen;
                    statusSub.textContent = (entry && entry.DestinationPath ? entry.DestinationPath + ' · ' : '') +
                        (plan.FreeBytesAfter !== null && plan.FreeBytesAfter !== undefined
                            ? bytes(plan.FreeBytesAfter) + ' would be left free'
                            : '');
                    go.disabled = false;
                })
                .catch(function (e) {
                    statusMain.textContent = 'Could not check that drive';
                    statusSub.textContent = e.message;
                });
        }

        go.addEventListener('click', function () {
            go.disabled = true;
            go.textContent = 'Starting…';
            request('POST', 'MediaOptimizer/Move', { ItemIds: [info.ItemId], DestinationPath: chosen })
                .then(function (result) {
                    var job = (result.Jobs || [])[0];
                    if (!job) {
                        go.textContent = 'Move file';
                        notes.appendChild(warningBox('blocker', 'The server accepted the request but queued nothing.'));
                        return;
                    }
                    showMoveProgress(shell, pane, foot, job);
                })
                .catch(function (e) {
                    go.disabled = false;
                    go.textContent = 'Move file';
                    notes.appendChild(warningBox('blocker', e.message));
                });
        });
    }

    function showMoveProgress(shell, pane, foot, job) {
        pane.innerHTML = '';
        foot.innerHTML = '';

        pane.appendChild(el('div', 'mopt-sec', 'Move queued'));
        var status = el('div', 'mopt-estimate-main', 'Waiting for a worker…');
        var sub = el('div', 'mopt-estimate-sub', job.SourcePath + ' → ' + job.DestinationPath);
        var bar = el('div', 'mopt-bar');
        var fill = el('i');
        fill.style.width = '0%';
        bar.appendChild(fill);
        pane.appendChild(status);
        pane.appendChild(sub);
        pane.appendChild(bar);

        pane.appendChild(warningBox('info',
            'This runs on the server. You can close this — progress stays visible under ' +
            'Dashboard → Move Media, and the original is only deleted once the copy has been checked.'));

        var cancelBtn = el('button', 'mopt-btn mopt-btn-danger', 'Cancel move');
        cancelBtn.addEventListener('click', function () {
            cancelBtn.disabled = true;
            request('DELETE', 'MediaOptimizer/Move/Jobs/' + job.Id).catch(function () {});
        });
        foot.appendChild(cancelBtn);

        var closeBtn = el('button', 'mopt-btn', 'Close');
        closeBtn.addEventListener('click', function () { stop(); shell.close(); });
        foot.appendChild(closeBtn);

        var timer = setInterval(poll, 1500);
        function stop() { clearInterval(timer); }
        poll();

        function poll() {
            request('GET', 'MediaOptimizer/Move/Jobs/' + job.Id).then(function (j) {
                var pct = Math.round(j.ProgressPercent || 0);
                fill.style.width = pct + '%';
                status.textContent = ({
                    Queued: 'Waiting for a worker…', Preflight: 'Running safety checks…',
                    Copying: 'Copying — ' + pct + '%', Verifying: 'Checking the copy…',
                    Finalizing: 'Updating the library…', Completed: 'Done',
                    Failed: 'Failed', Cancelled: 'Cancelled', Interrupted: 'Interrupted'
                })[j.Status] || j.Status;

                var bits = [j.DestinationPath];
                if (j.Status === 'Copying' && j.BytesPerSecond) {
                    bits.push(bytes(j.BytesPerSecond) + '/s');
                    if (j.EtaSeconds) { bits.push(duration(j.EtaSeconds) + ' remaining'); }
                }
                if (j.Status === 'Completed') {
                    if (j.WasInstantRename) { bits.push('renamed on the same drive'); }
                    if (j.HashVerified === true) { bits.push('checked byte for byte'); }
                }
                if (j.Error) { bits.push(j.Error); }
                sub.textContent = bits.join(' · ');

                if (['Completed', 'Failed', 'Cancelled', 'Interrupted'].indexOf(j.Status) >= 0) {
                    stop();
                    cancelBtn.style.display = 'none';
                }
            }).catch(function () { /* transient; the next tick retries */ });
        }
    }

    // ---------------------------------------------------------------- progress

    function showProgress(shell, pane, foot, job) {
        pane.innerHTML = '';
        foot.innerHTML = '';

        pane.appendChild(el('div', 'mopt-sec', 'Conversion queued'));
        var status = el('div', 'mopt-estimate-main', 'Waiting for a worker…');
        var sub = el('div', 'mopt-estimate-sub', '');
        var bar = el('div', 'mopt-bar');
        var fill = el('i');
        fill.style.width = '0%';
        bar.appendChild(fill);
        pane.appendChild(status);
        pane.appendChild(sub);
        pane.appendChild(bar);

        pane.appendChild(warningBox('info',
            'This runs on the server. You can close this — progress stays visible under ' +
            'Dashboard → Media Optimizer, and the original is not touched until the result passes verification.'));

        var cancelBtn = el('button', 'mopt-btn mopt-btn-danger', 'Cancel conversion');
        cancelBtn.addEventListener('click', function () {
            cancelBtn.disabled = true;
            request('DELETE', 'MediaOptimizer/Jobs/' + job.Id).catch(function () {});
        });
        foot.appendChild(cancelBtn);

        var closeBtn = el('button', 'mopt-btn', 'Close');
        closeBtn.addEventListener('click', function () { stop(); shell.close(); });
        foot.appendChild(closeBtn);

        var timer = setInterval(poll, 1500);
        function stop() { clearInterval(timer); }
        poll();

        function poll() {
            request('GET', 'MediaOptimizer/Jobs/' + job.Id).then(function (j) {
                var pct = Math.round(j.ProgressPercent || 0);
                fill.style.width = pct + '%';
                status.textContent = ({
                    Queued: 'Waiting for a worker…', Preflight: 'Running safety checks…',
                    Encoding: 'Encoding — ' + pct + '%', Verifying: 'Checking the result…',
                    Applying: 'Moving the file into place…', Completed: 'Done',
                    Failed: 'Failed', Cancelled: 'Cancelled', Interrupted: 'Interrupted'
                })[j.Status] || j.Status;

                var bits = [];
                if (j.Speed) { bits.push(j.Speed.toFixed(2) + '× realtime'); }
                if (j.EtaSeconds && j.Status === 'Encoding') { bits.push(duration(j.EtaSeconds) + ' remaining'); }
                if (j.Status === 'Completed') {
                    bits.push(bytes(j.SourceSizeBytes) + ' → ' + bytes(j.OutputSizeBytes));
                    if (j.LosslessVerified === true) { bits.push('bit-exactness verified'); }
                }
                if (j.Error) { bits.push(j.Error); }
                sub.textContent = bits.join(' · ');

                if (['Completed', 'Failed', 'Cancelled', 'Interrupted'].indexOf(j.Status) >= 0) {
                    stop();
                    cancelBtn.style.display = 'none';
                }
            }).catch(function () { /* transient; the next tick retries */ });
        }
    }

    // --------------------------------------------------------------- bootstrap

    window.MediaOptimizer = {
        open: openDialog,
        openBatch: openBatch,
        openMove: openMoveDialog,
        ready: true,
        // Tests and the dashboard need a way through the shadow boundary.
        shadowRoot: function () {
            var host = document.getElementById('mopt-host');
            return host ? host.shadowRoot : null;
        },
        grafted: function () {
            return {
                menu: !!document.querySelector('[data-id="mediaoptimizer"]'),
                move: !!document.querySelector('[data-id="mediaoptimizer-move"]'),
                player: !!document.querySelector('.btnMediaOptimizer')
            };
        }
    };

    var startAttempts = 0;
    function start() {
        if (!apiClient()) {
            if (++startAttempts > 60) {
                console.warn('[MediaOptimizer] ApiClient never appeared; in-app buttons disabled. '
                    + 'Use Dashboard → Media Optimizer.');
                return;
            }
            setTimeout(start, 500);
            return;
        }

        // The move entry is admin-only, and the action sheet is built and thrown away too fast
        // to ask the server at the moment it opens. One request at startup answers it for every
        // sheet afterwards; the rescan puts the entry into a sheet that opened before it landed.
        if (!state.capabilities) {
            request('GET', 'MediaOptimizer/Capabilities').then(function (caps) {
                state.capabilities = caps;
                scan();
            }).catch(function () { /* the optimize entry does not depend on this */ });
        }

        try {
            observer.observe(document.body, { childList: true, subtree: true });
            scan();
        } catch (e) {
            warnOnce('observe', 'DOM integration unavailable: ' + e.message + ' The dashboard page is unaffected.');
        }

        console.log('[MediaOptimizer] client ready');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
