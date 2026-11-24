// MailTrap web client. Plain JS, no build step.
(function () {
    'use strict';

    var PAGE = 100;
    var state = {
        folder: 'all',
        q: '',
        items: [],
        total: 0,
        stats: { total: 0, unread: 0, totalBytes: 0 },
        selectedId: null,
        detail: null,
        tab: 'html',
        rawCache: {},
        settings: null,
        live: false,
    };

    var $ = function (id) { return document.getElementById(id); };
    var listEl = $('msg-list'), readerEl = $('reader'), mailEl = $('mail');

    // ---------- helpers ----------
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function api(path, opts) {
        return fetch('api/' + path, Object.assign({ cache: 'no-store' }, opts)).then(function (r) {
            if (!r.ok) throw new Error(r.status + ' ' + r.statusText);
            return r.status === 204 ? null : r.headers.get('content-type') && r.headers.get('content-type').indexOf('json') >= 0 ? r.json() : r.text();
        });
    }
    function displayName(addr) {
        if (!addr) return '(unknown sender)';
        var first = addr.split(',')[0].trim();
        var m = first.match(/^"?([^"<]+?)"?\s*<([^>]+)>/);
        return m ? m[1].trim() : first;
    }
    function initials(addr) {
        var n = displayName(addr).replace(/@.*/, '');
        var parts = n.split(/[\s._-]+/).filter(Boolean);
        return ((parts[0] || '?')[0] + (parts[1] ? parts[1][0] : '')).toUpperCase();
    }
    function fmtSize(b) {
        if (b < 1024) return b + ' B';
        if (b < 1024 * 1024) return (b / 1024).toFixed(b < 10240 ? 1 : 0) + ' KB';
        return (b / 1024 / 1024).toFixed(1) + ' MB';
    }
    function fmtShort(iso) {
        var d = new Date(iso), now = new Date();
        if (d.toDateString() === now.toDateString()) return d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
        var y = new Date(now); y.setDate(now.getDate() - 1);
        if (d.toDateString() === y.toDateString()) return 'Yesterday';
        if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
        return d.toLocaleDateString();
    }
    function fmtLong(iso) {
        return new Date(iso).toLocaleString([], { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' });
    }
    function ago(iso) {
        if (!iso) return 'never';
        var s = Math.round((Date.now() - new Date(iso)) / 1000);
        if (s < 60) return 'just now';
        if (s < 3600) return Math.round(s / 60) + ' min ago';
        if (s < 86400) return Math.round(s / 3600) + ' h ago';
        return Math.round(s / 86400) + ' d ago';
    }

    // ---------- list ----------
    function query(skip) {
        var p = new URLSearchParams({ skip: skip, take: PAGE });
        if (state.q) p.set('q', state.q);
        if (state.folder === 'unread') p.set('unread', 'true');
        if (state.folder === 'attachments') p.set('attachments', 'true');
        return 'messages?' + p.toString();
    }

    function loadList(append) {
        return api(query(append ? state.items.length : 0)).then(function (res) {
            state.items = append ? state.items.concat(res.items) : res.items;
            state.total = res.total;
            state.stats = res.stats;
            renderList();
            renderCounts();
        }).catch(function (e) { toast('Could not load messages: ' + e.message, 'error'); });
    }

    function renderCounts() {
        $('count-all').textContent = state.stats.total;
        var u = $('count-unread');
        u.textContent = state.stats.unread;
        u.classList.toggle('hot', state.stats.unread > 0);
        document.title = (state.stats.unread ? '(' + state.stats.unread + ') ' : '') + 'MailTrap';
        $('list-meta').textContent = state.total === state.stats.total || state.q || state.folder !== 'all'
            ? state.total + (state.total === 1 ? ' message' : ' messages')
            : '';
        if (state.folder === 'all' && !state.q) $('list-meta').textContent = state.stats.total + ' · ' + fmtSize(state.stats.totalBytes);
    }

    function msgRow(m, isNew) {
        return '<button type="button" class="msg' + (m.isRead ? ' read' : '') + (m.id === state.selectedId ? ' selected' : '') + (isNew ? ' new' : '') +
            '" data-id="' + m.id + '">' +
            '<span class="avatar">' + esc(initials(m.from)) + '</span>' +
            '<span style="min-width:0">' +
            '<span class="msg-top"><span class="msg-from">' + esc(displayName(m.from)) + '</span>' +
            (m.attachmentCount ? '<span class="msg-flags" title="' + m.attachmentCount + ' attachment(s)"><i class="i i-paperclip"></i>' + m.attachmentCount + '</span>' : '') +
            '<span class="msg-time" title="' + esc(fmtLong(m.receivedUtc)) + '">' + esc(fmtShort(m.receivedUtc)) + '</span></span>' +
            '<span class="msg-subject" style="display:block">' + (m.subject ? esc(m.subject) : '<em>(no subject)</em>') + '</span>' +
            '<span class="msg-preview">' + esc('To: ' + displayName(m.to) + (m.preview ? ' — ' + m.preview : '')) + '</span>' +
            '</span></button>';
    }

    function renderList(newIds) {
        newIds = newIds || {};
        if (!state.items.length) {
            var filtered = state.q || state.folder !== 'all';
            listEl.innerHTML = '<div class="empty"><div class="empty-icon"><i class="i ' + (filtered ? 'i-search' : 'i-inbox') + '"></i></div>' +
                '<h3>' + (filtered ? 'No matching messages' : 'Inbox is empty') + '</h3>' +
                '<p>' + (filtered ? 'Try a different search or folder.' : 'Point your app’s SMTP settings at this server and send something.') + '</p>' +
                (filtered ? '' : '<p><button type="button" class="btn btn-sm" data-action="connect" style="margin-top:.6rem"><i class="i i-info"></i> How to connect</button></p>') +
                '</div>';
            return;
        }
        var html = state.items.map(function (m) { return msgRow(m, newIds[m.id]); }).join('');
        if (state.items.length < state.total)
            html += '<div class="list-footer"><button type="button" class="btn btn-sm" data-action="more">Load more (' + (state.total - state.items.length) + ' remaining)</button></div>';
        listEl.innerHTML = html;
    }

    // ---------- reader ----------
    function emptyReader() {
        readerEl.innerHTML = '<div class="empty"><div class="empty-icon"><i class="i i-mail"></i></div>' +
            '<h3>No message selected</h3><p>Pick a message from the list to read it, view its source and download attachments.</p></div>';
    }

    function select(id, opts) {
        opts = opts || {};
        state.selectedId = id;
        state.tab = 'html';
        document.querySelectorAll('.msg.selected').forEach(function (el) { el.classList.remove('selected'); });
        var row = listEl.querySelector('.msg[data-id="' + id + '"]');
        if (row) { row.classList.add('selected'); if (!opts.noScroll) row.scrollIntoView({ block: 'nearest' }); }
        mailEl.classList.add('reading');
        if (location.hash !== '#' + id) history.replaceState(null, '', '#' + id);

        readerEl.innerHTML = '<div class="empty"><p>Loading…</p></div>';
        api('messages/' + id).then(function (d) {
            if (state.selectedId !== id) return;
            state.detail = d;
            if (!d.hasHtml) state.tab = 'text';
            renderReader();
            if (!d.isRead) markRead(id, true);
        }).catch(function () {
            readerEl.innerHTML = '<div class="empty"><div class="empty-icon"><i class="i i-alert"></i></div><h3>Message not found</h3><p>It may have been deleted or purged.</p></div>';
        });
    }

    function markRead(id, read) {
        api('messages/' + id + '/read?read=' + read, { method: 'POST' }).then(function () {
            var m = state.items.find(function (x) { return x.id === id; });
            if (m && m.isRead !== read) {
                m.isRead = read;
                state.stats.unread += read ? -1 : 1;
                var row = listEl.querySelector('.msg[data-id="' + id + '"]');
                if (row) row.classList.toggle('read', read);
                renderCounts();
            }
            if (state.detail && state.detail.id === id) state.detail.isRead = read;
        });
    }

    var typeColors = { image: '#10b981', pdf: '#ef4444', text: '#3b82f6', zip: '#f59e0b', calendar: '#8b5cf6', message: '#6366f1', other: '#64748b' };
    function attKind(a) {
        var t = a.contentType;
        if (t.indexOf('image/') === 0) return 'image';
        if (t === 'application/pdf') return 'pdf';
        if (t === 'message/rfc822') return 'message';
        if (t === 'text/calendar') return 'calendar';
        if (/zip|compressed|x-tar|gzip|7z|rar/.test(t)) return 'zip';
        if (t.indexOf('text/') === 0 || /json|xml|csv/.test(t)) return 'text';
        return 'other';
    }

    function renderReader() {
        var d = state.detail;
        if (!d) return emptyReader();
        var base = 'api/messages/' + d.id;

        var rows = [
            ['From', d.from], ['To', d.to], ['Cc', d.cc], ['Bcc', d.bcc], ['Reply-To', d.replyTo],
        ].filter(function (r) { return r[1]; }).map(function (r) { return '<dt>' + r[0] + '</dt><dd>' + esc(r[1]) + '</dd>'; }).join('');
        var envelope = '<dt>Envelope from</dt><dd class="mono">' + esc(d.envelopeFrom || '—') + '</dd>' +
            '<dt>Envelope to</dt><dd class="mono">' + esc(d.envelopeTo || '—') + '</dd>' +
            '<dt>Received</dt><dd>' + esc(fmtLong(d.receivedUtc)) + (d.clientIp ? ' from <span class="mono">' + esc(d.clientIp) + '</span>' : '') + '</dd>' +
            (d.date ? '<dt>Date header</dt><dd>' + esc(fmtLong(d.date)) + '</dd>' : '') +
            (d.messageId ? '<dt>Message-ID</dt><dd class="mono">' + esc(d.messageId) + '</dd>' : '') +
            '<dt>Size</dt><dd>' + fmtSize(d.size) + '</dd>';

        var atts = '';
        if (d.attachments.length) {
            atts = '<div class="attachments"><div class="attachments-title"><i class="i i-paperclip"></i> ' + d.attachments.length + ' attachment' + (d.attachments.length === 1 ? '' : 's') +
                ' <a class="btn btn-sm btn-ghost" style="margin-left:auto" href="#" data-action="download-all"><i class="i i-download"></i> Download all</a></div>' +
                d.attachments.map(function (a) {
                    var kind = attKind(a), url = base + '/attachments/' + a.index;
                    var icon = kind === 'image'
                        ? '<img src="' + url + '" alt="" loading="lazy" />'
                        : '<i class="i ' + (kind === 'message' ? 'i-mail' : kind === 'calendar' ? 'i-clock' : kind === 'zip' ? 'i-database' : kind === 'text' ? 'i-code' : 'i-file') + '"></i>';
                    return '<div class="att">' +
                        '<span class="att-icon" style="--tc:' + typeColors[kind] + '">' + icon + '</span>' +
                        '<a class="att-info" href="' + url + '" target="_blank" rel="noopener" title="Open ' + esc(a.fileName) + '">' +
                        '<span class="att-name">' + esc(a.fileName) + '</span>' +
                        '<span class="att-meta">' + esc(a.contentType) + ' · ' + fmtSize(a.size) + (a.inline ? ' · inline' : '') + '</span></a>' +
                        '<a class="icon-btn" href="' + url + '?download=true" title="Download" data-download><i class="i i-download"></i></a>' +
                        '</div>';
                }).join('') + '</div>';
        }

        var tabs = [
            ['html', 'HTML', d.hasHtml], ['text', 'Plain text', !!d.text], ['source', 'HTML source', d.hasHtml],
            ['headers', 'Headers (' + d.headers.length + ')', true], ['raw', 'Raw', true],
        ].map(function (t) {
            return '<button type="button" class="tab' + (state.tab === t[0] ? ' active' : '') + '" data-tab="' + t[0] + '"' + (t[2] ? '' : ' disabled') + '>' + t[1] + '</button>';
        }).join('');

        readerEl.innerHTML =
            '<div class="reader-scroll">' +
            '<div class="reader-head">' +
            '<div class="reader-title">' +
            '<button type="button" class="icon-btn icon-btn-sm mobile-only" data-action="back" title="Back to list"><i class="i i-arrow-left"></i></button>' +
            '<h1>' + (d.subject ? esc(d.subject) : '<em>(no subject)</em>') + '</h1>' +
            '<div class="reader-actions">' +
            '<button type="button" class="icon-btn icon-btn-sm" data-action="toggle-read" title="Mark as unread"><i class="i i-dot-circle"></i></button>' +
            '<a class="icon-btn icon-btn-sm" href="' + base + '/raw?download=true" title="Download .eml"><i class="i i-download"></i></a>' +
            '<a class="icon-btn icon-btn-sm" href="' + base + '/raw" target="_blank" rel="noopener" title="Open raw source in new tab"><i class="i i-external"></i></a>' +
            '<button type="button" class="icon-btn icon-btn-sm danger" data-action="delete" title="Delete (Del)"><i class="i i-trash"></i></button>' +
            '</div></div>' +
            '<div class="reader-from"><span class="avatar avatar-lg">' + esc(initials(d.from)) + '</span>' +
            '<div class="who"><div class="name">' + esc(displayName(d.from)) + '</div><div class="form-text" style="margin:0">' + esc(d.from) + '</div></div>' +
            '<div class="when" title="' + esc(fmtLong(d.receivedUtc)) + '">' + esc(fmtShort(d.receivedUtc)) + '</div></div>' +
            '<dl class="kv">' + rows + '</dl>' +
            '<details class="kv-toggle"><summary class="btn btn-sm btn-ghost" style="padding-left:0"><i class="i i-info"></i> Delivery details</summary><dl class="kv">' + envelope + '</dl></details>' +
            '</div>' +
            atts +
            '<div class="tabs" role="tablist">' + tabs + '</div>' +
            '<div class="tab-body" id="tab-body"></div>' +
            '</div>';
        renderTab();
    }

    function renderTab() {
        var d = state.detail, body = $('tab-body');
        if (!d || !body) return;
        var base = 'api/messages/' + d.id;
        switch (state.tab) {
            case 'html':
                body.innerHTML = '<iframe class="html-frame" title="Message body" sandbox="allow-popups allow-popups-to-escape-sandbox" referrerpolicy="no-referrer" src="' + base + '/html"></iframe>';
                break;
            case 'text':
                body.innerHTML = d.text ? '<pre class="text-view">' + linkify(esc(d.text)) + '</pre>' : '<div class="empty"><p>This message has no plain-text part.</p></div>';
                break;
            case 'source':
                body.innerHTML = '<pre class="code-view">' + esc(d.html || '') + '</pre>';
                break;
            case 'headers':
                body.innerHTML = '<table class="headers-table"><tbody>' + d.headers.map(function (h) {
                    return '<tr><td>' + esc(h.name) + '</td><td>' + esc(h.value) + '</td></tr>';
                }).join('') + '</tbody></table>';
                break;
            case 'raw':
                if (state.rawCache[d.id] != null) { body.innerHTML = '<pre class="code-view">' + esc(state.rawCache[d.id]) + '</pre>'; break; }
                body.innerHTML = '<div class="empty"><p>Loading…</p></div>';
                api('messages/' + d.id + '/raw').then(function (t) {
                    state.rawCache[d.id] = t.length > 2000000 ? t.slice(0, 2000000) + '\n\n… truncated; download the .eml for the full source.' : t;
                    if (state.tab === 'raw' && state.detail && state.detail.id === d.id) renderTab();
                });
                break;
        }
    }

    function linkify(escaped) {
        return escaped.replace(/\bhttps?:\/\/[^\s<]+[^\s<.,;:!?)\]'"]/g, function (u) {
            return '<a href="' + u + '" target="_blank" rel="noopener noreferrer">' + u + '</a>';
        });
    }

    function deleteSelected() {
        var id = state.selectedId;
        if (!id) return;
        var idx = state.items.findIndex(function (m) { return m.id === id; });
        api('messages/' + id, { method: 'DELETE' }).then(function () {
            removeLocal(id);
            var next = state.items[idx] || state.items[idx - 1];
            if (next) select(next.id); else clearSelection();
            toast('Message deleted');
        }).catch(function (e) { toast('Delete failed: ' + e.message, 'error'); });
    }

    function removeLocal(id) {
        var idx = state.items.findIndex(function (m) { return m.id === id; });
        if (idx < 0) return;
        var m = state.items.splice(idx, 1)[0];
        state.total--; state.stats.total--;
        if (!m.isRead) state.stats.unread--;
        state.stats.totalBytes -= m.size;
        renderList(); renderCounts();
    }

    function clearSelection() {
        state.selectedId = null; state.detail = null;
        mailEl.classList.remove('reading');
        history.replaceState(null, '', location.pathname + location.search);
        emptyReader();
    }

    // ---------- toasts ----------
    function toast(text, kind) {
        var el = document.createElement('div');
        el.className = 'sm-toast' + (kind === 'error' ? ' error' : '');
        el.innerHTML = '<i class="i ' + (kind === 'error' ? 'i-alert' : 'i-check') + '"></i><div class="toast-body"><div class="toast-line">' + esc(text) + '</div></div>';
        showToast(el, 3500);
    }

    function mailToast(m) {
        var el = document.createElement('div');
        el.className = 'sm-toast clickable';
        el.innerHTML = '<span class="avatar">' + esc(initials(m.from)) + '</span>' +
            '<div class="toast-body"><div class="toast-title">New message' + (m.attachmentCount ? ' · ' + m.attachmentCount + ' attachment' + (m.attachmentCount > 1 ? 's' : '') : '') + '</div>' +
            '<div class="toast-line"><strong>' + esc(displayName(m.from)) + '</strong></div>' +
            '<div class="toast-line">' + (m.subject ? esc(m.subject) : '<em>(no subject)</em>') + '</div>' +
            '<div class="toast-line sub">To: ' + esc(m.to) + '</div></div>' +
            '<button type="button" class="toast-close" title="Dismiss">✕</button>';
        el.addEventListener('click', function (e) {
            dismiss(el);
            if (e.target.closest('.toast-close')) return;
            if (state.folder !== 'all' || state.q) { setFolder('all', true); $('search').value = ''; state.q = ''; loadList().then(function () { select(m.id); }); }
            else select(m.id);
        });
        showToast(el, 7000);
    }

    function showToast(el, ms) {
        var stack = $('toasts');
        stack.appendChild(el);
        while (stack.children.length > 5) stack.removeChild(stack.firstChild);
        var timer = setTimeout(function () { dismiss(el); }, ms);
        el.addEventListener('mouseenter', function () { clearTimeout(timer); });
        el.addEventListener('mouseleave', function () { timer = setTimeout(function () { dismiss(el); }, 2500); });
    }
    function dismiss(el) {
        el.classList.add('leaving');
        setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 250);
    }

    // ---------- live updates (SSE) ----------
    var listReloadTimer;
    function connectEvents() {
        var es = new EventSource('api/events');
        var wasDown = false;
        es.onopen = function () {
            setLive(true);
            if (wasDown) { wasDown = false; loadList(); loadSettings(); }
        };
        es.onerror = function () { setLive(false); wasDown = true; };
        es.onmessage = function (e) {
            var ev; try { ev = JSON.parse(e.data); } catch (err) { return; }
            if (ev.type === 'message') onNewMessage(ev.payload);
            else if (ev.type === 'deleted') { removeLocal(ev.payload.id); if (state.selectedId === ev.payload.id) clearSelection(); }
            else if (ev.type === 'purged' || ev.type === 'changed') {
                if (ev.type === 'purged' && ev.payload && ev.payload.removed) toast('Auto-purge removed ' + ev.payload.removed + ' message' + (ev.payload.removed === 1 ? '' : 's'));
                clearTimeout(listReloadTimer);
                listReloadTimer = setTimeout(function () {
                    loadList().then(function () {
                        if (state.selectedId && !state.items.some(function (m) { return m.id === state.selectedId; })) clearSelection();
                    });
                }, 150);
            }
        };
    }

    function onNewMessage(m) {
        mailToast(m);
        state.stats.total++; state.stats.unread++; state.stats.totalBytes += m.size;
        var matchesFolder = state.folder === 'all' || state.folder === 'unread' || (state.folder === 'attachments' && m.attachmentCount > 0);
        if (matchesFolder && !state.q) {
            state.items.unshift(m); state.total++;
            var keep = listEl.scrollTop;
            var n = {}; n[m.id] = true;
            renderList(n);
            listEl.scrollTop = keep ? keep + (listEl.querySelector('.msg') || { offsetHeight: 0 }).offsetHeight : 0;
        } else if (state.q) {
            clearTimeout(listReloadTimer);
            listReloadTimer = setTimeout(loadList, 300);
        }
        renderCounts();
    }

    function setLive(on) {
        state.live = on;
        var chip = $('live-chip');
        chip.className = 'topbar-chip live ' + (on ? 'nominal' : 'down');
        chip.innerHTML = '<span class="dot ' + (on ? 'dot-live' : 'dot-danger') + '"></span> ' + (on ? 'Live' : 'Reconnecting…');
    }

    // ---------- settings + connect ----------
    function loadSettings() {
        return api('settings').then(function (s) {
            state.settings = s;
            var ok = s.smtp.status.indexOf('listening') === 0;
            $('smtp-dot').className = 'dot ' + (ok ? 'dot-live' : 'dot-danger');
            $('smtp-status').textContent = ok ? 'SMTP listening' : 'SMTP ' + s.smtp.status;
            $('smtp-addr').textContent = location.hostname + ':' + s.smtp.port;
            return s;
        });
    }

    function openModal(title, icon, body, footer) {
        var root = $('modal-root');
        root.innerHTML = '<div class="sm-modal-backdrop" data-close><div class="sm-modal" role="dialog" aria-modal="true" aria-label="' + esc(title) + '">' +
            '<div class="sm-modal-header"><h3><i class="i ' + icon + '"></i> ' + esc(title) + '</h3><button type="button" class="icon-btn icon-btn-sm" data-close title="Close"><i class="i i-x"></i></button></div>' +
            '<div class="sm-modal-body">' + body + '</div>' +
            (footer ? '<div class="sm-modal-footer">' + footer + '</div>' : '') + '</div></div>';
        var first = root.querySelector('input, button:not([data-close])');
        if (first) first.focus();
        return root.querySelector('.sm-modal');
    }
    function closeModal() { $('modal-root').innerHTML = ''; }

    function openSettings() {
        loadSettings().then(function (s) {
            var p = s.purge;
            var modal = openModal('Settings & auto-purge', 'i-settings',
                '<div class="form-section">' +
                '<div class="form-section-title">Auto-purge</div>' +
                '<label class="switch"><input type="checkbox" id="p-enabled"' + (p.enabled ? ' checked' : '') + ' /> Automatically delete old messages</label>' +
                '<div class="form-grid">' +
                '<div><label class="form-label" for="p-age">Max age (hours)</label><input class="form-control" id="p-age" type="number" min="0" value="' + p.maxAgeHours + '" /><div class="form-text">0 = keep regardless of age</div></div>' +
                '<div><label class="form-label" for="p-count">Max messages</label><input class="form-control" id="p-count" type="number" min="0" value="' + p.maxMessages + '" /><div class="form-text">Oldest removed first. 0 = no limit</div></div>' +
                '<div><label class="form-label" for="p-interval">Check every (min)</label><input class="form-control" id="p-interval" type="number" min="1" max="1440" value="' + p.intervalMinutes + '" /><div class="form-text">How often rules run</div></div>' +
                '</div>' +
                '<div class="form-text" style="margin-top:.8rem">Last run: ' + esc(ago(s.lastPurgeUtc)) + (s.lastPurgeUtc ? ' · removed ' + s.lastPurgeRemoved : '') + '</div>' +
                '</div>' +
                '<div class="form-section">' +
                '<div class="form-section-title">Storage</div>' +
                '<div class="stat-row"><div><strong>' + s.stats.total + '</strong><span>Messages</span></div><div><strong>' + s.stats.unread + '</strong><span>Unread</span></div><div><strong>' + fmtSize(s.stats.totalBytes) + '</strong><span>Total size</span></div></div>' +
                '<div class="form-text" style="margin-top:.6rem">Data folder: <span class="mono">' + esc(s.dataPath) + '</span></div>' +
                '</div>' +
                '<div class="form-section">' +
                '<div class="form-section-title">SMTP listener</div>' +
                '<div class="stat-row"><div><strong class="mono">' + s.smtp.port + '</strong><span>Port</span></div><div><strong>' + s.smtp.maxMessageSizeMb + ' MB</strong><span>Max message size</span></div><div><strong>' + s.connectedClients + '</strong><span>Open browser tabs</span></div></div>' +
                '<div class="form-text" style="margin-top:.6rem">Status: ' + esc(s.smtp.status) + '. Port and size are set in <span class="mono">appsettings.json</span> (MailTrap section).</div>' +
                '</div>',
                '<button type="button" class="btn btn-danger" data-action="purge-now" style="margin-right:auto"><i class="i i-zap"></i> Run purge now</button>' +
                '<button type="button" class="btn" data-close>Cancel</button>' +
                '<button type="button" class="btn btn-primary" data-action="save-settings"><i class="i i-check"></i> Save</button>');
            modal.addEventListener('keydown', function (e) { if (e.key === 'Enter' && e.target.tagName === 'INPUT') save(); });
        });
    }

    function readPurgeForm() {
        return {
            enabled: $('p-enabled').checked,
            maxAgeHours: parseInt($('p-age').value, 10) || 0,
            maxMessages: parseInt($('p-count').value, 10) || 0,
            intervalMinutes: parseInt($('p-interval').value, 10) || 15,
        };
    }

    function save() {
        api('settings/purge', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readPurgeForm()) })
            .then(function () { closeModal(); toast('Auto-purge settings saved'); })
            .catch(function (e) { toast('Save failed: ' + e.message, 'error'); });
    }

    function purgeNow() {
        api('settings/purge', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readPurgeForm()) })
            .then(function () { return api('purge', { method: 'POST' }); })
            .then(function (r) { closeModal(); toast(r.removed ? 'Purged ' + r.removed + ' message' + (r.removed === 1 ? '' : 's') : 'Nothing to purge'); loadList(); })
            .catch(function (e) { toast('Purge failed: ' + e.message, 'error'); });
    }

    function openConnect() {
        loadSettings().then(function (s) {
            var host = location.hostname, port = s.smtp.port;
            openModal('How to connect', 'i-info',
                '<p style="margin-top:0">Point any app’s SMTP settings at this server. Every message is accepted and shown here. <strong>Nothing is ever delivered to real recipients.</strong> Any username and password are accepted, or you can use none. TLS isn’t needed.</p>' +
                '<div class="stat-row"><div><strong class="mono">' + esc(host) + '</strong><span>Host</span></div><div><strong class="mono">' + port + '</strong><span>Port</span></div><div><strong>None</strong><span>TLS / auth</span></div></div>' +
                '<div class="form-section-title" style="margin-top:1.2rem">.NET (appsettings / SmtpClient)</div>' +
                '<pre class="cs-preview">var smtp = new SmtpClient("' + esc(host) + '", ' + port + ') { EnableSsl = false };\nsmtp.Send("app@example.com", "user@example.com", "Hello", "Test body");</pre>' +
                '<div class="form-section-title" style="margin-top:1.2rem">PowerShell</div>' +
                '<pre class="cs-preview">Send-MailMessage -SmtpServer ' + esc(host) + ' -Port ' + port + ' -From app@example.com -To user@example.com -Subject "Hello" -Body "Test"</pre>' +
                '<div class="form-section-title" style="margin-top:1.2rem">Node (nodemailer)</div>' +
                '<pre class="cs-preview">nodemailer.createTransport({ host: "' + esc(host) + '", port: ' + port + ', secure: false, ignoreTLS: true })</pre>' +
                '<div class="form-section-title" style="margin-top:1.2rem">Python</div>' +
                '<pre class="cs-preview">smtplib.SMTP("' + esc(host) + '", ' + port + ').sendmail("app@example.com", ["user@example.com"], msg.as_string())</pre>',
                '<button type="button" class="btn btn-primary" data-close>Got it</button>');
        });
    }

    // ---------- folders / search ----------
    var folderTitles = { all: 'Inbox', unread: 'Unread', attachments: 'With attachments' };
    function setFolder(f, silent) {
        state.folder = f;
        document.querySelectorAll('[data-folder]').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-folder') === f); });
        $('folder-title').textContent = folderTitles[f];
        $('nav-toggle').checked = false;
        if (!silent) loadList();
    }

    var searchTimer;
    $('search').addEventListener('input', function (e) {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(function () { state.q = e.target.value.trim(); loadList(); }, 220);
    });

    // ---------- events ----------
    document.addEventListener('click', function (e) {
        var t = e.target;
        if (t.closest('[data-theme-toggle]')) {
            var next = document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-bs-theme', next);
            try { localStorage.setItem('mt-theme', next); } catch (err) { }
            return;
        }
        var row = t.closest('.msg[data-id]');
        if (row) { select(row.getAttribute('data-id'), { noScroll: true }); return; }
        var tab = t.closest('[data-tab]');
        if (tab && !tab.disabled) {
            state.tab = tab.getAttribute('data-tab');
            document.querySelectorAll('[data-tab]').forEach(function (b) { b.classList.toggle('active', b === tab); });
            renderTab();
            return;
        }
        var folder = t.closest('[data-folder]');
        if (folder) { setFolder(folder.getAttribute('data-folder')); return; }
        if (t.closest('[data-close]') && (t.closest('button[data-close]') || t.classList.contains('sm-modal-backdrop'))) { closeModal(); return; }

        var actionEl = t.closest('[data-action]');
        if (!actionEl) return;
        switch (actionEl.getAttribute('data-action')) {
            case 'refresh': loadList(); loadSettings(); break;
            case 'more': loadList(true); break;
            case 'back': clearSelection(); break;
            case 'delete': deleteSelected(); break;
            case 'toggle-read':
                if (state.detail) { markRead(state.detail.id, false); toast('Marked as unread'); }
                break;
            case 'read-all':
                api('messages/read-all', { method: 'POST' }).then(function () { toast('All messages marked as read'); });
                break;
            case 'delete-all':
                if (!state.stats.total) { toast('Mailbox is already empty'); break; }
                if (confirm('Delete all ' + state.stats.total + ' messages? This cannot be undone.'))
                    api('messages', { method: 'DELETE' }).then(function (r) { clearSelection(); toast('Deleted ' + r.removed + ' messages'); });
                break;
            case 'download-all':
                e.preventDefault();
                document.querySelectorAll('.att [data-download]').forEach(function (a, i) {
                    setTimeout(function () { var x = document.createElement('a'); x.href = a.href; x.download = ''; document.body.appendChild(x); x.click(); x.remove(); }, i * 300);
                });
                break;
            case 'settings': $('nav-toggle').checked = false; openSettings(); break;
            case 'connect': $('nav-toggle').checked = false; openConnect(); break;
            case 'save-settings': save(); break;
            case 'purge-now': purgeNow(); break;
        }
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && $('modal-root').innerHTML) { closeModal(); return; }
        if (/INPUT|TEXTAREA|SELECT/.test(e.target.tagName) || $('modal-root').innerHTML || e.ctrlKey || e.metaKey || e.altKey) return;
        var idx = state.items.findIndex(function (m) { return m.id === state.selectedId; });
        if (e.key === 'j' || e.key === 'ArrowDown') { var n = state.items[idx + 1]; if (n) { select(n.id); e.preventDefault(); } }
        else if (e.key === 'k' || e.key === 'ArrowUp') { var p = state.items[idx - 1]; if (p) { select(p.id); e.preventDefault(); } }
        else if (e.key === 'Delete' || e.key === '#') deleteSelected();
        else if (e.key === '/') { $('search').focus(); e.preventDefault(); }
    });

    // Keep relative times fresh.
    setInterval(function () { if (state.items.length) renderList(); }, 60000);

    // ---------- boot ----------
    emptyReader();
    setLive(false);
    loadSettings();
    loadList().then(function () {
        var id = location.hash.slice(1);
        if (/^[0-9a-f]{32}$/.test(id)) select(id);
    });
    connectEvents();
})();
