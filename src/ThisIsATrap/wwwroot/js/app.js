// ThisIsATrap web client. Plain JS, no build step.
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
        view: 'mail',
        inbox: '',      // '' = all inboxes, otherwise an inbox slug
        inboxes: [],
    };
    try { state.inbox = localStorage.getItem('mt-inbox') || ''; } catch (err) { }

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
        if (b < 1024 * 1024 * 1024) return (b / 1024 / 1024).toFixed(1) + ' MB';
        return (b / 1024 / 1024 / 1024).toFixed(1) + ' GB';
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
        if (state.inbox) p.set('inbox', state.inbox);
        return 'messages?' + p.toString();
    }
    function inboxScope() { return state.inbox ? '?inbox=' + encodeURIComponent(state.inbox) : ''; }

    // ---------- inboxes ----------
    function inboxBySlug(slug) { return state.inboxes.find(function (i) { return i.slug === slug; }); }
    function inboxName(slug) { var i = inboxBySlug(slug); return i ? i.name : slug; }
    function multiInbox() { return state.inboxes.length > 1; }

    var inboxReloadTimer;
    function loadInboxes() {
        return api('inboxes').then(function (list) {
            state.inboxes = list;
            if (state.inbox && !inboxBySlug(state.inbox)) { setInbox(''); return; }
            renderInboxNav();
            renderCounts();
        }).catch(function () { });
    }
    function reloadInboxesSoon() { clearTimeout(inboxReloadTimer); inboxReloadTimer = setTimeout(loadInboxes, 250); }

    function renderInboxNav() {
        var totalUnread = state.inboxes.reduce(function (s, i) { return s + i.unread; }, 0);
        var row = function (slug, name, unread, icon, title) {
            return '<button type="button" class="nav-item-link' + (state.inbox === slug ? ' active' : '') + '" data-inbox="' + esc(slug) + '" title="' + esc(title) + '">' +
                '<i class="i ' + icon + '"></i> <span class="nav-label">' + esc(name) + '</span>' +
                '<span class="nav-count' + (unread ? ' hot' : '') + '">' + unread + '</span></button>';
        };
        $('inbox-nav').innerHTML = row('', 'All inboxes', totalUnread, 'i-layers', 'Messages from every inbox') +
            state.inboxes.map(function (i) {
                return row(i.slug, i.name, i.unread, 'i-inbox', i.isDefault ? 'Mail sent without a username' : 'SMTP username: ' + i.slug);
            }).join('');
    }

    function setInbox(slug) {
        state.inbox = slug;
        try { localStorage.setItem('mt-inbox', slug); } catch (err) { }
        if (state.view !== 'mail') showView('mail');
        clearSelection();
        renderInboxNav();
        updateTitle();
        $('nav-toggle').checked = false;
        return loadList();
    }

    function updateTitle() {
        var base = state.inbox ? inboxName(state.inbox) : 'All inboxes';
        $('folder-title').textContent = base + (state.folder !== 'all' ? ' · ' + folderTitles[state.folder] : '');
        $('inbox-settings-btn').hidden = !state.inbox;
    }

    function credentialsHtml(slug) {
        var s = state.settings, host = location.hostname, port = s ? s.smtp.port : 25;
        return '<div class="stat-row cred-row">' +
            '<div><strong class="mono">' + esc(host) + ':' + port + '</strong><span>SMTP server</span></div>' +
            '<div><strong class="mono">' + esc(slug) + '</strong><span>Username</span></div>' +
            '<div><strong>Anything</strong><span>Password</span></div></div>' +
            '<div class="form-text" style="margin-top:.6rem">No SMTP login? Add the header <span class="mono">' + esc((s && s.smtp.routingHeader) || 'X-Trap-Inbox') + ': ' + esc(slug) + '</span> to the message instead.</div>';
    }

    function openNewInbox() {
        var modal = openModal('New inbox', 'i-inbox',
            '<p style="margin-top:0">Give each app or environment its own inbox. Mail is routed by <strong>SMTP username</strong>, so the inbox key below is the username to configure.</p>' +
            '<label class="form-label" for="ni-name">Name</label><input class="form-control" id="ni-name" maxlength="80" placeholder="e.g. Billing API (staging)" autocomplete="off" />' +
            '<div class="form-text" style="margin-top:.4rem">Inbox key / username: <span class="mono" id="ni-slug">—</span></div>',
            '<button type="button" class="btn" data-close>Cancel</button><button type="button" class="btn btn-primary" data-action="create-inbox"><i class="i i-check"></i> Create</button>');
        var input = $('ni-name');
        input.addEventListener('input', function () { $('ni-slug').textContent = slugify(input.value) || '—'; });
        modal.addEventListener('keydown', function (e) { if (e.key === 'Enter') createInbox(); });
        input.focus();
    }

    // Mirrors InboxSlug.Normalize on the server (preview only; the server decides).
    function slugify(v) {
        return (v || '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase()
            .replace(/[^a-z0-9._-]+/g, '-').replace(/-+/g, '-').replace(/^[-._]+|[-._]+$/g, '').slice(0, 64);
    }

    function createInbox() {
        var name = $('ni-name').value.trim();
        if (!slugify(name)) { toast('Enter a name with at least one letter or digit', 'error'); return; }
        fetch('api/inboxes', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: name }) })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .then(function (res) {
                if (!res.ok && res.status !== 409) throw new Error(res.body.error || res.status);
                var inbox = res.ok ? res.body : res.body.inbox;
                return loadInboxes().then(function () {
                    setInbox(inbox.slug);
                    openModal(res.ok ? 'Inbox created' : 'Inbox already exists', 'i-check',
                        '<p style="margin-top:0"><strong>' + esc(inbox.name) + '</strong> is ready. Point your app at it with these settings:</p>' + credentialsHtml(inbox.slug),
                        '<button type="button" class="btn btn-primary" data-close>Done</button>');
                });
            })
            .catch(function (e) { toast('Could not create inbox: ' + e.message, 'error'); });
    }

    function openInboxSettings() {
        var inbox = inboxBySlug(state.inbox);
        if (!inbox) return;
        var p = inbox.purge || (state.settings && state.settings.purge) || { enabled: true, maxAgeHours: 72, maxMessages: 1000, intervalMinutes: 15 };
        var custom = !!inbox.purge;
        var modal = openModal(inbox.name, 'i-inbox',
            '<div class="form-section"><div class="form-section-title">Name</div>' +
            '<input class="form-control" id="ib-name" maxlength="80" value="' + esc(inbox.name) + '" />' +
            '<div class="form-text" style="margin-top:.4rem">Renaming keeps the key <span class="mono">' + esc(inbox.slug) + '</span>, so apps keep delivering here.</div></div>' +
            '<div class="form-section"><div class="form-section-title">Connect</div>' + credentialsHtml(inbox.slug) + '</div>' +
            '<div class="form-section"><div class="form-section-title">Auto-purge</div>' +
            '<label class="switch"><input type="checkbox" id="ib-custom"' + (custom ? ' checked' : '') + ' /> Use custom rules for this inbox</label>' +
            '<div id="ib-rules"' + (custom ? '' : ' class="disabled-block"') + '>' +
            '<label class="switch"><input type="checkbox" id="ib-enabled"' + (p.enabled ? ' checked' : '') + ' /> Automatically delete old messages</label>' +
            '<div class="form-grid form-grid-2">' +
            '<div><label class="form-label" for="ib-age">Max age (hours)</label><input class="form-control" id="ib-age" type="number" min="0" value="' + p.maxAgeHours + '" /><div class="form-text">0 = no age limit</div></div>' +
            '<div><label class="form-label" for="ib-count">Max messages</label><input class="form-control" id="ib-count" type="number" min="0" value="' + p.maxMessages + '" /><div class="form-text">0 = no limit</div></div>' +
            '</div></div>' +
            '<div class="form-text" style="margin-top:.6rem">Without custom rules this inbox follows the global rules in <a href="#" data-action="settings">Settings</a>.</div></div>' +
            '<div class="form-section"><div class="form-section-title">Contents</div>' +
            '<div class="stat-row"><div><strong>' + inbox.total + '</strong><span>Messages</span></div><div><strong>' + inbox.unread + '</strong><span>Unread</span></div><div><strong>' + fmtSize(inbox.totalBytes) + '</strong><span>Size</span></div></div></div>',
            (inbox.isDefault ? '' : '<button type="button" class="btn btn-danger" data-action="delete-inbox" style="margin-right:auto"><i class="i i-trash"></i> Delete inbox</button>') +
            '<button type="button" class="btn" data-close>Cancel</button>' +
            '<button type="button" class="btn btn-primary" data-action="save-inbox"><i class="i i-check"></i> Save</button>');
        $('ib-custom').addEventListener('change', function (e) { $('ib-rules').classList.toggle('disabled-block', !e.target.checked); });
        modal.addEventListener('keydown', function (e) { if (e.key === 'Enter' && e.target.tagName === 'INPUT' && e.target.type !== 'checkbox') saveInbox(); });
    }

    function saveInbox() {
        var slug = state.inbox;
        var body = {
            name: $('ib-name').value.trim(),
            purge: $('ib-custom').checked ? {
                enabled: $('ib-enabled').checked,
                maxAgeHours: parseInt($('ib-age').value, 10) || 0,
                maxMessages: parseInt($('ib-count').value, 10) || 0,
                intervalMinutes: (state.settings && state.settings.purge.intervalMinutes) || 15,
            } : null,
        };
        if (!body.name) { toast('Name is required', 'error'); return; }
        api('inboxes/' + encodeURIComponent(slug), { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
            .then(function () { closeModal(); toast('Inbox saved'); return loadInboxes(); })
            .then(updateTitle)
            .catch(function (e) { toast('Save failed: ' + e.message, 'error'); });
    }

    function deleteInbox() {
        var inbox = inboxBySlug(state.inbox);
        if (!inbox || !confirm('Delete the inbox "' + inbox.name + '" and its ' + inbox.total + ' message(s)? This cannot be undone.')) return;
        api('inboxes/' + encodeURIComponent(inbox.slug), { method: 'DELETE' })
            .then(function (r) { closeModal(); toast('Deleted ' + inbox.name + (r.removed ? ' and ' + r.removed + ' message(s)' : '')); return loadInboxes(); })
            .then(function () { setInbox(''); })
            .catch(function (e) { toast('Delete failed: ' + e.message, 'error'); });
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
        var allUnread = state.inboxes.length ? state.inboxes.reduce(function (s, i) { return s + i.unread; }, 0) : state.stats.unread;
        document.title = (allUnread ? '(' + allUnread + ') ' : '') + (state.env && state.env.label ? '[' + state.env.label + '] ' : '') + 'ThisIsATrap';
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
            (!state.inbox && multiInbox() ? '<span class="pill pill-accent msg-inbox" title="Inbox">' + esc(inboxName(m.inbox)) + '</span>' : '') +
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
                '<h3>' + (filtered ? 'No matching messages' : state.inbox ? esc(inboxName(state.inbox)) + ' is empty' : 'Inbox is empty') + '</h3>' +
                '<p>' + (filtered ? 'Try a different search or folder.'
                    : state.inbox && state.inbox !== 'default' ? 'Send mail with SMTP username <span class="mono">' + esc(state.inbox) + '</span> (any password) to land here.'
                    : 'Point your app’s SMTP settings at this server and send something.') + '</p>' +
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
                var ib = inboxBySlug(m.inbox);
                if (ib) { ib.unread += read ? -1 : 1; renderInboxNav(); }
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
            ['Inbox', multiInbox() ? d.inboxName : ''],
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
            ['check', 'HTML check', d.hasHtml], ['spam', 'Spam score', true], ['links', 'Links', true],
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
                var size = previewSizes[state.preview] ? state.preview : 'desktop';
                body.innerHTML = '<div class="preview-bar"><div class="seg" role="group" aria-label="Preview width">' +
                    Object.keys(previewSizes).map(function (k) {
                        var p = previewSizes[k];
                        return '<button type="button" data-preview="' + k + '" class="' + (k === size ? 'active' : '') + '" aria-pressed="' + (k === size) + '" title="' + p.title + '">' +
                            '<i class="i ' + p.icon + '"></i> ' + p.label + '</button>';
                    }).join('') +
                    '</div><span class="meta" id="preview-width">' + previewSizes[size].hint + '</span></div>' +
                    '<div class="preview-stage' + (size === 'desktop' ? '' : ' device ' + size) + '" id="preview-stage">' +
                    '<iframe class="html-frame" title="Message body" sandbox="allow-popups allow-popups-to-escape-sandbox" referrerpolicy="no-referrer" src="' + base + '/html"></iframe></div>';
                if (size === 'desktop') setPreviewHint();
                break;
            case 'text':
                body.innerHTML = d.text ? '<pre class="text-view">' + linkify(esc(d.text)) + '</pre>' : '<div class="empty"><p>This message has no plain-text part.</p></div>';
                break;
            case 'source':
                body.innerHTML = '<pre class="code-view">' + esc(d.html || '') + '</pre>';
                break;
            case 'check':
                loadAnalysis(d.id, 'html-check', renderHtmlCheck);
                break;
            case 'spam':
                loadAnalysis(d.id, 'spam', renderSpam);
                break;
            case 'links':
                if (analysisCache['links/check:' + d.id]) renderLinks(analysisCache['links/check:' + d.id], body);
                else loadAnalysis(d.id, 'links', renderLinks);
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

    // ---------- analysis tabs (HTML check, spam, links) ----------
    var analysisCache = {};   // "<kind>:<id>" → result
    function loadAnalysis(id, kind, render, opts) {
        var key = kind + ':' + id, body = $('tab-body');
        if (analysisCache[key] && !(opts && opts.refresh)) { render(analysisCache[key], body); return; }
        body.innerHTML = '<div class="empty"><p>Analyzing…</p></div>';
        api('messages/' + id + '/' + kind, opts && opts.method ? { method: opts.method } : undefined).then(function (r) {
            analysisCache[key] = r;
            if (state.detail && state.detail.id === id && $('tab-body')) render(r, $('tab-body'));
        }).catch(function (e) {
            if (state.detail && state.detail.id === id) $('tab-body').innerHTML = '<div class="empty"><div class="empty-icon"><i class="i i-alert"></i></div><h3>Analysis failed</h3><p>' + esc(e.message) + '</p></div>';
        });
    }

    function scoreClass(score) { return score >= 90 ? 'ok' : score >= 70 ? 'warn' : 'bad'; }
    function scoreRing(score, label) {
        return '<div class="score-ring ' + scoreClass(score) + '" style="--p:' + score + '"><div><strong>' + Math.round(score) + '%</strong><span>' + label + '</span></div></div>';
    }
    var supportLabel = { y: 'Supported', a: 'Partial', n: 'Not supported', u: 'Unknown' };

    function renderHtmlCheck(r, body) {
        if (!r.hasHtml) { body.innerHTML = '<div class="empty"><p>This message has no HTML part.</p></div>'; return; }
        var clients = r.clients.map(function (c) {
            return '<div class="client-score ' + scoreClass(c.score) + '"><div class="client-name">' + esc(c.name) + '</div>' +
                '<div class="client-pct">' + Math.round(c.score) + '%</div>' +
                '<div class="meter"><span class="' + (c.score < 70 ? 'bad' : c.score < 90 ? 'warn' : '') + '" style="width:' + c.score + '%"></span></div>' +
                '<div class="client-meta">' + (c.unsupported ? c.unsupported + ' unsupported' : '') + (c.unsupported && c.partial ? ' · ' : '') + (c.partial ? c.partial + ' partial' : '') + (!c.unsupported && !c.partial ? 'All supported' : '') + '</div></div>';
        }).join('');
        var rows = r.features.map(function (f) {
            var chips = f.clients.map(function (c) {
                var detail = c.platforms.map(function (p) { return p.name + ': ' + supportLabel[p.support] + (p.notes.length ? ' (' + p.notes.join(' ') + ')' : ''); }).join('\n');
                return '<span class="support-chip s-' + c.support + '" title="' + esc(detail) + '">' + esc(c.name) + '</span>';
            }).join('');
            var notes = [];
            f.clients.forEach(function (c) { c.platforms.forEach(function (p) { p.notes.forEach(function (n) { if (notes.indexOf(n) < 0) notes.push(n); }); }); });
            return '<details class="feature-row"><summary>' +
                '<span class="feature-score ' + scoreClass(f.score) + '">' + Math.round(f.score) + '%</span>' +
                '<span class="feature-main"><strong>' + esc(f.title) + '</strong><span class="mono">' + esc(f.sample) + '</span></span>' +
                '<span class="pill pill-muted">' + esc(f.category.toUpperCase()) + '</span>' +
                '<span class="feature-count" title="Occurrences">×' + f.occurrences + '</span></summary>' +
                '<div class="feature-detail"><div class="support-chips">' + chips + '</div>' +
                (notes.length ? '<ul class="feature-notes">' + notes.map(function (n) { return '<li>' + esc(n) + '</li>'; }).join('') + '</ul>' : '') +
                '<a href="' + esc(f.url) + '" target="_blank" rel="noopener noreferrer">Full support table on caniemail.com <i class="i i-external"></i></a></div></details>';
        }).join('');
        body.innerHTML = '<div class="analysis">' +
            '<div class="analysis-head">' + scoreRing(r.score, 'support') +
            '<div><h3>HTML &amp; CSS support across email clients</h3>' +
            '<p>' + r.features.length + ' feature' + (r.features.length === 1 ? '' : 's') + ' detected. The score is the share of client/platform combinations that render them (partial support counts half).</p>' +
            '<p class="form-text">Data: <a href="https://www.caniemail.com" target="_blank" rel="noopener noreferrer">caniemail.com</a> (MIT), updated ' + esc((r.dataUpdated || '').slice(0, 10)) + '.</p></div></div>' +
            '<div class="client-grid">' + clients + '</div>' +
            '<div class="analysis-section-title">Features, least supported first <span class="legend"><span class="support-chip s-y">Supported</span><span class="support-chip s-a">Partial</span><span class="support-chip s-n">Not supported</span></span></div>' +
            (rows || '<div class="empty"><p>No HTML/CSS features with client-support data were found.</p></div>') +
            '</div>';
    }

    function ruleTable(rules, emptyText) {
        if (!rules.length) return '<div class="rule-empty"><i class="i i-check"></i> ' + emptyText + '</div>';
        return '<table class="rule-table"><thead><tr><th>Points</th><th>Rule</th><th>Why</th></tr></thead><tbody>' +
            rules.map(function (r) {
                return '<tr><td><span class="rule-pts ' + (r.score >= 1.5 ? 'bad' : r.score > 0 ? 'warn' : 'ok') + '">' + (r.score > 0 ? '+' : '') + r.score.toFixed(1) + '</span></td>' +
                    '<td class="mono">' + esc(r.id) + '</td>' +
                    '<td>' + esc(r.description) + (r.detail ? '<div class="rule-detail mono">' + esc(r.detail) + '</div>' : '') + '</td></tr>';
            }).join('') + '</tbody></table>';
    }

    function renderSpam(r, body) {
        // Ring shows "how clean": 0 points = 100%, at/over the threshold = 0%.
        var clean = Math.max(0, Math.min(100, 100 - (r.score / r.threshold) * 100));
        var sa = r.spamAssassin;
        body.innerHTML = '<div class="analysis">' +
            '<div class="analysis-head"><div class="score-ring ' + (r.score >= r.threshold ? 'bad' : r.score >= 2 ? 'warn' : 'ok') + '" style="--p:' + clean + '"><div><strong>' + r.score.toFixed(1) + '</strong><span>of ' + r.threshold.toFixed(1) + '</span></div></div>' +
            '<div><h3>' + esc(r.verdict) + '</h3>' +
            '<p>Spam score using ' + (r.source === 'spamassassin' ? '<strong>SpamAssassin</strong>' : 'ThisIsATrap’s built-in rules (modelled on SpamAssassin)') +
            '. Messages scoring ' + r.threshold.toFixed(1) + ' or more are usually filtered as spam.</p>' +
            (sa && !sa.available ? '<p class="form-text">' + esc(sa.error) + '. Showing built-in rules only.</p>' : '') +
            (!sa ? '<p class="form-text">Set <span class="mono">ThisIsATrap:SpamAssassin:Host</span> to add real SpamAssassin scoring.</p>' : '') +
            '</div></div>' +
            (sa && sa.available ? '<div class="analysis-section-title">SpamAssassin rules (' + sa.score.toFixed(1) + ' / ' + sa.threshold.toFixed(1) + ')</div>' + ruleTable(sa.rules, 'No SpamAssassin rules matched.') : '') +
            '<div class="analysis-section-title">Content checks</div>' +
            ruleTable(r.rules, 'No content problems found.') +
            '</div>';
    }

    var linkStatus = {
        ok: ['ok', 'OK'], redirect: ['ok', 'Redirects'], broken: ['bad', 'Broken'], error: ['bad', 'Error'], timeout: ['bad', 'Timeout'],
        blocked: ['warn', 'Not reachable'], missing: ['bad', 'Missing'], skipped: ['muted', 'Not fetched'], unchecked: ['muted', 'Not checked'],
    };
    var issueText = {
        'insecure-http': 'http://', 'local-address': 'Local/private address', 'url-shortener': 'URL shortener', 'text-mismatch': 'Text shows another domain',
        'downgrade-to-http': 'Redirects to http://', 'not-an-image': 'Not an image', 'relative-url': 'Relative URL',
    };
    var kindIcon = { link: 'i-link', image: 'i-image', background: 'i-image', text: 'i-file' };

    function renderLinks(r, body) {
        var rows = r.links.map(function (l) {
            var st = linkStatus[l.status] || ['muted', l.status];
            var href = /^https?:/i.test(l.url) ? l.url : null;
            return '<tr><td><span class="link-status s-' + st[0] + '">' + st[1] + (l.httpStatus ? ' ' + l.httpStatus : '') + '</span></td>' +
                '<td><i class="i ' + (kindIcon[l.kind] || 'i-link') + '" title="' + esc(l.kind) + '"></i></td>' +
                '<td class="link-cell">' + (href ? '<a href="' + esc(href) + '" target="_blank" rel="noopener noreferrer">' + esc(l.url) + '</a>' : '<span class="mono">' + esc(l.url) + '</span>') +
                (l.text ? '<div class="link-text">' + esc(l.text) + '</div>' : '') +
                (l.redirects.length ? '<div class="link-redirects">→ ' + l.redirects.map(esc).join('<br>→ ') + '</div>' : '') +
                (l.error ? '<div class="link-error">' + esc(l.error) + '</div>' : '') +
                (l.issues.length ? '<div class="link-issues">' + l.issues.map(function (i) { return '<span class="support-chip s-a">' + esc(issueText[i] || i) + '</span>'; }).join('') + '</div>' : '') +
                '</td><td class="mono link-ms">' + (l.elapsedMs != null ? l.elapsedMs + ' ms' : '') + '</td></tr>';
        }).join('');
        body.innerHTML = '<div class="analysis">' +
            '<div class="links-head"><div class="stat-row links-stats">' +
            '<div><strong>' + r.total + '</strong><span>Links &amp; images</span></div>' +
            '<div><strong class="' + (r.broken ? 'text-bad' : '') + '">' + (r.checked ? r.broken : '—') + '</strong><span>' + (r.checked ? 'Problems' : 'Not checked yet') + '</span></div>' +
            '<div><strong class="' + (r.warnings ? 'text-warn' : '') + '">' + r.warnings + '</strong><span>With warnings</span></div></div>' +
            '<button type="button" class="btn btn-primary" data-action="check-links"><i class="i i-refresh"></i> ' + (r.checked ? 'Check again' : 'Check links') + '</button></div>' +
            '<p class="form-text" style="margin:0">' + (r.checked
                ? 'Each URL was requested (HEAD, then GET if needed) and redirects followed. Local and private-network addresses are never fetched.'
                : 'Lists every link and image. <strong>Check links</strong> requests each URL to find broken links, redirects and slow responses.') + '</p>' +
            (r.total ? '<table class="rule-table link-table"><thead><tr><th>Status</th><th></th><th>URL</th><th>Time</th></tr></thead><tbody>' + rows + '</tbody></table>'
                : '<div class="rule-empty"><i class="i i-check"></i> No links or images in this message.</div>') +
            '</div>';
    }

    function checkLinks() {
        var d = state.detail;
        if (!d) return;
        var btn = document.querySelector('[data-action="check-links"]');
        if (btn) { btn.disabled = true; btn.innerHTML = '<i class="i i-refresh spin"></i> Checking…'; }
        api('messages/' + d.id + '/links/check', { method: 'POST' }).then(function (r) {
            analysisCache['links/check:' + d.id] = r;
            if (state.detail && state.detail.id === d.id && state.tab === 'links') renderLinks(r, $('tab-body'));
            toast(r.broken ? r.broken + ' link problem' + (r.broken === 1 ? '' : 's') + ' found' : 'All links OK', r.broken ? 'error' : undefined);
        }).catch(function (e) {
            toast('Link check failed: ' + e.message, 'error');
            if (btn) { btn.disabled = false; btn.textContent = 'Check links'; }
        });
    }

    // ---------- responsive preview ----------
    var previewSizes = {
        desktop: { label: 'Desktop', icon: 'i-monitor', title: 'Full width of the reading pane', hint: '' },
        tablet: { label: 'Tablet', icon: 'i-tablet', title: 'Tablet width (768 px)', hint: '768 px' },
        mobile: { label: 'Mobile', icon: 'i-phone', title: 'Phone width (375 px)', hint: '375 px' },
    };
    try { state.preview = localStorage.getItem('mt-preview') || 'desktop'; } catch (err) { state.preview = 'desktop'; }

    function setPreview(size) {
        state.preview = size;
        try { localStorage.setItem('mt-preview', size); } catch (err) { }
        var stage = $('preview-stage');
        if (!stage) return;
        stage.className = 'preview-stage' + (size === 'desktop' ? '' : ' device ' + size);
        document.querySelectorAll('[data-preview]').forEach(function (b) {
            var on = b.getAttribute('data-preview') === size;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on);
        });
        $('preview-width').textContent = previewSizes[size].hint;
        if (size === 'desktop') setPreviewHint();
    }
    function setPreviewHint() {
        requestAnimationFrame(function () {
            var f = document.querySelector('#preview-stage .html-frame'), hint = $('preview-width');
            if (f && hint) hint.textContent = Math.round(f.getBoundingClientRect().width) + ' px';
        });
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
        reloadInboxesSoon();
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
            '<div class="toast-body"><div class="toast-title">New message' + (multiInbox() ? ' · ' + esc(inboxName(m.inbox)) : '') + (m.attachmentCount ? ' · ' + m.attachmentCount + ' attachment' + (m.attachmentCount > 1 ? 's' : '') : '') + '</div>' +
            '<div class="toast-line"><strong>' + esc(displayName(m.from)) + '</strong></div>' +
            '<div class="toast-line">' + (m.subject ? esc(m.subject) : '<em>(no subject)</em>') + '</div>' +
            '<div class="toast-line sub">To: ' + esc(m.to) + '</div></div>' +
            '<button type="button" class="toast-close" title="Dismiss">✕</button>';
        el.addEventListener('click', function (e) {
            dismiss(el);
            if (e.target.closest('.toast-close')) return;
            var outOfScope = state.inbox && state.inbox !== m.inbox;
            if (state.view !== 'mail' || outOfScope || state.folder !== 'all' || state.q) {
                setFolder('all', true); $('search').value = ''; state.q = '';
                (outOfScope ? setInbox(m.inbox) : loadList()).then(function () { select(m.id); });
            }
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
            else if (ev.type === 'inboxes') reloadInboxesSoon();
            else if (ev.type === 'rejected') rejectedToast(ev.payload);
            else if (ev.type === 'environment') applyEnvironment(ev.payload);
            else if (ev.type === 'deleted') { removeLocal(ev.payload.id); if (state.selectedId === ev.payload.id) clearSelection(); }
            else if (ev.type === 'purged' || ev.type === 'changed') {
                if (ev.type === 'purged' && ev.payload && ev.payload.removed) toast('Removed ' + ev.payload.removed + ' message' + (ev.payload.removed === 1 ? '' : 's'));
                reloadInboxesSoon();
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
        var ib = inboxBySlug(m.inbox);
        if (ib) { ib.total++; ib.unread++; ib.totalBytes += m.size; renderInboxNav(); }
        else reloadInboxesSoon();
        mailToast(m);
        if (state.inbox && state.inbox !== m.inbox) { renderCounts(); return; }
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
    // ---------- environment label ----------
    var envColors = { red: '#ef4444', orange: '#f97316', amber: '#f59e0b', green: '#10b981', teal: '#14b8a6', blue: '#3b82f6', indigo: '#6366f1', purple: '#a855f7', pink: '#ec4899', gray: '#64748b' };
    function envColor(c) { return envColors[c] || (/^#[0-9a-f]{3}([0-9a-f]{3})?$/i.test(c || '') ? c : envColors.indigo); }

    function applyEnvironment(env) {
        state.env = env;
        var badge = $('env-badge');
        if (env && env.label) {
            badge.textContent = env.label;
            badge.style.setProperty('--env', envColor(env.color));
            badge.hidden = false;
        } else {
            badge.hidden = true;
        }
        renderCounts();
    }

    function loadSettings() {
        return api('settings').then(function (s) {
            state.settings = s;
            applyEnvironment(s.environment);
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
            var p = s.purge, env = s.environment;
            var modal = openModal('Settings & auto-purge', 'i-settings',
                '<div class="form-section">' +
                '<div class="form-section-title">Environment label</div>' +
                '<div class="form-text" style="margin:-.2rem 0 .7rem">Shown in the header and the tab title so Dev / Stage / QA traps are easy to tell apart.' +
                (env.source === 'config' ? ' Currently set by configuration (<span class="mono">ThisIsATrap:EnvironmentLabel</span>); a value here overrides it.' : '') + '</div>' +
                '<div class="form-grid form-grid-env">' +
                '<div><label class="form-label" for="env-label">Label</label><input class="form-control" id="env-label" maxlength="24" placeholder="e.g. QA" value="' + esc(env.source === 'settings' ? env.label : '') + '" />' +
                '<div class="form-text">Empty = ' + (env.source === 'config' || (env.source === 'settings' && s.environment) ? 'use configuration' : 'no label') + '</div></div>' +
                '<div><label class="form-label" for="env-color">Colour</label><select class="form-control" id="env-color">' +
                '<option value="">Automatic</option>' + Object.keys(envColors).map(function (c) {
                    return '<option value="' + c + '"' + (env.source === 'settings' && env.color === c ? ' selected' : '') + '>' + c.charAt(0).toUpperCase() + c.slice(1) + '</option>';
                }).join('') + '</select></div>' +
                '<div><span class="form-label">Preview</span><div><span class="env-badge" id="env-preview"></span></div></div></div></div>' +
                '<div class="form-section">' +
                '<div class="form-section-title">Auto-purge (global rules)</div>' +
                '<div class="form-text" style="margin:-.2rem 0 .7rem">Apply to every inbox that has no custom rules. The message limit is per inbox.</div>' +
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
                '<div class="form-text" style="margin-top:.6rem">Status: ' + esc(s.smtp.status) + '. Port and size are set in <span class="mono">appsettings.json</span> (ThisIsATrap section).</div>' +
                '</div>',
                '<button type="button" class="btn btn-danger" data-action="purge-now" style="margin-right:auto"><i class="i i-zap"></i> Run purge now</button>' +
                '<button type="button" class="btn" data-close>Cancel</button>' +
                '<button type="button" class="btn btn-primary" data-action="save-settings"><i class="i i-check"></i> Save</button>');
            modal.addEventListener('keydown', function (e) { if (e.key === 'Enter' && e.target.tagName === 'INPUT') save(); });
            $('env-label').addEventListener('input', updateEnvPreview);
            $('env-color').addEventListener('change', updateEnvPreview);
            updateEnvPreview();
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

    function updateEnvPreview() {
        var el = $('env-preview');
        if (!el) return;
        var label = $('env-label').value.trim() || (state.env && state.env.source === 'config' ? state.env.label : '');
        var color = $('env-color').value || autoEnvColor(label);
        el.textContent = label || '(none)';
        el.style.setProperty('--env', envColor(color));
        el.classList.toggle('env-empty', !label);
    }
    // Mirrors EnvironmentLabels.NormalizeColor on the server.
    function autoEnvColor(label) {
        var l = (label || '').toLowerCase();
        return /prod|live/.test(l) ? 'red' : /stag|qa|uat|test/.test(l) ? 'amber' : /dev|local/.test(l) ? 'green' : 'indigo';
    }

    function save() {
        var envBody = { label: $('env-label').value.trim() || null, color: $('env-color').value || null };
        Promise.all([
            api('settings/purge', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readPurgeForm()) }),
            fetch('api/settings/environment', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(envBody) })
                .then(function (r) { return r.json().then(function (b) { if (!r.ok) throw new Error(b.error || r.status); return b; }); }),
        ])
            .then(function (results) { applyEnvironment(results[1]); closeModal(); toast('Settings saved'); })
            .catch(function (e) { toast('Save failed: ' + e.message, 'error'); });
    }

    function purgeNow() {
        api('settings/purge', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readPurgeForm()) })
            .then(function () { return api('purge', { method: 'POST' }); })
            .then(function (r) { closeModal(); toast(r.removed ? 'Purged ' + r.removed + ' message' + (r.removed === 1 ? '' : 's') : 'Nothing to purge'); loadList(); })
            .catch(function (e) { toast('Purge failed: ' + e.message, 'error'); });
    }

    // ---------- webhooks ----------
    function webhookRow(h) {
        var opts = '<option value="">All inboxes</option>' + state.inboxes.map(function (i) {
            return '<option value="' + esc(i.slug) + '"' + (h.inbox === i.slug ? ' selected' : '') + '>' + esc(i.name) + '</option>';
        }).join('');
        var last = h.lastDelivery;
        return '<div class="editor-row hook-row" data-id="' + esc(h.id || '') + '">' +
            '<label class="switch" title="Enabled"><input type="checkbox" class="h-enabled"' + (h.enabled !== false ? ' checked' : '') + ' /></label>' +
            '<input class="form-control h-url" placeholder="https://ci.internal/hooks/mail" value="' + esc(h.url || '') + '" />' +
            '<select class="form-control h-inbox">' + opts + '</select>' +
            '<input class="form-control h-secret" type="password" autocomplete="new-password" placeholder="' + (h.hasSecret ? '•••••• (unchanged)' : 'Signing secret (optional)') + '" />' +
            (h.id ? '<button type="button" class="btn btn-sm" data-action="test-webhook" title="Send a test notification now">Test</button>' : '<span></span>') +
            '<button type="button" class="icon-btn icon-btn-sm danger" data-action="remove-row" title="Remove"><i class="i i-trash"></i></button>' +
            (last ? '<div class="hook-status ' + (last.ok ? 'ok' : 'bad') + '">' + (last.ok ? 'Delivered' : 'Failed') + (last.httpStatus ? ' · HTTP ' + last.httpStatus : '') +
                ' · ' + esc(ago(last.attemptedUtc)) + (last.attempts > 1 ? ' · ' + last.attempts + ' attempts' : '') + (last.error ? ' · ' + esc(last.error) : '') + '</div>' : '') +
            '</div>';
    }

    function openWebhooks() {
        api('settings/webhooks').then(function (hooks) {
            openModal('Webhooks', 'i-webhook',
                '<p style="margin-top:0">POST a JSON notification to your own endpoints whenever mail arrives, for example to wake a CI job waiting for an email. Webhooks make outbound HTTP calls only; mail is never sent anywhere.</p>' +
                '<div id="hook-rows" class="editor-rows">' + (hooks.length ? hooks.map(webhookRow).join('') : '') + '</div>' +
                '<button type="button" class="btn btn-sm" data-action="add-webhook" style="margin-top:.6rem"><i class="i i-plus"></i> Add webhook</button>' +
                '<details class="kv-toggle" style="margin-top:1rem"><summary class="btn btn-sm btn-ghost" style="padding-left:0"><i class="i i-info"></i> Payload &amp; signature</summary>' +
                '<pre class="cs-preview">POST &lt;your url&gt;\nContent-Type: application/json\nX-Trap-Event: message.received\nX-Trap-Signature: sha256=&lt;hex HMAC-SHA256 of the body with your secret&gt;\n\n' +
                '{ "event": "message.received", "sentUtc": "…",\n  "inbox": { "slug": "billing-api", "name": "Billing API" },\n  "message": { "id", "receivedUtc", "from", "to", "subject", "preview", "size", "attachmentCount", "inbox" },\n  "urls": { "ui", "api", "raw" } }</pre>' +
                '<div class="form-text">Failed deliveries are retried twice (after 1 s and 5 s). Private-network receivers are allowed; cloud metadata addresses are not.</div></details>',
                '<button type="button" class="btn" data-close>Cancel</button><button type="button" class="btn btn-primary" data-action="save-webhooks"><i class="i i-check"></i> Save</button>');
            var modal = document.querySelector('.sm-modal');
            if (modal) modal.classList.add('sm-modal-wide');
            if (!hooks.length) addWebhookRow();
        }).catch(function (e) { toast('Could not load webhooks: ' + e.message, 'error'); });
    }
    function addWebhookRow() { $('hook-rows').insertAdjacentHTML('beforeend', webhookRow({})); }

    function readWebhooks() {
        return [].map.call(document.querySelectorAll('.hook-row'), function (row) {
            var secret = row.querySelector('.h-secret').value;
            return {
                id: row.getAttribute('data-id') || null,
                url: row.querySelector('.h-url').value.trim(),
                inbox: row.querySelector('.h-inbox').value || null,
                enabled: row.querySelector('.h-enabled').checked,
                secret: secret === '' ? null : secret,   // empty = keep the stored secret
            };
        }).filter(function (h) { return h.url; });
    }

    function saveWebhooks(then) {
        return fetch('api/settings/webhooks', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readWebhooks()) })
            .then(function (r) { return r.json().then(function (b) { if (!r.ok) throw new Error(b.error || r.status); return b; }); })
            .then(function (hooks) { if (then) then(hooks); else { closeModal(); toast('Webhooks saved'); } return hooks; })
            .catch(function (e) { toast('Save failed: ' + e.message, 'error'); });
    }

    function testWebhook(row) {
        var url = row.querySelector('.h-url').value.trim();
        // Save first so the test uses exactly what's on screen.
        saveWebhooks(function (hooks) {
            var hook = hooks.find(function (h) { return h.url === url; });
            if (!hook) return;
            toast('Sending test notification…');
            api('settings/webhooks/' + hook.id + '/test', { method: 'POST' }).then(function (s) {
                toast(s.ok ? 'Test delivered (HTTP ' + s.httpStatus + ')' : 'Test failed: ' + (s.error || 'HTTP ' + s.httpStatus), s.ok ? undefined : 'error');
                openWebhooks();
            });
        });
    }

    // ---------- SMTP error simulation ----------
    function ruleRow(r) {
        return '<div class="editor-row rule-row">' +
            '<label class="switch" title="Enabled"><input type="checkbox" class="r-enabled"' + (r.enabled !== false ? ' checked' : '') + ' /></label>' +
            '<select class="form-control r-match"><option value="rcpt"' + (r.match !== 'from' ? ' selected' : '') + '>RCPT TO</option><option value="from"' + (r.match === 'from' ? ' selected' : '') + '>MAIL FROM</option></select>' +
            '<input class="form-control r-pattern mono" placeholder="*@reject.test" value="' + esc(r.pattern || '') + '" />' +
            '<input class="form-control r-code mono" type="number" min="200" max="599" value="' + (r.code || 550) + '" title="SMTP reply code" />' +
            '<input class="form-control r-enhanced mono" placeholder="5.1.1" value="' + esc(r.enhancedCode || '') + '" title="Enhanced status code" />' +
            '<input class="form-control r-message" placeholder="Message" value="' + esc(r.message || '') + '" />' +
            '<input class="form-control r-delay mono" type="number" min="0" max="120000" step="500" value="' + (r.delayMs || 0) + '" title="Delay before replying (ms)" />' +
            '<button type="button" class="icon-btn icon-btn-sm danger" data-action="remove-row" title="Remove"><i class="i i-trash"></i></button></div>';
    }

    function openRules(rules) {
        (rules ? Promise.resolve(rules) : api('settings/smtp-rules')).then(function (list) {
            openModal('SMTP error simulation', 'i-alert',
                '<p style="margin-top:0">Make the server <strong>reject or delay</strong> specific addresses so you can test how your app handles bounces, full mailboxes, temporary failures and slow servers. The first matching rule wins; <span class="mono">*</span> and <span class="mono">?</span> are wildcards. A 2xx code accepts after the delay.</p>' +
                '<div class="editor-head rule-row"><span></span><span>Stage</span><span>Address pattern</span><span>Code</span><span>Enhanced</span><span>Message</span><span>Delay ms</span><span></span></div>' +
                '<div id="rule-rows" class="editor-rows">' + list.map(ruleRow).join('') + '</div>' +
                '<div style="display:flex;gap:.5rem;margin-top:.6rem"><button type="button" class="btn btn-sm" data-action="add-rule"><i class="i i-plus"></i> Add rule</button>' +
                '<button type="button" class="btn btn-sm btn-ghost" data-action="reset-rules">Restore defaults</button></div>' +
                '<div class="form-text" style="margin-top:.8rem">Built-in test addresses use the reserved <span class="mono">.test</span> domain, so they can never be real: ' +
                '<span class="mono">x@reject.test</span> (550), <span class="mono">x@full.test</span> (452), <span class="mono">x@tempfail.test</span> (451), <span class="mono">x@slow.test</span> (10 s delay).</div>',
                '<button type="button" class="btn" data-close>Cancel</button><button type="button" class="btn btn-primary" data-action="save-rules"><i class="i i-check"></i> Save</button>');
            var modal = document.querySelector('.sm-modal');
            if (modal) modal.classList.add('sm-modal-wide');
        }).catch(function (e) { toast('Could not load rules: ' + e.message, 'error'); });
    }

    function saveRules() {
        var rules = [].map.call(document.querySelectorAll('#rule-rows .rule-row'), function (row) {
            return {
                enabled: row.querySelector('.r-enabled').checked,
                match: row.querySelector('.r-match').value,
                pattern: row.querySelector('.r-pattern').value.trim(),
                code: parseInt(row.querySelector('.r-code').value, 10) || 550,
                enhancedCode: row.querySelector('.r-enhanced').value.trim(),
                message: row.querySelector('.r-message').value.trim(),
                delayMs: parseInt(row.querySelector('.r-delay').value, 10) || 0,
            };
        }).filter(function (r) { return r.pattern; });
        api('settings/smtp-rules', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(rules) })
            .then(function () { closeModal(); toast('Error simulation rules saved'); })
            .catch(function (e) { toast('Save failed: ' + e.message, 'error'); });
    }

    function rejectedToast(r) {
        var el = document.createElement('div');
        el.className = 'sm-toast warning';
        el.innerHTML = '<i class="i i-alert"></i><div class="toast-body"><div class="toast-title">Simulated ' + esc(String(r.code)) + ' · ' + (r.stage === 'from' ? 'MAIL FROM' : 'RCPT TO') + '</div>' +
            '<div class="toast-line"><strong>' + esc(r.address) + '</strong></div><div class="toast-line sub">' + esc(r.enhancedCode + ' ' + r.message) + '</div></div>';
        showToast(el, 6000);
    }

    // TLS details for the connect page: ports, certificate fingerprint, and download links.
    function tlsHtml(t, host) {
        if (!t || !t.enabled) return '<div class="form-text" style="margin-top:.5rem">TLS is off. Turn on STARTTLS or an SMTPS port with the <span class="mono">Tls</span> settings if your app insists on encryption.</div>';
        if (t.error) return '<div class="link-error" style="margin-top:.5rem">TLS is enabled but the certificate failed to load: ' + esc(t.error) + '</div>';
        var c = t.certificate;
        return '<div class="form-section-title" style="margin-top:1.2rem">TLS</div>' +
            '<div class="stat-row">' +
            '<div><strong>' + (t.startTls ? 'On' : 'Off') + '</strong><span>STARTTLS</span></div>' +
            '<div><strong class="mono">' + (t.smtpsPort || '—') + '</strong><span>SMTPS (implicit TLS) port</span></div>' +
            '<div><strong>' + (c && c.selfSigned ? 'Self-signed' : 'Configured') + '</strong><span>Certificate</span></div></div>' +
            (c ? '<div class="form-text" style="margin-top:.5rem">' + esc(c.subject) + ' · expires ' + esc(new Date(c.notAfter).toLocaleDateString()) +
                '<br>SHA-1 <span class="mono">' + esc(c.thumbprint) + '</span>' +
                (c.selfSigned ? '<br>Clients must trust this certificate (or skip verification in test code). Download it: ' : '<br>Download: ') +
                '<a href="api/tls/certificate" download>.cer</a> · <a href="api/tls/certificate?format=pem" download>.pem</a></div>' : '') +
            '<pre class="cs-preview">// .NET: STARTTLS on port ' + esc(String(state.settings.smtp.port)) + '\nvar smtp = new SmtpClient("' + esc(host) + '", ' + state.settings.smtp.port + ') { EnableSsl = true };</pre>';
    }

    function openConnect() {
        loadSettings().then(function (s) {
            var host = location.hostname, port = s.smtp.port;
            openModal('How to connect', 'i-info',
                '<p style="margin-top:0">Point any app’s SMTP settings at this server. Every message is accepted and shown here. <strong>Nothing is ever delivered to real recipients.</strong> Any username and password are accepted, or you can use none. TLS isn’t needed.</p>' +
                '<div class="stat-row"><div><strong class="mono">' + esc(host) + '</strong><span>Host</span></div><div><strong class="mono">' + port + '</strong><span>Port</span></div>' +
                '<div><strong>' + (s.tls.startTls ? 'STARTTLS (optional)' : 'None') + '</strong><span>TLS on port ' + port + '</span></div></div>' +
                tlsHtml(s.tls, host) +
                '<div class="form-section-title" style="margin-top:1.2rem">Separate inboxes per app</div>' +
                '<p style="margin:.3rem 0 0">The <strong>SMTP username</strong> picks the inbox: log in as <span class="mono">billing-api</span> (any password) and mail lands in a “billing-api” inbox' +
                (s.smtp.autoCreateInboxes ? ', created automatically the first time.' : ' (create it first with <a href="#" data-action="new-inbox">New inbox</a>).') +
                ' Clients that can’t log in can add a <span class="mono">' + esc(s.smtp.routingHeader) + ': billing-api</span> header instead. Anything else goes to <strong>Default</strong>.</p>' +
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

    // ---------- status & health view ----------
    var statusTimer = null;
    var healthClass = { Healthy: 'ok', Degraded: 'warn', Unhealthy: 'bad' };
    var checkNames = { smtp: 'SMTP listener', database: 'Database', storage: 'Storage' };
    function fmtVersion(v) {
        var m = /^([^+]+)\+([0-9a-f]{7})/.exec(v || '');
        return m ? esc(m[1]) + ' <span class="pill pill-muted" title="' + esc(v) + '">' + m[2] + '</span>' : esc(v);
    }

    function fmtDuration(sec) {
        var d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
        return d ? d + 'd ' + h + 'h' : h ? h + 'h ' + m + 'm' : m ? m + 'm ' + (sec % 60) + 's' : sec + 's';
    }
    function kvRows(rows) {
        return '<dl class="kv status-kv">' + rows.filter(Boolean).map(function (r) {
            return '<dt>' + r[0] + '</dt><dd' + (r[2] ? ' class="mono"' : '') + '>' + r[1] + '</dd>';
        }).join('') + '</dl>';
    }
    function card(icon, title, body, extra) {
        return '<div class="status-card"><div class="status-card-head"><i class="i ' + icon + '"></i> ' + title + (extra || '') + '</div>' + body + '</div>';
    }

    function setHealthBadge(status) {
        var el = $('nav-health');
        el.className = 'nav-health ' + (healthClass[status] || '');
        el.title = status || '';
    }

    function loadStatus() {
        return api('status').then(function (s) {
            setHealthBadge(s.health.status);
            if (state.view === 'status') renderStatus(s);
        }).catch(function (e) {
            setHealthBadge('Unhealthy');
            if (state.view === 'status')
                $('status-view').innerHTML = '<div class="status-wrap"><div class="status-banner bad"><i class="i i-alert"></i><div><strong>Status unavailable</strong><span>' + esc(e.message) + '</span></div></div></div>';
        });
    }

    function renderStatus(s) {
        var h = s.health, cls = healthClass[h.status] || 'bad';
        var checks = h.checks.map(function (c) {
            return '<div class="check-row"><span class="dot ' + (c.status === 'Healthy' ? 'dot-live' : c.status === 'Degraded' ? 'dot-warn' : 'dot-danger') + '"></span>' +
                '<div class="check-main"><strong>' + esc(checkNames[c.name] || c.name) + '</strong><span>' + esc(c.description || '') + (c.error ? ' · ' + esc(c.error) : '') + '</span></div>' +
                '<span class="pill ' + (c.status === 'Healthy' ? 'pill-muted' : 'pill-accent') + '">' + esc(c.status) + '</span>' +
                '<span class="check-ms">' + c.durationMs + ' ms</span></div>';
        }).join('');

        var st = s.storage, usedPct = st.diskTotalBytes ? Math.round((1 - st.diskFreeBytes / st.diskTotalBytes) * 100) : null;
        var disk = usedPct == null ? '' :
            '<div class="meter-label"><span>Disk</span><span>' + fmtSize(st.diskFreeBytes) + ' free of ' + fmtSize(st.diskTotalBytes) + '</span></div>' +
            '<div class="meter"><span class="' + (usedPct > 95 ? 'bad' : usedPct > 85 ? 'warn' : '') + '" style="width:' + usedPct + '%"></span></div>';

        var p = s.purge, smtp = s.smtp, app = s.app;
        $('status-view').innerHTML =
            '<div class="status-wrap">' +
            '<div class="status-title"><h2>Status &amp; health</h2>' +
            '<span class="meta">Updated ' + new Date().toLocaleTimeString() + ' · refreshes every 5 s</span>' +
            '<button type="button" class="btn btn-sm" data-action="status-refresh"><i class="i i-refresh"></i> Refresh</button></div>' +
            '<div class="status-banner ' + cls + '"><i class="i ' + (cls === 'ok' ? 'i-check' : 'i-alert') + '"></i><div><strong>' +
            (cls === 'ok' ? 'All systems operational' : cls === 'warn' ? 'Running with warnings' : 'Not ready') + '</strong>' +
            '<span>Overall ' + esc(h.status) + ' · ' + h.checks.length + ' checks in ' + h.totalDurationMs + ' ms</span></div>' +
            '<div class="status-links"><a class="pill pill-muted mono" href="healthz" target="_blank" rel="noopener">/healthz</a>' +
            '<a class="pill pill-muted mono" href="readyz" target="_blank" rel="noopener">/readyz</a>' +
            '<a class="pill pill-muted mono" href="api/status" target="_blank" rel="noopener">/api/status</a></div></div>' +
            '<div class="status-grid">' +
            card('i-activity', 'Health checks', '<div class="checks">' + checks + '</div>') +
            card('i-server', 'SMTP listener',
                '<div class="stat-row"><div><strong>' + smtp.messagesReceived + '</strong><span title="Since the app started">Received</span></div>' +
                '<div><strong>' + smtp.messagesRejected + '</strong><span>Rejected</span></div>' +
                '<div><strong>' + esc(ago(smtp.lastMessageUtc)) + '</strong><span>Last message</span></div></div>' +
                kvRows([
                    ['Status', '<span class="dot ' + (smtp.listening ? 'dot-live' : 'dot-danger') + '"></span> ' + esc(smtp.status)],
                    ['Address', esc(location.hostname + ':' + smtp.port), true],
                    smtp.port !== smtp.listenPort ? ['Listening on', String(smtp.listenPort), true] : null,
                    ['Greeting name', esc(smtp.serverName), true],
                    ['TLS', !smtp.tls.enabled ? 'Off' : smtp.tls.error ? '<span class="text-bad">' + esc(smtp.tls.error) + '</span>'
                        : [smtp.tls.startTls ? 'STARTTLS' : '', smtp.tls.smtpsPort ? 'SMTPS on ' + smtp.tls.smtpsPort : ''].filter(Boolean).join(' · ') +
                          (smtp.tls.certificate ? ' · cert expires ' + esc(new Date(smtp.tls.certificate.notAfter).toLocaleDateString()) : '')],
                    ['Max message size', smtp.maxMessageSizeMb + ' MB'],
                ])) +
            card('i-database', 'Storage',
                '<div class="stat-row"><div><strong>' + st.messages + '</strong><span>Messages</span></div>' +
                '<div><strong>' + st.unread + '</strong><span>Unread</span></div>' +
                '<div><strong>' + fmtSize(st.messageBytes) + '</strong><span>Mail size</span></div></div>' +
                disk +
                kvRows([['Data folder', esc(st.dataPath), true], ['Database', fmtSize(st.databaseBytes)]])) +
            card('i-zap', 'Auto-purge',
                kvRows([
                    ['Automatic', p.enabled ? 'On, every ' + p.intervalMinutes + ' min' : 'Off'],
                    ['Max age', p.maxAgeHours ? p.maxAgeHours + ' hours' : 'No limit'],
                    ['Max messages', p.maxMessages || 'No limit'],
                    ['Last run', esc(ago(p.lastRunUtc)) + (p.lastRunUtc ? ' · removed ' + p.lastRemoved : '')],
                    ['Next run', p.nextRunUtc ? esc(new Date(p.nextRunUtc).toLocaleTimeString()) : '—'],
                ]), '<button type="button" class="btn btn-sm btn-ghost status-card-action" data-action="settings">Edit</button>') +
            card('i-layers', 'Application',
                kvRows([
                    ['Version', fmtVersion(app.version)],
                    ['Uptime', fmtDuration(app.uptimeSeconds) + ' · since ' + esc(fmtLong(app.startedUtc))],
                    ['Hosting', esc(app.hosting) + ' · ' + esc(app.environment)],
                    ['Host', esc(app.host), true],
                    ['OS', esc(app.os)],
                    ['Runtime', esc(app.runtime)],
                    ['Process', 'PID ' + app.processId + ' · ' + fmtSize(app.memoryBytes)],
                    ['Browser tabs', String(s.events.connectedClients)],
                ])) +
            '</div></div>';
    }

    // Full-page views besides the mailbox. Each has a #hash so it can be bookmarked.
    var pageViews = ['status', 'logs'];

    function showView(view) {
        state.view = view;
        var isMail = view === 'mail';
        mailEl.hidden = !isMail;
        pageViews.forEach(function (v) { $(v + '-view').hidden = v !== view; });
        document.querySelectorAll('[data-view]').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-view') === view); });
        if (!isMail) document.querySelectorAll('[data-folder]').forEach(function (b) { b.classList.remove('active'); });
        $('nav-toggle').checked = false;
        clearInterval(statusTimer);
        stopLogTail();
        if (!isMail) {
            if (location.hash !== '#' + view) history.replaceState(null, '', '#' + view);
        } else if (pageViews.indexOf(location.hash.slice(1)) >= 0) {
            history.replaceState(null, '', location.pathname + location.search);
        }
        if (view === 'status') {
            $('status-view').innerHTML = '<div class="status-wrap"><div class="empty"><p>Loading…</p></div></div>';
            loadStatus();
            statusTimer = setInterval(loadStatus, 5000);
        } else if (view === 'logs') {
            openLogs();
        }
    }

    // ---------- log viewer ----------
    var logState = { lastSeq: 0, entries: [], timer: null, paused: false, follow: true, level: 'Information', q: '' };
    var levelClass = { Trace: 'muted', Debug: 'muted', Information: 'info', Warning: 'warn', Error: 'bad', Critical: 'bad' };
    var levelShort = { Trace: 'TRC', Debug: 'DBG', Information: 'INF', Warning: 'WRN', Error: 'ERR', Critical: 'CRT' };

    function stopLogTail() { clearInterval(logState.timer); logState.timer = null; }

    function openLogs() {
        try { logState.level = localStorage.getItem('mt-log-level') || 'Information'; } catch (err) { }
        $('logs-view').innerHTML = '<div class="status-wrap logs-wrap">' +
            '<div class="status-title"><h2>Logs</h2><span class="meta" id="log-meta"></span></div>' +
            '<div class="log-toolbar">' +
            '<div class="input-icon log-search"><i class="i i-search"></i><input id="log-q" class="form-control" type="search" placeholder="Filter text, category, IP…" autocomplete="off" /></div>' +
            '<select id="log-level" class="form-control log-level">' + ['Debug', 'Information', 'Warning', 'Error'].map(function (l) {
                return '<option value="' + l + '"' + (l === logState.level ? ' selected' : '') + '>' + (l === 'Information' ? 'Info' : l) + ' and above</option>';
            }).join('') + '</select>' +
            '<label class="switch log-switch"><input type="checkbox" id="log-follow"' + (logState.follow ? ' checked' : '') + ' /> Follow</label>' +
            '<button type="button" class="btn btn-sm" data-action="log-pause" id="log-pause">' + (logState.paused ? 'Resume' : 'Pause') + '</button>' +
            '<a class="btn btn-sm" id="log-download" href="api/logs/download" download><i class="i i-download"></i> Download</a>' +
            '<button type="button" class="btn btn-sm btn-danger" data-action="log-clear"><i class="i i-trash"></i> Clear</button></div>' +
            '<div class="log-box" id="log-box"><table class="log-table"><tbody id="log-rows"></tbody></table></div></div>';
        $('log-q').addEventListener('input', function (e) { clearTimeout(logState.qTimer); logState.qTimer = setTimeout(function () { logState.q = e.target.value.trim(); reloadLogs(); }, 250); });
        $('log-level').addEventListener('change', function (e) { logState.level = e.target.value; try { localStorage.setItem('mt-log-level', logState.level); } catch (err) { } reloadLogs(); });
        $('log-follow').addEventListener('change', function (e) { logState.follow = e.target.checked; if (logState.follow) scrollLogs(); });
        reloadLogs();
        logState.timer = setInterval(function () { if (!logState.paused) pollLogs(); }, 2000);
    }

    function logQuery(after) {
        var p = new URLSearchParams({ after: after, level: logState.level, take: 1000 });
        if (logState.q) p.set('q', logState.q);
        return 'logs?' + p.toString();
    }

    function reloadLogs() {
        logState.lastSeq = 0; logState.entries = [];
        $('log-rows').innerHTML = '';
        var dl = new URLSearchParams({ level: logState.level }); if (logState.q) dl.set('q', logState.q);
        $('log-download').href = 'api/logs/download?' + dl.toString();
        pollLogs();
    }

    function pollLogs() {
        if (state.view !== 'logs') return;
        api(logQuery(logState.lastSeq)).then(function (page) {
            if (state.view !== 'logs') return;
            logState.lastSeq = page.lastSeq;
            if (page.entries.length) {
                $('log-rows').insertAdjacentHTML('beforeend', page.entries.map(logRow).join(''));
                logState.entries = logState.entries.concat(page.entries);
                // Keep the DOM bounded.
                var rows = $('log-rows');
                while (rows.children.length > 3000) rows.removeChild(rows.firstChild);
                if (logState.follow) scrollLogs();
            }
            $('log-meta').textContent = page.buffered + ' of ' + page.capacity + ' lines kept in memory' +
                (page.dropped ? ' · ' + page.dropped + ' older dropped' : '') + (logState.paused ? ' · paused' : ' · live');
            if (!$('log-rows').children.length) $('log-rows').innerHTML = '<tr class="log-empty"><td>No log lines match.</td></tr>';
            else { var empty = document.querySelector('.log-empty'); if (empty) empty.remove(); }
        }).catch(function (e) { $('log-meta').textContent = 'Could not load logs: ' + e.message; });
    }

    function logRow(e) {
        var cls = levelClass[e.level] || 'muted';
        return '<tr class="lvl-' + cls + '"><td class="log-time" title="' + esc(e.utc) + '">' + esc(new Date(e.utc).toLocaleTimeString([], { hour12: false }) + '.' + String(new Date(e.utc).getMilliseconds()).padStart(3, '0')) + '</td>' +
            '<td><span class="log-level-pill ' + cls + '">' + (levelShort[e.level] || esc(e.level)) + '</span></td>' +
            '<td class="log-cat">' + esc(e.category) + '</td>' +
            '<td class="log-msg">' + esc(e.message) + (e.exception ? '<details><summary>Exception</summary><pre>' + esc(e.exception) + '</pre></details>' : '') + '</td></tr>';
    }

    function scrollLogs() { var box = $('log-box'); if (box) box.scrollTop = box.scrollHeight; }

    // ---------- SMTP client allowlist ----------
    function openAccess() {
        api('settings/smtp-access').then(function (r) {
            var s = r.settings;
            openModal('SMTP access', 'i-shield',
                '<p style="margin-top:0">Optionally restrict which machines may send mail to this trap. When on, clients that aren’t listed get <span class="mono">554 5.7.1</span> at <span class="mono">MAIL FROM</span> and the attempt shows in Logs. The web UI is not affected.</p>' +
                '<label class="switch"><input type="checkbox" id="ac-enabled"' + (s.enabled ? ' checked' : '') + ' /> Only accept mail from the addresses below</label>' +
                '<label class="form-label" for="ac-allow">Allowed addresses — one per line: an IP (<span class="mono">10.0.0.5</span>) or a CIDR range (<span class="mono">192.168.1.0/24</span>, <span class="mono">fd00::/8</span>)</label>' +
                '<textarea class="form-control mono" id="ac-allow" rows="8" spellcheck="false" placeholder="192.168.1.0/24&#10;10.20.0.15">' + esc(s.allow.join('\n')) + '</textarea>' +
                '<div style="display:flex;gap:.5rem;align-items:center;margin-top:.5rem;flex-wrap:wrap">' +
                (r.yourAddress ? '<button type="button" class="btn btn-sm" data-action="access-add-me" data-ip="' + esc(r.yourAddress) + '"><i class="i i-plus"></i> Add my address (' + esc(r.yourAddress) + ')</button>' : '') +
                '</div>' +
                '<label class="switch" style="margin-top:1rem"><input type="checkbox" id="ac-loopback"' + (s.alwaysAllowLoopback ? ' checked' : '') + ' /> Always allow this machine (127.0.0.1 / ::1)</label>' +
                '<div class="form-text">Lines starting with <span class="mono">#</span> are ignored. Behind Docker’s port mapping, the address seen may be the Docker gateway rather than the real client; check Logs for the address of each connection.</div>',
                '<button type="button" class="btn" data-close>Cancel</button><button type="button" class="btn btn-primary" data-action="save-access"><i class="i i-check"></i> Save</button>');
        }).catch(function (e) { toast('Could not load SMTP access settings: ' + e.message, 'error'); });
    }

    function saveAccess() {
        var body = {
            enabled: $('ac-enabled').checked,
            allow: $('ac-allow').value.split(/\r?\n/).map(function (l) { return l.trim(); }).filter(Boolean),
            alwaysAllowLoopback: $('ac-loopback').checked,
        };
        fetch('api/settings/smtp-access', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
            .then(function (r) { return r.json().then(function (b) { if (!r.ok) throw new Error(b.error || r.status); return b; }); })
            .then(function (s) { closeModal(); toast(s.enabled ? 'SMTP allowlist on: ' + s.allow.length + ' entr' + (s.allow.length === 1 ? 'y' : 'ies') : 'SMTP allowlist off: everyone may send'); loadSettings(); })
            .catch(function (e) { toast('Save failed: ' + e.message, 'error'); });
    }

    // ---------- folders / search ----------
    var folderTitles = { all: 'Inbox', unread: 'Unread', attachments: 'With attachments' };
    function setFolder(f, silent) {
        if (state.view !== 'mail') showView('mail');
        state.folder = f;
        document.querySelectorAll('[data-folder]').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-folder') === f); });
        updateTitle();
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
        var viewEl = t.closest('[data-view]');
        if (viewEl) { showView(viewEl.getAttribute('data-view')); return; }
        var previewEl = t.closest('[data-preview]');
        if (previewEl) { setPreview(previewEl.getAttribute('data-preview')); return; }
        var inboxEl = t.closest('[data-inbox]');
        if (inboxEl) { setInbox(inboxEl.getAttribute('data-inbox')); return; }
        if (t.closest('[data-close]') && (t.closest('button[data-close]') || t.classList.contains('sm-modal-backdrop'))) { closeModal(); return; }

        var actionEl = t.closest('[data-action]');
        if (!actionEl) return;
        if (actionEl.tagName === 'A' && actionEl.getAttribute('href') === '#') e.preventDefault();
        switch (actionEl.getAttribute('data-action')) {
            case 'refresh': loadList(); loadSettings(); loadInboxes(); break;
            case 'new-inbox': $('nav-toggle').checked = false; openNewInbox(); break;
            case 'create-inbox': createInbox(); break;
            case 'inbox-settings': openInboxSettings(); break;
            case 'save-inbox': saveInbox(); break;
            case 'delete-inbox': deleteInbox(); break;
            case 'more': loadList(true); break;
            case 'back': clearSelection(); break;
            case 'delete': deleteSelected(); break;
            case 'toggle-read':
                if (state.detail) { markRead(state.detail.id, false); toast('Marked as unread'); }
                break;
            case 'read-all':
                api('messages/read-all' + inboxScope(), { method: 'POST' }).then(function () {
                    toast(state.inbox ? 'All messages in ' + inboxName(state.inbox) + ' marked as read' : 'All messages marked as read');
                });
                break;
            case 'delete-all':
                if (!state.stats.total) { toast(state.inbox ? inboxName(state.inbox) + ' is already empty' : 'Mailbox is already empty'); break; }
                if (confirm('Delete all ' + state.stats.total + ' messages' + (state.inbox ? ' in ' + inboxName(state.inbox) : ' in every inbox') + '? This cannot be undone.'))
                    api('messages' + inboxScope(), { method: 'DELETE' }).then(function (r) { clearSelection(); toast('Deleted ' + r.removed + ' messages'); });
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
            case 'status-refresh': loadStatus(); break;
            case 'check-links': checkLinks(); break;
            case 'webhooks': $('nav-toggle').checked = false; openWebhooks(); break;
            case 'add-webhook': addWebhookRow(); break;
            case 'save-webhooks': saveWebhooks(); break;
            case 'test-webhook': testWebhook(actionEl.closest('.hook-row')); break;
            case 'smtp-rules': $('nav-toggle').checked = false; openRules(); break;
            case 'add-rule': $('rule-rows').insertAdjacentHTML('beforeend', ruleRow({})); break;
            case 'reset-rules': api('settings/smtp-rules/defaults').then(openRules); break;
            case 'save-rules': saveRules(); break;
            case 'remove-row': actionEl.closest('.editor-row').remove(); break;
            case 'smtp-access': $('nav-toggle').checked = false; openAccess(); break;
            case 'save-access': saveAccess(); break;
            case 'access-add-me':
                var ta = $('ac-allow'), ip = actionEl.getAttribute('data-ip');
                if (ta.value.split(/\r?\n/).map(function (l) { return l.trim(); }).indexOf(ip) < 0) ta.value = (ta.value.trim() ? ta.value.trim() + '\n' : '') + ip;
                break;
            case 'log-pause':
                logState.paused = !logState.paused;
                actionEl.textContent = logState.paused ? 'Resume' : 'Pause';
                if (!logState.paused) pollLogs();
                break;
            case 'log-clear':
                if (confirm('Clear the log lines kept in memory?')) api('logs', { method: 'DELETE' }).then(function () { reloadLogs(); toast('Logs cleared'); });
                break;
        }
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && $('modal-root').innerHTML) { closeModal(); return; }
        if (/INPUT|TEXTAREA|SELECT/.test(e.target.tagName) || $('modal-root').innerHTML || e.ctrlKey || e.metaKey || e.altKey || state.view !== 'mail') return;
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
    loadInboxes().then(function () {
        updateTitle();
        return loadList();
    }).then(function () {
        var id = location.hash.slice(1);
        if (/^[0-9a-f]{32}$/.test(id)) select(id);
    });
    if (pageViews.indexOf(location.hash.slice(1)) >= 0) showView(location.hash.slice(1));
    if (state.view !== 'status') loadStatus();
    // Keep the sidebar health dot current even when the status view is closed.
    setInterval(function () { if (state.view !== 'status') loadStatus(); }, 30000);
    connectEvents();
})();
