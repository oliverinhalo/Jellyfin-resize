/*
 * Media Optimizer — Jellyfin web client integration.
 *
 * Jellyfin has no plugin API for its web client, so this script grafts itself onto the DOM:
 * jellyfin-web's own modules are inside a webpack bundle and are not reachable from here.
 * Every selector below is private to jellyfin-web and verified against 10.11.x. If a future
 * release moves them, every hook fails closed and logs once — the plugin's dashboard page
 * keeps working regardless.
 */
(function () {
    'use strict';

    if (window.__mediaOptimizerLoaded) { return; }
    window.__mediaOptimizerLoaded = true;

    var CSS = /*__MEDIAOPTIMIZER_CSS__*/;
    var GUID_RE = /^[0-9a-f]{32}$|^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

    var state = {
        lastItemId: null,      // item the context menu was most recently opened against
        capabilities: null,
        warned: {}
    };

    function warnOnce(key, message) {
        if (state.warned[key]) { return; }
        state.warned[key] = true;
        console.warn('[MediaOptimizer] ' + message);
    }

    // ---------------------------------------------------------------- utilities

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) { node.className = className; }
        if (text !== undefined && text !== null) { node.textContent = String(text); }
        return node;
    }

    function bytes(n) {
        if (n === null || n === undefined || isNaN(n)) { return '—'; }
        if (n < 1024) { return n + ' B'; }
        var units = ['KiB', 'MiB', 'GiB', 'TiB'];
        var v = n / 1024;
        var i = 0;
        while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
        return v.toFixed(v < 10 ? 2 : 1) + ' ' + units[i];
    }

    function bitrate(info) {
        if (!info || info.Bps === null || info.Bps === undefined) { return '—'; }
        var mbps = info.Bps / 1000000;
        var text = mbps >= 1 ? mbps.toFixed(2) + ' Mb/s' : Math.round(info.Bps / 1000) + ' kb/s';
        // Derived figures are marked so a computed number is never mistaken for a measured one.
        return (info.Source === 'Derived' ? '≈ ' : '') + text;
    }

    function duration(seconds) {
        if (!seconds) { return '—'; }
        var s = Math.round(seconds);
        var h = Math.floor(s / 3600);
        var m = Math.floor((s % 3600) / 60);
        var r = s % 60;
        return (h > 0 ? h + 'h ' : '') + m + 'm ' + (h > 0 ? '' : r + 's');
    }

    function apiClient() {
        return window.ApiClient || (window.Emby && window.Emby.ApiClient) || null;
    }

    function request(method, path, body) {
        var client = apiClient();
        if (!client) { return Promise.reject(new Error('ApiClient is not available')); }

        var options = {
            type: method,
            url: client.getUrl(path),
            headers: { accept: 'application/json' }
        };

        if (body !== undefined) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
        }

        // Jellyfin's ApiClient rejects with a plain XHR-ish object; normalise the message so the
        // dialog can show something a person can act on.
        return client.ajax(options).then(function (response) {
            if (typeof response === 'string') {
                return response.length ? JSON.parse(response) : null;
            }
            return response;
        }, function (error) {
            var message = 'Request failed';
            if (error && error.status === 403) {
                message = 'You need administrator rights to do that.';
            } else if (error && error.statusText) {
                message = error.statusText;
            }
            var wrapped = new Error(message);
            wrapped.status = error && error.status;
            wrapped.raw = error;
            return Promise.reject(wrapped);
        });
    }

    // ------------------------------------------------------- item id resolution

    // The action sheet does not carry the item it was opened against, so remember the id from
    // the element that was clicked, captured before jellyfin-web handles the event.
    function rememberItemFromEvent(event) {
        var node = event.target;
        while (node && node !== document.body) {
            if (node.getAttribute) {
                var id = node.getAttribute('data-id');
                if (id && GUID_RE.test(id)) {
                    state.lastItemId = id;
                    return;
                }
            }
            node = node.parentNode;
        }
    }

    document.addEventListener('pointerdown', rememberItemFromEvent, true);
    document.addEventListener('contextmenu', rememberItemFromEvent, true);

    // In the player there is no clicked element to read, so ask the server what this device
    // is currently playing. This uses the documented Sessions API rather than player internals.
    function currentlyPlayingItemId() {
        var client = apiClient();
        if (!client) { return Promise.resolve(null); }

        var deviceId = client.deviceId && client.deviceId();
        return request('GET', 'Sessions' + (deviceId ? '?deviceId=' + encodeURIComponent(deviceId) : ''))
            .then(function (sessions) {
                if (!sessions || !sessions.length) { return null; }
                for (var i = 0; i < sessions.length; i++) {
                    if (sessions[i].NowPlayingItem && sessions[i].NowPlayingItem.Id) {
                        return sessions[i].NowPlayingItem.Id;
                    }
                }
                return null;
            })
            .catch(function () { return null; });
    }

    // ------------------------------------------------------------- DOM grafting

    function addContextMenuEntry(sheet) {
        if (sheet.querySelector('[data-id="mediaoptimizer"]')) { return; }

        var scroller = sheet.querySelector('.actionSheetScroller') || sheet.querySelector('.actionSheetContent');
        if (!scroller) {
            warnOnce('sheet', 'Action sheet layout not recognised; the menu entry was not added.');
            return;
        }

        // Only offer it where an item was actually clicked.
        if (!state.lastItemId) { return; }
        var itemId = state.lastItemId;

        // Built with insertAdjacentHTML so jellyfin-web's customised built-in <button is="…">
        // elements upgrade; createElement + className would leave them un-upgraded.
        scroller.insertAdjacentHTML('beforeend',
            '<button is="emby-button" type="button" class="listItem listItem-button actionSheetMenuItem" data-id="mediaoptimizer">' +
            '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons tune" aria-hidden="true"></span>' +
            '<div class="listItemBody actionsheetListItemBody">' +
            '<div class="listItemBodyText actionSheetItemText">Optimize file…</div>' +
            '</div></button>');

        var button = scroller.querySelector('[data-id="mediaoptimizer"]');
        if (!button) { return; }

        button.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            closeActionSheet(sheet);
            openDialog(itemId);
        }, true);
    }

    function closeActionSheet(sheet) {
        // Prefer the sheet's own dismissal so jellyfin-web can clean up its backdrop.
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

        if (anchor) {
            anchor.insertAdjacentHTML('beforebegin', markup);
        } else {
            bar.insertAdjacentHTML('beforeend', markup);
        }

        var button = bar.querySelector('.btnMediaOptimizer');
        if (!button) { return; }

        button.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            currentlyPlayingItemId().then(function (id) {
                if (id) {
                    openDialog(id);
                } else {
                    warnOnce('nowplaying', 'Could not determine the currently playing item.');
                }
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
        // Coalesce the burst of mutations jellyfin-web emits when a dialog opens.
        requestAnimationFrame(function () { scanQueued = false; scan(); });
    });

    // ---------------------------------------------------------------- the dialog

    function openDialog(itemId) {
        var overlay = el('div', 'mopt-overlay');
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Optimize media file');

        var dialog = el('div', 'mopt-dialog');
        dialog.appendChild(el('div', 'mopt-loading', 'Analysing file…'));
        overlay.appendChild(dialog);

        function close() {
            document.removeEventListener('keydown', onKey, true);
            if (overlay.parentNode) { overlay.parentNode.removeChild(overlay); }
        }

        function onKey(event) {
            if (event.key === 'Escape') { event.stopPropagation(); close(); }
        }

        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) { close(); }
        });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(overlay);

        Promise.all([
            request('GET', 'MediaOptimizer/Analyze/' + itemId),
            state.capabilities
                ? Promise.resolve(state.capabilities)
                : request('GET', 'MediaOptimizer/Capabilities')
        ]).then(function (results) {
            state.capabilities = results[1];
            renderDialog(dialog, results[0], results[1], close);
        }).catch(function (error) {
            dialog.innerHTML = '';
            var box = el('div', 'mopt-loading');
            box.appendChild(el('p', null, 'Could not analyse this file.'));
            box.appendChild(el('p', 'mopt-muted', error.message));
            dialog.appendChild(box);
        });
    }

    function renderDialog(dialog, analysis, caps, close) {
        dialog.innerHTML = '';

        // ---- header
        var head = el('div', 'mopt-head');
        head.appendChild(el('span', 'material-icons tune'));
        head.appendChild(el('h2', null, analysis.Name || 'Media file'));
        var closeBtn = el('button', 'mopt-close', '×');
        closeBtn.setAttribute('aria-label', 'Close');
        closeBtn.addEventListener('click', close);
        head.appendChild(closeBtn);
        dialog.appendChild(head);

        var body = el('div', 'mopt-body');
        var left = el('div', 'mopt-pane mopt-pane-left');
        var right = el('div', 'mopt-pane');
        body.appendChild(left);
        body.appendChild(right);
        dialog.appendChild(body);

        renderCurrentFile(left, analysis);

        var foot = el('div', 'mopt-foot');
        dialog.appendChild(foot);

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

        renderTargetForm(right, foot, analysis, caps, close);
    }

    function renderCurrentFile(pane, a) {
        pane.appendChild(el('div', 'mopt-sec', 'Current file'));

        var container = el('dl', 'mopt-kv');
        function kv(label, value) {
            container.appendChild(el('dt', null, label));
            container.appendChild(el('dd', null, value));
        }

        kv('Container', (a.Container || '—').toUpperCase());
        kv('File size', bytes(a.SizeBytes));
        kv('Duration', duration(a.DurationSeconds));
        kv('Overall bitrate', bitrate(a.OverallBitrate));
        pane.appendChild(container);

        if (a.Video) {
            pane.appendChild(el('div', 'mopt-sec', 'Video'));
            var v = el('dl', 'mopt-kv');
            function vkv(label, value) {
                v.appendChild(el('dt', null, label));
                v.appendChild(el('dd', null, value));
            }
            vkv('Codec', (a.Video.Codec || '—').toUpperCase() + (a.Video.Profile ? ' · ' + a.Video.Profile : ''));
            vkv('Resolution', a.Video.Width && a.Video.Height ? a.Video.Width + ' × ' + a.Video.Height : '—');
            vkv('Bit depth', a.Video.BitDepth ? a.Video.BitDepth + '-bit' : '—');
            vkv('Frame rate', a.Video.FrameRate ? a.Video.FrameRate.toFixed(3).replace(/\.?0+$/, '') + ' fps' + (a.Video.IsVariableFrameRate ? ' (VFR)' : '') : '—');
            vkv('Dynamic range', a.Video.Range || 'SDR');
            vkv('Bitrate', bitrate(a.Video.Bitrate));
            pane.appendChild(v);

            if (a.Video.Bitrate && a.Video.Bitrate.Source === 'Derived') {
                var measure = el('button', 'mopt-btn', 'Measure exactly');
                measure.style.fontSize = '12px';
                measure.style.padding = '5px 11px';
                measure.title = 'Reads the whole file to count packet sizes. Accurate, but slow on large files.';
                measure.addEventListener('click', function () {
                    measure.disabled = true;
                    measure.textContent = 'Measuring…';
                    request('GET', 'MediaOptimizer/MeasureBitrate/' + a.ItemId).then(function (result) {
                        a.Video.Bitrate = result;
                        v.lastChild.textContent = bitrate(result);
                        measure.parentNode.removeChild(measure);
                    }).catch(function () {
                        measure.textContent = 'Measurement failed';
                    });
                });
                pane.appendChild(measure);
            }
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

                var meta = [
                    t.ChannelLayout || (t.Channels ? t.Channels + 'ch' : null),
                    t.Language,
                    t.SampleRate ? Math.round(t.SampleRate / 1000) + ' kHz' : null,
                    bitrate(t.Bitrate)
                ].filter(Boolean).join(' · ');
                box.appendChild(el('div', 'mopt-track-meta', meta));
                pane.appendChild(box);
            });
        }

        if (a.Subtitles && a.Subtitles.length) {
            pane.appendChild(el('div', 'mopt-sec', 'Subtitles (' + a.Subtitles.length + ')'));
            var subs = a.Subtitles.map(function (s) {
                return (s.Language || 'und') + ' ' + (s.Codec || '').toUpperCase() + (s.IsExternal ? ' (external)' : '');
            }).join(', ');
            pane.appendChild(el('div', 'mopt-track-meta', subs));
        }

        pane.appendChild(el('div', 'mopt-sec', 'Storage'));
        var s = el('dl', 'mopt-kv');
        s.appendChild(el('dt', null, 'Attachments'));
        s.appendChild(el('dd', null, a.AttachmentCount || 0));
        s.appendChild(el('dt', null, 'Chapters'));
        s.appendChild(el('dd', null, a.ChapterCount || 0));
        s.appendChild(el('dt', null, 'Writable'));
        s.appendChild(el('dd', null, a.IsWritable ? 'yes' : 'no'));
        pane.appendChild(s);
        pane.appendChild(el('div', 'mopt-track-meta', a.Path || ''));
    }

    function warningBox(level, message) {
        var key = String(level || 'info').toLowerCase();
        var box = el('div', 'mopt-warn mopt-warn-' + key);
        box.appendChild(el('div', null, message));
        return box;
    }

    // ---- the target form, presets and live estimate -------------------------

    var PRESETS = {
        Balanced: { label: 'Balanced', crfOffset: 0, hint: 'Meaningful savings at close to transparent quality.' },
        MaximumCompression: { label: 'Maximum compression', crfOffset: 6, hint: 'Smallest file. Quality loss will be visible.' },
        Archive: { label: 'Archive', crfOffset: -5, hint: 'Preserve quality, modernise the codec. Savings are smaller.' },
        LosslessOnly: { label: 'Lossless only', crfOffset: 0, hint: 'Bit-exact operations only — no lossy re-encoding anywhere.' },
        Custom: { label: 'Custom', crfOffset: 0, hint: '' }
    };

    function renderTargetForm(pane, foot, a, caps, close) {
        var req = {
            ItemId: a.ItemId,
            Strategy: 'Balanced',
            Container: 'mkv',
            Video: 'Encode',
            VideoCodec: null,
            TargetHeight: null,
            BitDepth: a.Video ? a.Video.BitDepth : 8,
            RateControl: 'ConstantQuality',
            Quality: null,
            Preset: 'medium',
            UseHardware: false,
            AudioTracks: (a.Audio || []).map(function (t) {
                return { Index: t.Index, Action: 'Copy', Codec: null, BitrateBps: null, Channels: null };
            }),
            KeepSubtitleIndexes: null,
            KeepAttachments: true,
            KeepChapters: true,
            StripFillerData: false,
            AcceptDolbyVisionLoss: false,
            AcceptObjectAudioLoss: false,
            OutputPolicy: 'Sidecar'
        };

        var cpuEncoders = caps.VideoEncoders.filter(function (e) { return !e.IsHardware && e.Codec !== 'ffv1'; });
        var preferred = cpuEncoders.filter(function (e) { return e.Codec === 'hevc'; })[0] || cpuEncoders[0];
        if (preferred) { req.VideoCodec = preferred.Name; }

        pane.appendChild(el('div', 'mopt-sec', 'Strategy'));
        var presetRow = el('div', 'mopt-presets');
        var hint = el('div', 'mopt-track-meta');
        hint.style.marginBottom = '14px';

        Object.keys(PRESETS).forEach(function (key) {
            var b = el('button', 'mopt-preset', PRESETS[key].label);
            b.type = 'button';
            b.setAttribute('aria-pressed', key === 'Balanced' ? 'true' : 'false');
            b.addEventListener('click', function () {
                Array.prototype.forEach.call(presetRow.children, function (c) { c.setAttribute('aria-pressed', 'false'); });
                b.setAttribute('aria-pressed', 'true');
                req.Strategy = key;
                hint.textContent = PRESETS[key].hint;
                applyStrategy(key);
                rebuild();
            });
            presetRow.appendChild(b);
        });
        pane.appendChild(presetRow);
        hint.textContent = PRESETS.Balanced.hint;
        pane.appendChild(hint);

        var formHost = el('div');
        pane.appendChild(formHost);

        var warnHost = el('div');
        pane.appendChild(warnHost);

        function applyStrategy(key) {
            if (key === 'LosslessOnly') {
                var ffv1 = caps.VideoEncoders.filter(function (e) { return e.Name === 'ffv1'; })[0];
                if (ffv1 && a.Video && a.Video.IsLosslessCodec) { req.VideoCodec = ffv1.Name; }
                // In lossless mode the video stream is never re-encoded unless the source codec
                // is itself lossless — re-encoding lossy video "losslessly" always inflates it.
                req.Video = (a.Video && a.Video.IsLosslessCodec) ? 'Encode' : 'Copy';
                req.RateControl = 'Lossless';
                req.Container = 'mkv';
                req.AudioTracks.forEach(function (t) {
                    var track = findAudio(a, t.Index);
                    if (track && track.IsLossless && (track.Channels || 0) <= 8) {
                        t.Action = 'Encode';
                        t.Codec = 'flac';
                        t.BitrateBps = null;
                        t.Channels = null;
                    } else {
                        t.Action = 'Copy';
                        t.Codec = null;
                    }
                });
                return;
            }

            req.Video = 'Encode';
            req.RateControl = 'ConstantQuality';
            var enc = caps.VideoEncoders.filter(function (e) { return e.Name === req.VideoCodec; })[0];
            var base = enc && enc.Codec === 'hevc' ? 28 : enc && enc.Codec === 'av1' ? 32 : 23;
            req.Quality = base + (PRESETS[key] ? PRESETS[key].crfOffset : 0);

            req.AudioTracks.forEach(function (t) {
                if (key === 'MaximumCompression') {
                    t.Action = 'Encode';
                    t.Codec = pickAudioEncoder(caps);
                    t.BitrateBps = 128000;
                } else {
                    t.Action = 'Copy';
                    t.Codec = null;
                    t.BitrateBps = null;
                }
            });
        }

        applyStrategy('Balanced');

        function rebuild() {
            formHost.innerHTML = '';
            buildFields(formHost, req, a, caps, rebuild);
            refreshEstimate();
        }

        // ---- footer: live estimate and the action buttons
        var estimate = el('div', 'mopt-estimate');
        var estMain = el('div', 'mopt-estimate-main', 'Estimating…');
        var estSub = el('div', 'mopt-estimate-sub', '');
        estimate.appendChild(estMain);
        estimate.appendChild(estSub);
        foot.appendChild(estimate);

        var cancelBtn = el('button', 'mopt-btn', 'Cancel');
        cancelBtn.addEventListener('click', close);
        foot.appendChild(cancelBtn);

        var startBtn = el('button', 'mopt-btn mopt-btn-primary', 'Start conversion');
        foot.appendChild(startBtn);

        var estimateTimer = null;
        function refreshEstimate() {
            clearTimeout(estimateTimer);
            estimateTimer = setTimeout(function () {
                request('POST', 'MediaOptimizer/Estimate', req).then(function (result) {
                    renderEstimate(result);
                }).catch(function (error) {
                    estMain.textContent = 'Estimate unavailable';
                    estSub.textContent = error.message;
                });
            }, 220);
        }

        function renderEstimate(result) {
            warnHost.innerHTML = '';
            var blockers = 0;

            (result.Warnings || []).forEach(function (w) {
                if (w.Level === 'Blocker') { blockers++; }
                var box = warningBox(w.Level, w.Message);

                // Losing Dolby Vision or object audio is a decision, not just a notice, so the
                // consent lives on the warning itself rather than buried in the form.
                if (w.Code === 'DOLBY_VISION_LOSS') {
                    box.appendChild(consentCheckbox('I understand Dolby Vision will be lost', function (on) {
                        req.AcceptDolbyVisionLoss = on;
                        refreshEstimate();
                    }));
                }
                if (w.Code === 'OBJECT_AUDIO_LOSS' && !req.AcceptObjectAudioLoss) {
                    box.appendChild(consentCheckbox('I understand Atmos / DTS:X objects will be lost', function (on) {
                        req.AcceptObjectAudioLoss = on;
                    }));
                }
                warnHost.appendChild(box);
            });

            var saving = Math.round((result.SavingFraction || 0) * 100);
            var sign = saving >= 0 ? '−' : '+';
            estMain.textContent = bytes(result.CurrentSizeBytes) + '  →  ' + bytes(result.EstimatedSizeBytes) +
                '   (' + sign + Math.abs(saving) + '%)';

            var parts = [];
            parts.push('range ' + bytes(result.EstimatedSizeLowBytes) + ' – ' + bytes(result.EstimatedSizeHighBytes));
            if (result.EstimatedSeconds) { parts.push('about ' + duration(result.EstimatedSeconds) + ' to encode'); }
            parts.push(result.IsLossless ? 'bit-exact — verified by hash after encoding' : 'lossy');
            estSub.textContent = parts.join(' · ');

            startBtn.disabled = blockers > 0;
            startBtn.title = blockers > 0 ? 'Resolve the blocking issues above first.' : '';
        }

        startBtn.addEventListener('click', function () {
            startBtn.disabled = true;
            startBtn.textContent = 'Queueing…';
            request('POST', 'MediaOptimizer/Jobs', req).then(function (job) {
                showProgress(pane, foot, job, close);
            }).catch(function (error) {
                startBtn.disabled = false;
                startBtn.textContent = 'Start conversion';
                warnHost.insertBefore(warningBox('blocker', error.message), warnHost.firstChild);
            });
        });

        rebuild();
    }

    function consentCheckbox(label, onChange) {
        var wrap = el('label', 'mopt-check');
        wrap.style.marginTop = '8px';
        var input = document.createElement('input');
        input.type = 'checkbox';
        input.addEventListener('change', function () { onChange(input.checked); });
        wrap.appendChild(input);
        wrap.appendChild(el('span', null, label));
        return wrap;
    }

    function findAudio(a, index) {
        return (a.Audio || []).filter(function (t) { return t.Index === index; })[0];
    }

    function pickAudioEncoder(caps) {
        var names = caps.AudioEncoders.map(function (e) { return e.Name; });
        if (names.indexOf('libopus') >= 0) { return 'libopus'; }
        if (names.indexOf('aac') >= 0) { return 'aac'; }
        return names[0] || null;
    }

    function selectField(host, label, options, value, onChange) {
        var field = el('div', 'mopt-field');
        field.appendChild(el('label', null, label));
        var select = document.createElement('select');
        options.forEach(function (o) {
            var opt = document.createElement('option');
            opt.value = o.value === null ? '' : String(o.value);
            opt.textContent = o.label;
            if (String(o.value) === String(value) || (o.value === null && (value === null || value === undefined))) {
                opt.selected = true;
            }
            select.appendChild(opt);
        });
        select.addEventListener('change', function () {
            onChange(select.value === '' ? null : select.value);
        });
        field.appendChild(select);
        host.appendChild(field);
        return select;
    }

    function buildFields(host, req, a, caps, rebuild) {
        var lossless = req.Strategy === 'LosslessOnly';

        if (lossless) {
            host.appendChild(warningBox('info',
                'Lossless mode uses only operations that preserve the data exactly: removing tracks you ' +
                'do not want, re-encoding lossless audio to FLAC, and stripping filler data. ' +
                (a.Video && a.Video.IsLosslessCodec
                    ? 'This file uses a lossless video codec, so the video can also be re-encoded losslessly.'
                    : 'The video stream is copied untouched — re-encoding already-compressed video "losslessly" always makes the file larger, so it is not offered.')));

            // Only a source that is already lossless can be re-encoded losslessly, so the codec
            // choice appears exactly when it is actually usable.
            if (a.Video && a.Video.IsLosslessCodec) {
                var llRow = el('div', 'mopt-row');
                var llEncoders = caps.VideoEncoders.filter(function (e) {
                    return e.Name === 'ffv1' || e.Name === 'libx265' || e.Name === 'libx264';
                }).map(function (e) { return { value: e.Name, label: e.DisplayName }; });
                if (llEncoders.length) {
                    selectField(llRow, 'Lossless video codec', llEncoders, req.VideoCodec, function (v) {
                        req.VideoCodec = v; rebuild();
                    });
                    host.appendChild(llRow);
                }
            }
        }

        // ---- video
        if (a.Video && !lossless) {
            host.appendChild(el('div', 'mopt-sec', 'Video'));

            var row1 = el('div', 'mopt-row');
            selectField(row1, 'Action', [
                { value: 'Encode', label: 'Re-encode' },
                { value: 'Copy', label: 'Keep as-is (stream copy)' }
            ], req.Video, function (v) { req.Video = v; rebuild(); });

            if (req.Video === 'Encode') {
                var encoders = caps.VideoEncoders
                    .filter(function (e) { return e.Codec !== 'ffv1'; })
                    .map(function (e) { return { value: e.Name, label: e.DisplayName }; });
                selectField(row1, 'Codec', encoders, req.VideoCodec, function (v) { req.VideoCodec = v; rebuild(); });
            }
            host.appendChild(row1);

            if (req.Video === 'Encode') {
                var row2 = el('div', 'mopt-row');

                var heights = [{ value: null, label: 'Original (' + (a.Video.Height || '?') + 'p)' }];
                [720, 1080, 1440, 2160].forEach(function (h) {
                    if (!a.Video.Height || h < a.Video.Height) {
                        heights.push({ value: h, label: h === 1440 ? '1440p (2K)' : h === 2160 ? '2160p (4K)' : h + 'p' });
                    }
                });
                heights.push({ value: 'custom', label: 'Custom…' });

                selectField(row2, 'Resolution', heights, req.TargetHeight, function (v) {
                    if (v === 'custom') {
                        var entered = prompt('Target height in pixels', String(a.Video.Height || 1080));
                        var n = parseInt(entered, 10);
                        req.TargetHeight = isNaN(n) ? null : n;
                    } else {
                        req.TargetHeight = v === null ? null : parseInt(v, 10);
                    }
                    rebuild();
                });

                var enc = caps.VideoEncoders.filter(function (e) { return e.Name === req.VideoCodec; })[0];
                var depths = [{ value: 8, label: '8-bit' }];
                if (!enc || enc.Supports10Bit) { depths.push({ value: 10, label: '10-bit' }); }
                selectField(row2, 'Bit depth', depths, req.BitDepth, function (v) {
                    req.BitDepth = parseInt(v, 10); rebuild();
                });

                if (enc && enc.Presets && enc.Presets.length) {
                    selectField(row2, 'Preset', enc.Presets.map(function (p) { return { value: p, label: p }; }),
                        req.Preset, function (v) { req.Preset = v; rebuild(); });
                }
                host.appendChild(row2);

                var row3 = el('div', 'mopt-row');
                selectField(row3, 'Rate control', [
                    { value: 'ConstantQuality', label: 'Constant quality (CRF)' },
                    { value: 'AverageBitrate', label: 'Average bitrate' },
                    { value: 'TargetSize', label: 'Target file size' }
                ], req.RateControl, function (v) { req.RateControl = v; rebuild(); });

                if (req.RateControl === 'ConstantQuality') {
                    var q = el('div', 'mopt-field');
                    q.appendChild(el('label', null, 'Quality (CRF — lower is better)'));
                    var qi = document.createElement('input');
                    qi.type = 'number'; qi.min = '0'; qi.max = '51';
                    qi.value = req.Quality === null || req.Quality === undefined ? '' : req.Quality;
                    qi.addEventListener('change', function () {
                        req.Quality = qi.value === '' ? null : parseInt(qi.value, 10); rebuild();
                    });
                    q.appendChild(qi);
                    row3.appendChild(q);
                    host.appendChild(row3);
                } else if (req.RateControl === 'AverageBitrate') {
                    var b = el('div', 'mopt-field');
                    b.appendChild(el('label', null, 'Video bitrate (Mb/s)'));
                    var bi = document.createElement('input');
                    bi.type = 'number'; bi.min = '0.1'; bi.step = '0.1';
                    bi.value = req.VideoBitrateBps ? (req.VideoBitrateBps / 1000000) : '';
                    bi.addEventListener('change', function () {
                        req.VideoBitrateBps = bi.value === '' ? null : Math.round(parseFloat(bi.value) * 1000000);
                        rebuild();
                    });
                    b.appendChild(bi);
                    row3.appendChild(b);
                    host.appendChild(row3);
                } else {
                    var t = el('div', 'mopt-field');
                    t.appendChild(el('label', null, 'Target size (MiB)'));
                    var ti = document.createElement('input');
                    ti.type = 'number'; ti.min = '1';
                    ti.value = req.TargetSizeBytes ? Math.round(req.TargetSizeBytes / 1048576) : '';
                    ti.addEventListener('change', function () {
                        req.TargetSizeBytes = ti.value === '' ? null : parseInt(ti.value, 10) * 1048576;
                        rebuild();
                    });
                    t.appendChild(ti);
                    row3.appendChild(t);
                    host.appendChild(row3);
                }

                var hwEncoders = caps.VideoEncoders.filter(function (e) { return e.IsHardware; });
                if (hwEncoders.length) {
                    var hw = el('label', 'mopt-check');
                    var hwi = document.createElement('input');
                    hwi.type = 'checkbox';
                    hwi.checked = !!req.UseHardware;
                    hwi.addEventListener('change', function () { req.UseHardware = hwi.checked; rebuild(); });
                    hw.appendChild(hwi);
                    hw.appendChild(el('span', null, 'Use hardware encoding — much faster, but a noticeably larger file at the same visual quality'));
                    host.appendChild(hw);
                }
            }
        }

        // ---- audio
        if (a.Audio && a.Audio.length) {
            host.appendChild(el('div', 'mopt-sec', 'Audio tracks'));
            a.Audio.forEach(function (track) {
                var entry = req.AudioTracks.filter(function (x) { return x.Index === track.Index; })[0];
                if (!entry) { return; }

                var box = el('div', 'mopt-track');
                var label = (track.Codec || '?').toUpperCase() + ' · ' +
                    (track.ChannelLayout || (track.Channels || '?') + 'ch') +
                    (track.Language ? ' · ' + track.Language : '');
                box.appendChild(el('div', 'mopt-track-head')).appendChild(el('strong', null, label));

                var row = el('div', 'mopt-row');
                row.style.marginBottom = '0';

                var actions = [
                    { value: 'Copy', label: 'Keep as-is' },
                    { value: 'Encode', label: 'Re-encode' },
                    { value: 'Drop', label: 'Remove' }
                ];
                selectField(row, 'Action', actions, entry.Action, function (v) {
                    entry.Action = v;
                    if (v !== 'Encode') { entry.Codec = null; entry.BitrateBps = null; }
                    rebuild();
                });

                if (entry.Action === 'Encode') {
                    var audioOpts = caps.AudioEncoders.map(function (e) { return { value: e.Name, label: e.DisplayName }; });
                    selectField(row, 'Codec', audioOpts, entry.Codec, function (v) { entry.Codec = v; rebuild(); });

                    if (entry.Codec !== 'flac') {
                        var br = el('div', 'mopt-field');
                        br.appendChild(el('label', null, 'Bitrate (kb/s)'));
                        var bri = document.createElement('input');
                        bri.type = 'number'; bri.min = '32'; bri.step = '32';
                        bri.value = entry.BitrateBps ? entry.BitrateBps / 1000 : '';
                        bri.addEventListener('change', function () {
                            entry.BitrateBps = bri.value === '' ? null : parseInt(bri.value, 10) * 1000;
                            rebuild();
                        });
                        br.appendChild(bri);
                        row.appendChild(br);
                    }
                }
                box.appendChild(row);

                if (track.IsLossless && entry.Action === 'Encode' && entry.Codec === 'flac') {
                    box.appendChild(el('div', 'mopt-track-meta',
                        'FLAC from ' + track.Codec.toUpperCase() + ' is bit-exact and will be hash-verified.'));
                }
                host.appendChild(box);
            });
        }

        // ---- passthrough and output
        host.appendChild(el('div', 'mopt-sec', 'Keep'));

        [['KeepAttachments', 'Embedded attachments (subtitle fonts, cover art)'],
         ['KeepChapters', 'Chapter markers']].forEach(function (pair) {
            var wrap = el('label', 'mopt-check');
            var input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = !!req[pair[0]];
            input.addEventListener('change', function () { req[pair[0]] = input.checked; rebuild(); });
            wrap.appendChild(input);
            wrap.appendChild(el('span', null, pair[1]));
            host.appendChild(wrap);
        });

        if (req.Video === 'Copy') {
            var filler = el('label', 'mopt-check');
            var fi = document.createElement('input');
            fi.type = 'checkbox';
            fi.checked = !!req.StripFillerData;
            fi.addEventListener('change', function () { req.StripFillerData = fi.checked; rebuild(); });
            filler.appendChild(fi);
            filler.appendChild(el('span', null,
                'Strip filler data — bit-exact, but only saves space on broadcast or capture sources that pad to a constant bitrate'));
            host.appendChild(filler);
        }

        host.appendChild(el('div', 'mopt-sec', 'Output'));
        var outRow = el('div', 'mopt-row');
        selectField(outRow, 'Container', [
            { value: 'mkv', label: 'MKV (Matroska)' },
            { value: 'mp4', label: 'MP4' }
        ], req.Container, function (v) { req.Container = v; rebuild(); });

        selectField(outRow, 'Where it goes', [
            { value: 'Sidecar', label: 'New file, original untouched' },
            { value: 'AlternateVersion', label: 'Add as alternate version' },
            { value: 'Replace', label: 'Replace original (revertable)' }
        ], req.OutputPolicy, function (v) { req.OutputPolicy = v; rebuild(); });
        host.appendChild(outRow);

        if (req.OutputPolicy === 'Replace') {
            host.appendChild(warningBox('warning',
                'The original is moved to a quarantine folder and can be restored from the queue page ' +
                'until its retention period expires. Nothing is deleted until then, and nothing is touched ' +
                'at all unless the new file passes verification.'));
        }
    }

    // ---- progress view ------------------------------------------------------

    function showProgress(pane, foot, job, close) {
        pane.innerHTML = '';
        foot.innerHTML = '';

        pane.appendChild(el('div', 'mopt-sec', 'Conversion queued'));
        var status = el('div', 'mopt-estimate-main', 'Waiting for a worker…');
        pane.appendChild(status);
        var sub = el('div', 'mopt-estimate-sub', '');
        pane.appendChild(sub);
        var bar = el('div', 'mopt-bar');
        var fill = el('i');
        fill.style.width = '0%';
        bar.appendChild(fill);
        pane.appendChild(bar);

        pane.appendChild(warningBox('info',
            'This runs on the server. You can close this dialog — progress stays visible under ' +
            'Dashboard → Media Optimizer, and the original file is not touched until the result passes verification.'));

        var cancelBtn = el('button', 'mopt-btn mopt-btn-danger', 'Cancel conversion');
        cancelBtn.addEventListener('click', function () {
            cancelBtn.disabled = true;
            request('DELETE', 'MediaOptimizer/Jobs/' + job.Id).catch(function () {});
        });
        foot.appendChild(cancelBtn);

        var closeBtn = el('button', 'mopt-btn', 'Close');
        closeBtn.addEventListener('click', function () { stop(); close(); });
        foot.appendChild(closeBtn);

        var timer = setInterval(poll, 1500);
        function stop() { clearInterval(timer); }
        poll();

        function poll() {
            request('GET', 'MediaOptimizer/Jobs/' + job.Id).then(function (j) {
                var pct = Math.round(j.ProgressPercent || 0);
                fill.style.width = pct + '%';

                var labels = {
                    Queued: 'Waiting for a worker…',
                    Preflight: 'Running safety checks…',
                    Encoding: 'Encoding — ' + pct + '%',
                    Verifying: 'Verifying the result…',
                    Applying: 'Moving the file into place…',
                    Completed: 'Done',
                    Failed: 'Failed',
                    Cancelled: 'Cancelled',
                    Interrupted: 'Interrupted'
                };
                status.textContent = labels[j.Status] || j.Status;

                var bits = [];
                if (j.Speed) { bits.push(j.Speed.toFixed(2) + '× realtime'); }
                if (j.EtaSeconds && j.Status === 'Encoding') { bits.push(duration(j.EtaSeconds) + ' remaining'); }
                if (j.Status === 'Completed') {
                    bits.push(bytes(j.SourceSizeBytes) + ' → ' + bytes(j.OutputSizeBytes));
                    if (j.LosslessVerified === true) { bits.push('bit-exactness verified'); }
                }
                if (j.Error) { bits.push(j.Error); }
                sub.textContent = bits.join(' · ');

                if (j.Status === 'Completed' || j.Status === 'Failed' || j.Status === 'Cancelled' || j.Status === 'Interrupted') {
                    stop();
                    cancelBtn.classList.add('mopt-hidden');
                    closeBtn.textContent = 'Close';
                }
            }).catch(function () { /* transient; the next tick retries */ });
        }
    }

    // ---------------------------------------------------------------- bootstrap

    function start() {
        if (!apiClient()) {
            setTimeout(start, 500);
            return;
        }
        var style = document.createElement('style');
        style.textContent = CSS;
        document.head.appendChild(style);

        observer.observe(document.body, { childList: true, subtree: true });
        scan();
        console.log('[MediaOptimizer] client ready');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
