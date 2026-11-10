const PLUGIN_ID = '61a62eb4-5383-489b-8150-3b595e6a6400';

const CONTENT_TYPES = [
    ['movies', 'Movies'],
    ['tvshows', 'Shows'],
    ['music', 'Music'],
    ['musicvideos', 'Music videos'],
    ['homevideos', 'Home videos'],
    ['mixed', 'Mixed movies and shows']
];

const NUMBER_SETTINGS = ['SyncIntervalMinutes', 'ReadAheadMegabytes', 'ConnectionsPerFile', 'DiskCacheMegabytes'];
const TEXT_SETTINGS = ['StreamBaseUrl'];
const BOOL_SETTINGS = ['RestrictStreamToLocalhost', 'UseMegaPreviews', 'ExtractFrameWhenNoPreview', 'ProbeAfterScan'];

const LIBRARY_TEMPLATE = `
<div class="megaRow">
    <div class="selectContainer"><select is="emby-select" class="megaLibAccount" label="MEGA source"></select></div>
    <div class="inputContainer"><input is="emby-input" type="text" class="megaLibPath" label="MEGA folder"></div>
    <button is="emby-button" type="button" class="raised emby-button megaSmallButton megaLibBrowse"><span>Browse…</span></button>
</div>
<div class="megaPicker hide"></div>
<div class="megaRow">
    <div class="inputContainer"><input is="emby-input" type="text" class="megaLibName" label="Jellyfin library name"></div>
    <div class="selectContainer"><select is="emby-select" class="megaLibType" label="Content type"></select></div>
</div>
<div class="megaRow" style="align-items:center">
    <label class="checkboxContainer" style="margin:0"><input is="emby-checkbox" type="checkbox" class="megaLibEnabled"><span>Enabled</span></label>
    <label class="checkboxContainer" style="margin:0"><input is="emby-checkbox" type="checkbox" class="megaLibCreate"><span>Create the Jellyfin library</span></label>
    <span style="flex:1"></span>
    <button is="emby-button" type="button" class="raised emby-button megaSmallButton megaLibRemove"><span>Remove</span></button>
</div>
<div class="megaMuted megaLibStatus"></div>`;

function el(tag, props, ...children) {
    const e = document.createElement(tag);
    Object.assign(e, props || {});
    for (const c of children) {
        if (c !== null && c !== undefined) {
            e.append(c);
        }
    }

    return e;
}

function newId() {
    return (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()).replace(/-/g, '');
}

function formatDate(value) {
    return value ? new Date(value).toLocaleString() : 'never';
}

async function errorText(e) {
    if (e && typeof e.text === 'function') {
        try {
            let t = await e.text();
            try {
                const j = JSON.parse(t);
                t = typeof j === 'string' ? j : (j.title || j.detail || t);
            } catch {
                // plain text
            }

            return t || ('HTTP ' + e.status);
        } catch {
            return 'HTTP ' + e.status;
        }
    }

    return (e && e.message) || String(e);
}

async function api(method, path, body, json) {
    const request = { type: method, url: ApiClient.getUrl(path) };
    if (body !== undefined) {
        request.data = JSON.stringify(body);
        request.contentType = 'application/json';
    }

    if (json) {
        request.dataType = 'json';
    }

    try {
        return await ApiClient.ajax(request);
    } catch (e) {
        throw new Error(await errorText(e));
    }
}

