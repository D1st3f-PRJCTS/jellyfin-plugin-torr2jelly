(function () {
    'use strict';

    function initTorr2Jelly(view) {
        // State
        let currentTab = 'search';
        let searchResults = [];
        let activeFilter = 'all'; // 'all' or 'gold'
        let currentSort = 'priority';
        let torrents = [];
        let refreshInterval = null;

        // Elements
        const daemonBadge = view.querySelector('#t2jDaemonBadge');
        const daemonBadgeText = view.querySelector('#t2jDaemonBadgeText');
        const activeTorrentsBadge = view.querySelector('#activeTorrentsBadge');
        const syncNowBtn = view.querySelector('#t2jSyncNowBtn');
        const syncText = view.querySelector('#t2jSyncText');

        const searchForm = view.querySelector('#t2jSearchForm');
        const searchInput = view.querySelector('#t2jSearchInput');
        const clearSearchBtn = view.querySelector('#t2jClearSearchBtn');
        const searchCategorySelect = view.querySelector('#t2jSearchCategorySelect');
        const searchBtnText = view.querySelector('#t2jSearchBtnText');
        const searchSpinner = view.querySelector('#t2jSearchSpinner');
        const resultsWrapper = view.querySelector('#t2jResultsWrapper');
        const resultsList = view.querySelector('#t2jResultsList');
        const resultsCount = view.querySelector('#t2jResultsCount');
        const goldCountBadge = view.querySelector('#goldCountBadge');
        const filterAllBtn = view.querySelector('#filterAllBtn');
        const filterGoldBtn = view.querySelector('#filterGoldBtn');
        const sortSelect = view.querySelector('#t2jSortSelect');

        const directLinkForm = view.querySelector('#t2jDirectLinkForm');
        const directLinkInput = view.querySelector('#directLinkInput');
        const directTitleInput = view.querySelector('#directTitleInput');
        const directCategorySelect = view.querySelector('#directCategorySelect');
        const directSyncCheck = view.querySelector('#directSyncCheck');
        const directSubmitBtn = view.querySelector('#directSubmitBtn');

        const dashTorrentsCount = view.querySelector('#dashTorrentsCount');
        const dashDaemonEngine = view.querySelector('#dashDaemonEngine');
        const dashCacheSize = view.querySelector('#dashCacheSize');
        const dashMediaDir = view.querySelector('#dashMediaDir');
        const torrentsListTable = view.querySelector('#torrentsListTable');
        const torrentsEmptyNotice = view.querySelector('#torrentsEmptyNotice');
        const refreshTorrentsBtn = view.querySelector('#refreshTorrentsBtn');
        const scanLibraryBtn = view.querySelector('#scanLibraryBtn');

        const cfgCacheSizeInput = view.querySelector('#cfgCacheSizeInput');
        const cfgStreamUrlInput = view.querySelector('#cfgStreamUrlInput');
        const cfgProwlarrUrlInput = view.querySelector('#cfgProwlarrUrlInput');
        const cfgProwlarrKeyInput = view.querySelector('#cfgProwlarrKeyInput');
        const cfgMediaDirInput = view.querySelector('#cfgMediaDirInput');
        const cfgOnlyGoldCheck = view.querySelector('#cfgOnlyGoldCheck');
        const saveSettingsBtn = view.querySelector('#saveSettingsBtn');
        const saveSettingsBtnText = view.querySelector('#saveSettingsBtnText');
        const restartDaemonBtn = view.querySelector('#restartDaemonBtn');

        if (!document.getElementById('torr2jelly-custom-css')) {
            const link = document.createElement('link');
            link.id = 'torr2jelly-custom-css';
            link.rel = 'stylesheet';
            link.href = (window.ApiClient && typeof window.ApiClient.getUrl === 'function') 
                ? window.ApiClient.getUrl('ConfigurationPage', { name: 'torr2jellycss' }) 
                : 'ConfigurationPage?name=torr2jellycss';
            document.head.appendChild(link);
        }
        if (!document.getElementById('torr2jelly-fontawesome')) {
            const fa = document.createElement('link');
            fa.id = 'torr2jelly-fontawesome';
            fa.rel = 'stylesheet';
            fa.href = 'https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.1/css/all.min.css';
            document.head.appendChild(fa);
        }

        function getApiUrl(path) {
            if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
                return window.ApiClient.getUrl(path);
            }
            return path;
        }

        function getAuthHeaders() {
            const headers = { 'Content-Type': 'application/json' };
            if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
                headers['X-Emby-Token'] = window.ApiClient.accessToken();
            }
            return headers;
        }

        function escapeHtml(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        }

        function formatBytes(bytes) {
            if (!bytes || bytes === 0) return '0 B';
            const k = 1024;
            const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
            const i = Math.floor(Math.log(bytes) / Math.log(k));
            return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
        }

        // Tab Navigation
        view.querySelectorAll('.t2j-nav-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const tab = btn.getAttribute('data-tab');
                switchTab(tab);
            });
        });

        function switchTab(tab) {
            currentTab = tab;
            view.querySelectorAll('.t2j-nav-btn').forEach(b => {
                if (b.getAttribute('data-tab') === tab) {
                    b.classList.add('active');
                } else {
                    b.classList.remove('active');
                }
            });

            view.querySelectorAll('.t2j-tab-content').forEach(c => c.classList.add('hidden'));

            if (tab === 'search') view.querySelector('#t2jTabSearch').classList.remove('hidden');
            if (tab === 'magnet') view.querySelector('#t2jTabMagnet').classList.remove('hidden');
            if (tab === 'dashboard') {
                view.querySelector('#t2jTabDashboard').classList.remove('hidden');
                loadTorrents();
                startPolling();
            } else {
                stopPolling();
            }
            if (tab === 'settings') {
                view.querySelector('#t2jTabSettings').classList.remove('hidden');
                loadSettings();
            }
        }

        // Check Daemon Status
        async function checkStatus() {
            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Status'), { headers: getAuthHeaders() });
                if (res.ok) {
                    const data = await res.json();
                    daemonBadge.className = 'inline-flex items-center gap-2 px-3 py-1.5 rounded-xl text-xs font-semibold bg-emerald-500/15 text-emerald-400 border border-emerald-500/30';
                    daemonBadgeText.innerText = `TorrServer Online (${data.DaemonVersion || 'MatriX'})`;
                    dashDaemonEngine.innerText = `TorrServer ${data.DaemonVersion || 'MatriX'}`;
                    dashCacheSize.innerText = data.EffectiveUrl || 'http://torrserver:8090';
                    activeTorrentsBadge.innerText = data.ActiveTorrentsCount || 0;
                    dashTorrentsCount.innerText = data.ActiveTorrentsCount || 0;
                } else {
                    daemonBadge.className = 'inline-flex items-center gap-2 px-3 py-1.5 rounded-xl text-xs font-semibold bg-rose-500/15 text-rose-400 border border-rose-500/30';
                    daemonBadgeText.innerText = 'TorrServer Offline';
                }
            } catch {
                daemonBadge.className = 'inline-flex items-center gap-2 px-3 py-1.5 rounded-xl text-xs font-semibold bg-rose-500/15 text-rose-400 border border-rose-500/30';
                daemonBadgeText.innerText = 'TorrServer Offline';
            }
        }

        // Search Action
        searchForm.addEventListener('submit', async (e) => {
            e.preventDefault();
            const query = searchInput.value.trim();
            if (!query) return;

            searchSpinner.classList.remove('hidden');
            resultsWrapper.classList.add('hidden');
            resultsList.innerHTML = '';
            searchBtnText.innerText = 'Шукаємо...';

            try {
                const cat = searchCategorySelect.value;
                const url = getApiUrl(`Plugins/Torr2Jelly/Search?query=${encodeURIComponent(query)}&category=${encodeURIComponent(cat)}`);
                const res = await fetch(url, { headers: getAuthHeaders() });
                searchResults = await res.json();

                renderSearchResults();
            } catch (err) {
                resultsList.innerHTML = `<div class="p-6 text-center text-rose-400 font-medium">Помилка пошуку: ${err.message}</div>`;
                resultsWrapper.classList.remove('hidden');
            } finally {
                searchSpinner.classList.add('hidden');
                searchBtnText.innerText = 'Знайти';
            }
        });

        searchInput.addEventListener('input', () => {
            if (searchInput.value.trim()) {
                clearSearchBtn.classList.remove('hidden');
            } else {
                clearSearchBtn.classList.add('hidden');
            }
        });

        clearSearchBtn.addEventListener('click', () => {
            searchInput.value = '';
            clearSearchBtn.classList.add('hidden');
            resultsWrapper.classList.add('hidden');
            searchResults = [];
        });

        // Filter & Sort
        filterAllBtn.addEventListener('click', () => {
            activeFilter = 'all';
            filterAllBtn.className = 'px-2.5 py-1 rounded-lg border text-xs transition-all bg-slate-800 border-slate-600 text-white font-bold';
            filterGoldBtn.className = 'px-2.5 py-1 rounded-lg border text-xs transition-all border-slate-800 hover:bg-slate-800/60 text-amber-400/80 flex items-center space-x-1.5';
            renderSearchResults();
        });

        filterGoldBtn.addEventListener('click', () => {
            activeFilter = 'gold';
            filterGoldBtn.className = 'px-2.5 py-1 rounded-lg border text-xs transition-all bg-amber-500/20 border-amber-500/40 text-amber-300 font-bold flex items-center space-x-1.5';
            filterAllBtn.className = 'px-2.5 py-1 rounded-lg border text-xs transition-all border-slate-800 hover:bg-slate-800/60 text-slate-400';
            renderSearchResults();
        });

        sortSelect.addEventListener('change', () => {
            currentSort = sortSelect.value;
            renderSearchResults();
        });

        function renderSearchResults() {
            if (!searchResults) searchResults = [];

            let filtered = searchResults.slice();
            const goldTotal = filtered.filter(r => r.IsGold || r.isGold).length;
            goldCountBadge.innerText = goldTotal;

            if (activeFilter === 'gold') {
                filtered = filtered.filter(r => r.IsGold || r.isGold);
            }

            // Sort
            if (currentSort === 'seeders') {
                filtered.sort((a, b) => (b.Seeders || b.seeders || 0) - (a.Seeders || a.seeders || 0));
            } else if (currentSort === 'size_desc') {
                filtered.sort((a, b) => (b.Size || b.size || 0) - (a.Size || a.size || 0));
            } else if (currentSort === 'size_asc') {
                filtered.sort((a, b) => (a.Size || a.size || 0) - (b.Size || b.size || 0));
            }

            resultsCount.innerText = filtered.length;
            resultsList.innerHTML = '';

            if (filtered.length === 0) {
                resultsList.innerHTML = '<div class="p-8 text-center text-slate-400">Нічого не знайдено за заданими критеріями фільтрації.</div>';
                resultsWrapper.classList.remove('hidden');
                return;
            }

            filtered.forEach(item => {
                const isGold = item.IsGold || item.isGold;
                const isFreeleech = item.IsFreeleech || item.isFreeleech;
                const title = item.Title || item.title || '';
                const size = item.SizeFormatted || formatBytes(item.Size || item.size || 0);
                const seeders = item.Seeders || item.seeders || 0;
                const peers = item.Peers || item.peers || 0;
                const indexer = item.Indexer || item.indexer || 'Tracker';
                const link = item.MagnetUrl || item.magnetUrl || item.DownloadUrl || item.downloadUrl || '';

                const row = document.createElement('div');
                row.className = `t2j-result-row flex flex-col sm:flex-row sm:items-center justify-between gap-4 ${isGold ? 'is-gold' : ''}`;

                const goldBadge = isGold
                    ? `<span class="text-xs px-2.5 py-0.5 rounded-full bg-gradient-to-r from-amber-500/20 to-yellow-500/20 text-amber-300 border border-amber-500/40 font-bold flex items-center space-x-1 shadow-sm shadow-amber-500/20">
                         <span>⚡ ЗОЛОТА (Freeleech)</span>
                       </span>`
                    : (isFreeleech ? `<span class="text-xs px-2.5 py-0.5 rounded-full bg-slate-400/20 text-slate-300 border border-slate-400/40 font-bold">50% Freeleech</span>` : '');

                row.innerHTML = `
                  <div class="flex-1 min-w-0 space-y-1.5">
                    <div class="flex items-center space-x-2 flex-wrap gap-y-1">
                      ${goldBadge}
                      <span class="text-xs px-2 py-0.5 rounded bg-slate-800 text-slate-300 border border-slate-700 font-semibold">${escapeHtml(indexer)}</span>
                      <span class="text-xs text-slate-400 font-medium">${size}</span>
                      <span class="text-xs text-emerald-400 font-semibold flex items-center space-x-1">
                        <i class="fa-solid fa-arrow-up text-[10px]"></i>
                        <span>${seeders} сидів</span>
                      </span>
                      <span class="text-xs text-slate-500 flex items-center space-x-1">
                        <i class="fa-solid fa-arrow-down text-[10px]"></i>
                        <span>${peers} пірів</span>
                      </span>
                    </div>

                    <div>
                      <div class="text-sm font-bold text-white hover:text-amber-400 transition-colors line-clamp-1" title="${escapeHtml(title)}">
                        ${escapeHtml(title)}
                      </div>
                    </div>
                  </div>

                  <div class="flex items-center gap-2 shrink-0">
                    <select class="t2j-row-cat-select bg-slate-900 border border-slate-700 rounded-xl px-3 py-2 text-xs text-slate-200 focus:outline-none focus:border-brand-500">
                      <option value="Movies" ${searchCategorySelect.value === 'Movies' ? 'selected' : ''}>Фільми</option>
                      <option value="Shows" ${searchCategorySelect.value === 'Shows' ? 'selected' : ''}>Серіали</option>
                      <option value="Cartoons" ${searchCategorySelect.value === 'Cartoons' ? 'selected' : ''}>Мультфільми</option>
                      <option value="Anime" ${searchCategorySelect.value === 'Anime' ? 'selected' : ''}>Аніме</option>
                    </select>

                    <button type="button" class="t2j-add-row-btn px-4 py-2.5 rounded-xl text-xs font-semibold shadow-md active:scale-95 transition-all flex items-center justify-center space-x-1.5 ${isGold ? 'bg-gradient-to-r from-amber-500 to-yellow-600 hover:from-amber-400 hover:to-yellow-500 text-slate-950 font-bold' : 'bg-brand-600 hover:bg-brand-500 text-white shadow-brand-600/30'}">
                      <i class="fa-solid fa-play text-[10px]"></i>
                      <span>Додати в бібліотеку</span>
                    </button>
                  </div>
                `;

                const addBtn = row.querySelector('.t2j-add-row-btn');
                const catSelect = row.querySelector('.t2j-row-cat-select');

                addBtn.addEventListener('click', async () => {
                    addBtn.disabled = true;
                    addBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i> <span>Додаємо...</span>';

                    try {
                        const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Torrents'), {
                            method: 'POST',
                            headers: getAuthHeaders(),
                            body: JSON.stringify({
                                Link: link,
                                Title: title,
                                Category: catSelect.value,
                                SyncNow: true
                            })
                        });

                        if (res.ok) {
                            addBtn.className = 'px-4 py-2.5 rounded-xl text-xs font-semibold bg-emerald-600 text-white flex items-center space-x-1.5';
                            addBtn.innerHTML = '<i class="fa-solid fa-check text-xs"></i> <span>Додано в медіатеку!</span>';
                            checkStatus();
                        } else {
                            addBtn.disabled = false;
                            addBtn.innerHTML = '<i class="fa-solid fa-triangle-exclamation text-xs"></i> <span>Помилка</span>';
                        }
                    } catch {
                        addBtn.disabled = false;
                        addBtn.innerHTML = '<i class="fa-solid fa-triangle-exclamation text-xs"></i> <span>Помилка</span>';
                    }
                });

                resultsList.appendChild(row);
            });

            resultsWrapper.classList.remove('hidden');
        }

        // Direct Link submit
        directLinkForm.addEventListener('submit', async (e) => {
            e.preventDefault();
            const link = directLinkInput.value.trim();
            if (!link) return;

            directSubmitBtn.disabled = true;
            directSubmitBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i> <span>Додаємо...</span>';

            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Torrents'), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    body: JSON.stringify({
                        Link: link,
                        Title: directTitleInput.value.trim() || null,
                        Category: directCategorySelect.value,
                        SyncNow: directSyncCheck.checked
                    })
                });

                if (res.ok) {
                    directSubmitBtn.innerHTML = '<i class="fa-solid fa-check"></i> <span>Успішно додано!</span>';
                    directLinkInput.value = '';
                    directTitleInput.value = '';
                    setTimeout(() => {
                        directSubmitBtn.disabled = false;
                        directSubmitBtn.innerHTML = '<i class="fa-solid fa-plus"></i> <span>Додати в медіатеку</span>';
                    }, 2500);
                    checkStatus();
                } else {
                    directSubmitBtn.disabled = false;
                    directSubmitBtn.innerHTML = '<i class="fa-solid fa-triangle-exclamation"></i> <span>Помилка</span>';
                }
            } catch {
                directSubmitBtn.disabled = false;
                directSubmitBtn.innerHTML = '<i class="fa-solid fa-triangle-exclamation"></i> <span>Помилка</span>';
            }
        });

        // Load Torrents
        async function loadTorrents() {
            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Torrents'), { headers: getAuthHeaders() });
                torrents = await res.json();

                dashTorrentsCount.innerText = torrents.length;
                activeTorrentsBadge.innerText = torrents.length;
                torrentsListTable.innerHTML = '';

                if (!torrents || torrents.length === 0) {
                    torrentsEmptyNotice.classList.remove('hidden');
                    return;
                }

                torrentsEmptyNotice.classList.add('hidden');

                torrents.forEach(t => {
                    const title = t.title || t.Title || t.hash || t.Hash;
                    const hash = t.hash || t.Hash;
                    const size = formatBytes(t.torrent_size || t.TorrentSize || 0);
                    const loaded = formatBytes(t.loaded_size || t.LoadedSize || 0);
                    const statusStr = t.stat_string || t.StatString || 'Активний';

                    const row = document.createElement('div');
                    row.className = 'py-4 flex flex-col sm:flex-row sm:items-center justify-between gap-3';
                    row.innerHTML = `
                      <div class="flex-1 min-w-0">
                        <h4 class="text-sm font-bold text-white truncate" title="${escapeHtml(title)}">${escapeHtml(title)}</h4>
                        <div class="flex items-center space-x-3 text-xs text-slate-400 mt-1">
                          <span class="text-emerald-400 font-semibold">${escapeHtml(statusStr)}</span>
                          <span>Розмір: ${size}</span>
                          <span>Кеш: ${loaded}</span>
                          <span class="font-mono text-[10px] text-slate-600 truncate max-w-[120px]">${hash}</span>
                        </div>
                      </div>

                      <div class="flex items-center space-x-2">
                        <button type="button" class="t2j-del-torrent-btn px-3 py-1.5 rounded-lg bg-rose-500/15 hover:bg-rose-500/25 text-rose-300 border border-rose-500/30 text-xs font-semibold transition-all">
                          <i class="fa-solid fa-trash-can text-[11px] mr-1"></i> Видалити
                        </button>
                      </div>
                    `;

                    row.querySelector('.t2j-del-torrent-btn').addEventListener('click', async () => {
                        if (confirm(`Видалити торент "${title}" та відповідні .strm файли з медіатеки?`)) {
                            await fetch(getApiUrl(`Plugins/Torr2Jelly/Torrents/${hash}?deleteStrm=true`), {
                                method: 'DELETE',
                                headers: getAuthHeaders()
                            });
                            loadTorrents();
                            checkStatus();
                        }
                    });

                    torrentsListTable.appendChild(row);
                });
            } catch (err) {
                torrentsListTable.innerHTML = `<div class="p-4 text-rose-400 text-xs">Помилка завантаження: ${err.message}</div>`;
            }
        }

        refreshTorrentsBtn.addEventListener('click', loadTorrents);
        scanLibraryBtn.addEventListener('click', async () => {
            scanLibraryBtn.disabled = true;
            scanLibraryBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin text-xs"></i> <span>Скануємо...</span>';
            await fetch(getApiUrl('Plugins/Torr2Jelly/Library/Scan'), { method: 'POST', headers: getAuthHeaders() });
            setTimeout(() => {
                scanLibraryBtn.disabled = false;
                scanLibraryBtn.innerHTML = '<i class="fa-solid fa-folder-tree text-xs"></i> <span>Сканувати медіатеку</span>';
            }, 2000);
        });

        syncNowBtn.addEventListener('click', async () => {
            syncNowBtn.disabled = true;
            syncText.innerText = 'Синхронізація...';
            await fetch(getApiUrl('Plugins/Torr2Jelly/Library/Scan'), { method: 'POST', headers: getAuthHeaders() });
            setTimeout(() => {
                syncNowBtn.disabled = false;
                syncText.innerText = 'Синхронізація';
            }, 2000);
        });

        function startPolling() {
            stopPolling();
            refreshInterval = setInterval(loadTorrents, 5000);
        }

        function stopPolling() {
            if (refreshInterval) {
                clearInterval(refreshInterval);
                refreshInterval = null;
            }
        }

        // Settings load & save
        async function loadSettings() {
            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Settings'), { headers: getAuthHeaders() });
                const cfg = await res.json();
                cfgCacheSizeInput.value = cfg.CacheSizeMb || 102400;
                cfgStreamUrlInput.value = cfg.TorrServerStreamUrl || '';
                cfgProwlarrUrlInput.value = cfg.ProwlarrUrl || '';
                cfgProwlarrKeyInput.value = cfg.ProwlarrApiKey || '';
                cfgMediaDirInput.value = cfg.MediaRootPath || '';
                cfgOnlyGoldCheck.checked = cfg.OnlyGoldTorrentsForNonAdmins;
                dashMediaDir.innerText = cfg.MediaRootPath || '/media';
            } catch (err) {
                console.error('Failed to load settings', err);
            }
        }

        saveSettingsBtn.addEventListener('click', async () => {
            saveSettingsBtn.disabled = true;
            saveSettingsBtnText.innerText = 'Збереження...';

            const payload = {
                UseDiskCache: true,
                DiskCachePath: '/mnt/sdb/AppFiles/jellyfin/cache/torrserver',
                CacheSizeMb: parseInt(cfgCacheSizeInput.value, 10) || 102400,
                TorrServerStreamUrl: cfgStreamUrlInput.value.trim(),
                ProwlarrUrl: cfgProwlarrUrlInput.value.trim(),
                ProwlarrApiKey: cfgProwlarrKeyInput.value.trim(),
                MediaRootPath: cfgMediaDirInput.value.trim(),
                OnlyGoldTorrentsForNonAdmins: cfgOnlyGoldCheck.checked
            };

            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Settings'), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    body: JSON.stringify(payload)
                });

                if (res.ok) {
                    saveSettingsBtnText.innerText = '✓ Збережено!';
                    setTimeout(() => {
                        saveSettingsBtnText.innerText = 'Зберегти налаштування';
                        saveSettingsBtn.disabled = false;
                    }, 2000);
                    checkStatus();
                } else {
                    saveSettingsBtnText.innerText = 'Помилка';
                    saveSettingsBtn.disabled = false;
                }
            } catch {
                saveSettingsBtnText.innerText = 'Помилка';
                saveSettingsBtn.disabled = false;
            }
        });

        restartDaemonBtn.addEventListener('click', async () => {
            restartDaemonBtn.disabled = true;
            restartDaemonBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i> <span>Перезапуск...</span>';

            try {
                await fetch(getApiUrl('Plugins/Torr2Jelly/Daemon/Restart'), { method: 'POST', headers: getAuthHeaders() });
                setTimeout(() => {
                    restartDaemonBtn.disabled = false;
                    restartDaemonBtn.innerHTML = '<i class="fa-solid fa-power-off text-amber-400"></i> <span>Перезапустити торент-рушій</span>';
                    checkStatus();
                }, 3000);
            } catch {
                restartDaemonBtn.disabled = false;
                restartDaemonBtn.innerHTML = '<i class="fa-solid fa-triangle-exclamation"></i> <span>Помилка</span>';
            }
        });

        // Initialize
        checkStatus();
    }

    if (typeof define === 'function' && define.amd) {
        define(['loading'], function () {
            return function (view) {
                view.addEventListener('viewshow', function () {
                    initTorr2Jelly(view);
                });
            };
        });
    } else {
        document.addEventListener('DOMContentLoaded', () => {
            const page = document.getElementById('torr2jellyPage');
            if (page) initTorr2Jelly(page);
        });
    }
})();