export default function (view) {
    let config = null;
    let status = null;
    let timer = null;

    const $ = (sel) => view.querySelector(sel);
    const librariesHost = $('#megaLibraries');

    function accountLabel(a) {
        if (a.Kind === 'folder') {
            return (a.Name || 'Folder link') + ' (public folder link)';
        }

        return (a.Name || a.Email) + (a.Email && a.Name !== a.Email ? ' (' + a.Email + ')' : '');
    }

    function fillAccountSelect(select, selected) {
        select.innerHTML = '';
        for (const a of config.Accounts) {
            select.append(el('option', { value: a.Id, textContent: accountLabel(a) }));
        }

        if (!config.Accounts.length) {
            select.append(el('option', { value: '', textContent: 'Add a MEGA source first' }));
        }

        select.value = config.Accounts.some(a => a.Id === selected) ? selected : (config.Accounts[0] ? config.Accounts[0].Id : '');
    }

    function addLibraryRow(lib) {
        const row = el('div', { className: 'megaCard megaLibrary' });
        row.dataset.id = lib.Id || newId();
        row.innerHTML = LIBRARY_TEMPLATE;
        librariesHost.append(row);

        const type = row.querySelector('.megaLibType');
        for (const [value, label] of CONTENT_TYPES) {
            type.append(el('option', { value, textContent: label }));
        }

        fillAccountSelect(row.querySelector('.megaLibAccount'), lib.AccountId);
        row.querySelector('.megaLibPath').value = lib.MegaPath || '/';
        row.querySelector('.megaLibName').value = lib.LibraryName || '';
        type.value = lib.ContentType || 'movies';
        row.querySelector('.megaLibEnabled').checked = lib.Enabled !== false;
        row.querySelector('.megaLibCreate').checked = lib.CreateLibrary !== false;
        row.querySelector('.megaLibRemove').addEventListener('click', () => row.remove());
        row.querySelector('.megaLibBrowse').addEventListener('click', () => {
            const picker = row.querySelector('.megaPicker');
            if (picker.classList.contains('hide')) {
                browse(row, row.querySelector('.megaLibPath').value || null);
            } else {
                picker.classList.add('hide');
            }
        });
        row.querySelector('.megaLibAccount').addEventListener('change', () => row.querySelector('.megaPicker').classList.add('hide'));
        renderLibraryStatus(row);
        return row;
    }

    async function browse(row, path) {
        const picker = row.querySelector('.megaPicker');
        const accountId = row.querySelector('.megaLibAccount').value;
        picker.classList.remove('hide');
        if (!accountId) {
            picker.textContent = 'Add a MEGA source first.';
            return;
        }

        picker.textContent = 'Loading the MEGA folders… (the first time can take a while on large accounts)';
        let listing;
        try {
            listing = await api('GET', 'MegaFs/accounts/' + encodeURIComponent(accountId) + '/folders' + (path !== null && path !== undefined && path !== '' ? '?path=' + encodeURIComponent(path) : ''), undefined, true);
        } catch (e) {
            if (path) {
                // Path no longer exists: start from the root.
                return browse(row, null);
            }

            picker.textContent = '';
            picker.append(el('div', { className: 'megaError', textContent: e.message }));
            return;
        }

        picker.innerHTML = '';
        const header = el('div', { className: 'megaRow', style: 'align-items:center;margin-bottom:.4em' });
        header.append(el('strong', { textContent: listing.Path || 'MEGA', style: 'flex:1;overflow-wrap:anywhere' }));
        if (listing.Path) {
            const use = el('button', { type: 'button', className: 'raised button-submit emby-button megaSmallButton', textContent: 'Use this folder' });
            use.addEventListener('click', () => {
                row.querySelector('.megaLibPath').value = listing.Path;
                const name = row.querySelector('.megaLibName');
                if (!name.value) {
                    const parts = listing.Path.replace(/^in:/, '').split('/').filter(Boolean);
                    name.value = parts.length ? parts[parts.length - 1] : 'MEGA';
                }

                picker.classList.add('hide');
            });
            header.append(use);
        }

        picker.append(header);
        if (listing.Path) {
            picker.append(el('div', { className: 'megaMuted', textContent: listing.MediaFiles + ' video and audio files in this folder and its subfolders' }));
        }

        if (listing.Parent !== null && listing.Parent !== undefined) {
            const up = el('button', { type: 'button', className: 'megaFolder', textContent: '⬑ ..' });
            up.addEventListener('click', () => browse(row, listing.Parent));
            picker.append(up);
        }

        for (const f of listing.Folders) {
            const b = el('button', { type: 'button', className: 'megaFolder', textContent: '📁 ' + f.Name });
            b.addEventListener('click', () => browse(row, f.Path));
            picker.append(b);
        }

        if (!listing.Folders.length) {
            picker.append(el('div', { className: 'megaMuted', textContent: 'No subfolder.' }));
        }
    }

    function renderLibraryStatus(row) {
        const target = row.querySelector('.megaLibStatus');
        const st = status && status.Libraries.find(l => l.Id === row.dataset.id);
        target.innerHTML = '';
        if (!st) {
            target.textContent = 'Not synchronized yet: save, then the library is created within a few seconds.';
            return;
        }

        if (st.Error) {
            target.append(el('span', { className: 'megaError', textContent: st.Error }));
            return;
        }

        target.textContent = st.MediaFiles + ' media files, ' + st.SideFiles + ' side files, ' + st.Folders
            + ' folders. Last sync: ' + formatDate(st.LastSync) + '. Local folder: ' + st.LocalPath;
    }

    function renderAccounts() {
        const list = $('#megaAccounts');
        list.innerHTML = '';
        if (!config.Accounts.length) {
            list.append(el('li', {}, el('span', { className: 'megaMuted', textContent: 'No MEGA source yet: add one below.' })));
        }

        for (const a of config.Accounts) {
            const st = status && status.Accounts.find(s => s.Id === a.Id);
            const info = el('div', { className: 'megaGrow' }, el('div', { textContent: accountLabel(a) }));
            let line;
            if (st && st.Error) {
                line = el('div', { className: 'megaError', textContent: st.Error });
            } else if (st && st.Loaded) {
                line = el('div', { className: 'megaMuted', textContent: 'Connected: ' + st.Nodes.toLocaleString() + ' folders and media files, read ' + formatDate(st.LoadedAt) });
            } else {
                line = el('div', { className: 'megaMuted', textContent: 'Connecting…' });
            }

            info.append(line);
            const remove = el('button', { type: 'button', className: 'raised emby-button megaSmallButton', textContent: 'Remove' });
            remove.addEventListener('click', async () => {
                if (!window.confirm('Remove "' + accountLabel(a) + '" and its libraries?')) {
                    return;
                }

                try {
                    await api('DELETE', 'MegaFs/accounts/' + encodeURIComponent(a.Id));
                    await load();
                } catch (e) {
                    Dashboard.alert(e.message);
                }
            });
            list.append(el('li', {}, info, remove));
        }
    }

    function renderStatus() {
        const host = $('#megaStatus');
        host.innerHTML = '';
        if (!status) {
            return;
        }

        host.append(el('div', { textContent: status.Syncing ? 'Synchronizing…' : 'Last synchronization: ' + formatDate(status.LastSync) }));
        host.append(el('div', { textContent: 'Open streams: ' + status.OpenStreams }));
        host.append(el('div', { textContent: 'Virtual libraries are kept in: ' + status.LibraryRoot }));
        renderAccounts();
        librariesHost.querySelectorAll('.megaLibrary').forEach(renderLibraryStatus);
    }

    async function refreshStatus() {
        try {
            status = await api('GET', 'MegaFs/status', undefined, true);
            renderStatus();
        } catch (e) {
            $('#megaStatus').textContent = e.message;
        }
    }

    // Accounts change on the server (log in, remove): reload them without losing unsaved library edits.
    async function reloadAccounts() {
        const fresh = await ApiClient.getPluginConfiguration(PLUGIN_ID);
        config.Accounts = fresh.Accounts;
        librariesHost.querySelectorAll('.megaLibrary').forEach(row => {
            const select = row.querySelector('.megaLibAccount');
            fillAccountSelect(select, select.value);
        });
        renderAccounts();
        await refreshStatus();
    }

    async function load() {
        Dashboard.showLoadingMsg();
        try {
            config = await ApiClient.getPluginConfiguration(PLUGIN_ID);
            for (const k of NUMBER_SETTINGS.concat(TEXT_SETTINGS)) {
                $('#' + k).value = config[k] ?? '';
            }

            for (const k of BOOL_SETTINGS) {
                $('#' + k).checked = !!config[k];
            }

            librariesHost.innerHTML = '';
            config.Libraries.forEach(addLibraryRow);
            renderAccounts();
        } finally {
            Dashboard.hideLoadingMsg();
        }

        await refreshStatus();
    }

    function readLibraries() {
        const libs = [];
        const names = new Set();
        for (const row of librariesHost.querySelectorAll('.megaLibrary')) {
            const lib = {
                Id: row.dataset.id,
                Enabled: row.querySelector('.megaLibEnabled').checked,
                AccountId: row.querySelector('.megaLibAccount').value,
                MegaPath: row.querySelector('.megaLibPath').value.trim() || '/',
                LibraryName: row.querySelector('.megaLibName').value.trim(),
                ContentType: row.querySelector('.megaLibType').value,
                CreateLibrary: row.querySelector('.megaLibCreate').checked
            };
            if (!lib.AccountId) {
                throw new Error('Choose a MEGA source for every library.');
            }

            if (!lib.LibraryName) {
                throw new Error('Give a name to every library.');
            }

            if (names.has(lib.LibraryName.toLowerCase())) {
                throw new Error('Two libraries are named "' + lib.LibraryName + '".');
            }

            names.add(lib.LibraryName.toLowerCase());
            libs.push(lib);
        }

        return libs;
    }

    async function save(e) {
        e.preventDefault();
        let libraries;
        try {
            libraries = readLibraries();
        } catch (err) {
            Dashboard.alert(err.message);
            return false;
        }

        Dashboard.showLoadingMsg();
        try {
            // Start from the server copy: accounts and the stream secret are managed there.
            const fresh = await ApiClient.getPluginConfiguration(PLUGIN_ID);
            fresh.Libraries = libraries;
            for (const k of NUMBER_SETTINGS) {
                const v = parseInt($('#' + k).value, 10);
                if (!Number.isNaN(v)) {
                    fresh[k] = v;
                }
            }

            for (const k of TEXT_SETTINGS) {
                fresh[k] = $('#' + k).value.trim();
            }

            for (const k of BOOL_SETTINGS) {
                fresh[k] = $('#' + k).checked;
            }

            const result = await ApiClient.updatePluginConfiguration(PLUGIN_ID, fresh);
            config = fresh;
            Dashboard.processPluginConfigurationUpdateResult(result);
            setTimeout(refreshStatus, 1500);
        } catch (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert(await errorText(err));
        }

        return false;
    }

    async function sourceAdded(form) {
        form.reset();
        await reloadAccounts();
        if (!librariesHost.querySelector('.megaLibrary')) {
            addLibraryRow({ AccountId: config.Accounts[config.Accounts.length - 1].Id, MegaPath: '/', LibraryName: '' });
        }

        Dashboard.alert('MEGA source added. Pick a folder for your library, then save.');
    }

    async function addSource(form, path, body) {
        Dashboard.showLoadingMsg();
        try {
            await api('POST', path, body, true);
            await sourceAdded(form);
        } catch (e) {
            Dashboard.alert(e.message);
        } finally {
            Dashboard.hideLoadingMsg();
        }
    }

    // The login runs on the server; it can take minutes when MEGA asks for a proof of work.
    async function login(form, body) {
        const line = $('#megaLoginStatus');
        const button = form.querySelector('button[type=submit]');
        button.disabled = true;
        Dashboard.showLoadingMsg();
        try {
            let job = await api('POST', 'MegaFs/accounts/login', body, true);
            while (job.State === 'running') {
                line.textContent = job.Message || 'Logging in…';
                await new Promise(resolve => setTimeout(resolve, 1500));
                job = await api('GET', 'MegaFs/accounts/login/' + encodeURIComponent(job.Id), undefined, true);
            }

            if (job.State !== 'done') {
                throw new Error(job.Message || 'Login failed');
            }

            line.textContent = '';
            await sourceAdded(form);
        } catch (e) {
            line.textContent = '';
            Dashboard.alert(e.message);
        } finally {
            button.disabled = false;
            Dashboard.hideLoadingMsg();
        }
    }

    view.querySelectorAll('.megaTab').forEach(tab => tab.addEventListener('click', () => {
        view.querySelectorAll('.megaTab').forEach(t => t.classList.toggle('button-submit', t === tab));
        view.querySelectorAll('.megaTabPanel').forEach(p => p.classList.toggle('hide', p.dataset.tab !== tab.dataset.tab));
    }));

    $('#megaLoginForm').addEventListener('submit', (e) => {
        e.preventDefault();
        login(e.target, {
            Name: $('#megaLoginName').value.trim() || null,
            Email: $('#megaLoginEmail').value.trim(),
            Password: $('#megaLoginPassword').value,
            Mfa: $('#megaLoginMfa').value.trim() || null
        });
        return false;
    });

    $('#megaSessionForm').addEventListener('submit', (e) => {
        e.preventDefault();
        addSource(e.target, 'MegaFs/accounts/session', {
            Name: $('#megaSessionName').value.trim() || null,
            Session: $('#megaSessionValue').value.trim()
        });
        return false;
    });

    $('#megaLinkForm').addEventListener('submit', (e) => {
        e.preventDefault();
        addSource(e.target, 'MegaFs/accounts/link', {
            Name: $('#megaLinkName').value.trim() || null,
            Link: $('#megaLinkValue').value.trim()
        });
        return false;
    });

    $('#megaAddLibrary').addEventListener('click', () => {
        if (!config) {
            return;
        }

        const row = addLibraryRow({ AccountId: config.Accounts[0] ? config.Accounts[0].Id : '', MegaPath: '/', LibraryName: '' });
        if (config.Accounts.length) {
            browse(row, null);
        }
    });

    $('#megaSyncNow').addEventListener('click', async () => {
        try {
            await api('POST', 'MegaFs/sync');
            setTimeout(refreshStatus, 800);
        } catch (e) {
            Dashboard.alert(e.message);
        }
    });

    $('#megaSettingsForm').addEventListener('submit', save);

    view.addEventListener('viewshow', () => {
        load().catch(e => Dashboard.alert(e.message || String(e)));
        clearInterval(timer);
        timer = setInterval(refreshStatus, 5000);
    });

    view.addEventListener('viewhide', () => {
        clearInterval(timer);
        timer = null;
    });
}
