(function () {
    'use strict';

    const i18n = {
        uk: {
            subtitle: "Автономний торент-рушій та інтеграція з Prowlarr для Jellyfin",
            tabSearch: "Пошук",
            tabDashboard: "Стріми & Швидкість",
            tabSettings: "Налаштування",
            searchPlaceholder: "Введіть назву фільму або серіалу...",
            searchBtn: "Пошук",
            searching: "Шукаємо роздачі на трекерах...",
            noResults: "Нічого не знайдено за вашим запитом.",
            addLink: "Додати лінк",
            streamBtn: "Стримити зараз",
            addedSuccess: "Успішно додано в бібліотеку!",
            deleteConfirm: "Ви впевнені, що хочете видалити цей торент та пов'язані .strm файли?",
            deleted: "Торент видалено.",
            settingsSaved: "Налаштування успішно збережено!",
            daemonRestarted: "Торент-рушій перезапущено.",
            connected: "Підключено",
            offline: "Вимкнено",
            scanTriggered: "Сканування медіатеки запущено!"
        },
        en: {
            subtitle: "Standalone Torrent Streaming & Prowlarr Hub for Jellyfin",
            tabSearch: "Search",
            tabDashboard: "Streams & Speed",
            tabSettings: "Settings",
            searchPlaceholder: "Enter movie or series title...",
            searchBtn: "Search",
            searching: "Searching indexers...",
            noResults: "No torrents found for your query.",
            addLink: "Add Link",
            streamBtn: "Stream Now",
            addedSuccess: "Successfully added to Jellyfin library!",
            deleteConfirm: "Are you sure you want to delete this torrent and associated .strm files?",
            deleted: "Torrent deleted.",
            settingsSaved: "Settings saved successfully!",
            daemonRestarted: "Torrent engine restarted.",
            connected: "Connected",
            offline: "Offline",
            scanTriggered: "Library scan queued!"
        }
    };

    let currentLang = 'uk';

    function initController(view) {
        let activeTab = 'search';
        let refreshInterval = null;

        // UI Element References
        const daemonBadge = view.querySelector('#t2jDaemonStatus');
        const daemonBadgeText = view.querySelector('#t2jDaemonStatusText');
        const searchInput = view.querySelector('#t2jSearchInput');
        const categorySelect = view.querySelector('#t2jCategorySelect');
        const searchBtn = view.querySelector('#t2jSearchBtn');
        const searchLoading = view.querySelector('#t2jSearchLoading');
        const searchResults = view.querySelector('#t2jSearchResults');
        const directMagnetInput = view.querySelector('#t2jDirectMagnetInput');
        const addDirectBtn = view.querySelector('#t2jAddDirectBtn');

        const metricActiveCount = view.querySelector('#metricActiveCount');
        const metricDaemonMode = view.querySelector('#metricDaemonMode');
        const metricStreamUrl = view.querySelector('#metricStreamUrl');
        const activeTorrentsList = view.querySelector('#t2jActiveTorrentsList');
        const refreshTorrentsBtn = view.querySelector('#t2jRefreshTorrentsBtn');
        const triggerScanBtn = view.querySelector('#t2jTriggerScanBtn');

        const cfgUseEmbedded = view.querySelector('#cfgUseEmbedded');
        const cfgPort = view.querySelector('#cfgPort');
        const cfgCacheSize = view.querySelector('#cfgCacheSize');
        const cfgStreamUrl = view.querySelector('#cfgStreamUrl');
        const cfgProwlarrUrl = view.querySelector('#cfgProwlarrUrl');
        const cfgProwlarrApiKey = view.querySelector('#cfgProwlarrApiKey');
        const cfgOnlyGold = view.querySelector('#cfgOnlyGold');
        const saveSettingsBtn = view.querySelector('#t2jSaveSettingsBtn');
        const restartDaemonBtn = view.querySelector('#t2jRestartDaemonBtn');
        const langSelect = view.querySelector('#t2jLangSelect');

        // Api Client helper
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

        // Apply localization
        function applyTranslations(lang) {
            currentLang = lang;
            const t = i18n[lang] || i18n.en;
            view.querySelector('#t2jSubtitle').innerText = t.subtitle;
            view.querySelector('#tabSearchText').innerText = t.tabSearch;
            view.querySelector('#tabDashboardText').innerText = t.tabDashboard;
            view.querySelector('#tabSettingsText').innerText = t.tabSettings;
            searchInput.placeholder = t.searchPlaceholder;
            searchBtn.innerText = t.searchBtn;
            addDirectBtn.innerText = t.addLink;
        }

        langSelect.addEventListener('change', () => applyTranslations(langSelect.value));

        // Tab Switching
        view.querySelectorAll('.t2j-tab-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                view.querySelectorAll('.t2j-tab-btn').forEach(b => b.classList.remove('active'));
                view.querySelectorAll('.t2j-panel').forEach(p => p.classList.remove('active'));

                btn.classList.add('active');
                activeTab = btn.getAttribute('data-tab');
                const panel = view.querySelector(`#panel-${activeTab}`);
                if (panel) panel.classList.add('active');

                if (activeTab === 'dashboard') {
                    loadActiveTorrents();
                    startAutoRefresh();
                } else {
                    stopAutoRefresh();
                }

                if (activeTab === 'settings') {
                    loadSettings();
                }
            });
        });

        // Load Status
        async function checkStatus() {
            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Status'), { headers: getAuthHeaders() });
                if (res.ok) {
                    const data = await res.json();
                    daemonBadge.classList.remove('offline');
                    daemonBadgeText.innerText = `${data.mode}: ${data.daemonVersion || 'v1.2'} (${data.statusMessage})`;
                    metricActiveCount.innerText = data.activeTorrentsCount || 0;
                    metricDaemonMode.innerText = data.mode;
                    metricStreamUrl.innerText = data.streamBaseUrl || '-';
                } else {
                    daemonBadge.classList.add('offline');
                    daemonBadgeText.innerText = i18n[currentLang].offline;
                }
            } catch (e) {
                daemonBadge.classList.add('offline');
                daemonBadgeText.innerText = i18n[currentLang].offline;
            }
        }

        // Search Action
        async function executeSearch() {
            const query = searchInput.value.trim();
            if (!query) return;

            searchLoading.style.display = 'block';
            searchResults.innerHTML = '';

            try {
                const cat = categorySelect.value;
                const url = getApiUrl(`Plugins/Torr2Jelly/Search?query=${encodeURIComponent(query)}&category=${encodeURIComponent(cat)}`);
                const res = await fetch(url, { headers: getAuthHeaders() });
                const items = await res.json();

                searchLoading.style.display = 'none';

                if (!items || items.length === 0) {
                    searchResults.innerHTML = `<div style="text-align:center; padding: 40px; color: #94a3b8;">${i18n[currentLang].noResults}</div>`;
                    return;
                }

                items.forEach(item => {
                    const card = document.createElement('div');
                    card.className = 't2j-card';
                    card.tabIndex = 0;

                    const goldBadge = item.isGold ? '<span class="t2j-badge-gold">⚡ GOLD / FREELEECH</span>' : '';

                    card.innerHTML = `
                        <div class="t2j-card-info">
                            <h4 class="t2j-card-title">${escapeHtml(item.title)}</h4>
                            <div class="t2j-card-meta">
                                ${goldBadge}
                                <span>📦 ${item.sizeFormatted}</span>
                                <span class="t2j-seeders">▲ ${item.seeders} seeds</span>
                                <span>▼ ${item.peers} peers</span>
                                <span>🌐 ${escapeHtml(item.indexer)}</span>
                            </div>
                        </div>
                        <div class="t2j-card-actions">
                            <button class="t2j-btn-stream stream-action-btn" tabindex="0">▶ ${i18n[currentLang].streamBtn}</button>
                        </div>
                    `;

                    const streamBtn = card.querySelector('.stream-action-btn');
                    streamBtn.addEventListener('click', async (e) => {
                        e.stopPropagation();
                        streamBtn.disabled = true;
                        streamBtn.innerText = '⏳ Додавання...';

                        try {
                            const addRes = await fetch(getApiUrl('Plugins/Torr2Jelly/Torrents'), {
                                method: 'POST',
                                headers: getAuthHeaders(),
                                body: JSON.stringify({
                                    link: item.magnetUrl || item.downloadUrl,
                                    title: item.title,
                                    category: categorySelect.value,
                                    posterUrl: item.posterUrl,
                                    tmdbId: item.tmdbId,
                                    syncNow: true
                                })
                            });

                            if (addRes.ok) {
                                streamBtn.style.background = 'rgba(16, 185, 129, 0.6)';
                                streamBtn.innerText = '✓ Додано!';
                                if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
                                    window.Dashboard.alert({ message: i18n[currentLang].addedSuccess });
                                }
                            } else {
                                streamBtn.innerText = 'Помилка';
                                streamBtn.disabled = false;
                            }
                        } catch (err) {
                            streamBtn.innerText = 'Помилка';
                            streamBtn.disabled = false;
                        }
                    });

                    searchResults.appendChild(card);
                });
            } catch (e) {
                searchLoading.style.display = 'none';
                searchResults.innerHTML = `<div style="text-align:center; padding: 20px; color: #f87171;">Помилка пошуку: ${e.message}</div>`;
            }
        }

        searchBtn.addEventListener('click', executeSearch);
        searchInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') executeSearch();
        });

        // Add direct link
        addDirectBtn.addEventListener('click', async () => {
            const link = directMagnetInput.value.trim();
            if (!link) return;

            addDirectBtn.disabled = true;
            addDirectBtn.innerText = '⏳...';

            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Torrents'), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    body: JSON.stringify({ link, syncNow: true })
                });

                if (res.ok) {
                    directMagnetInput.value = '';
                    addDirectBtn.innerText = '✓ Додано';
                    setTimeout(() => { addDirectBtn.innerText = i18n[currentLang].addLink; addDirectBtn.disabled = false; }, 2000);
                } else {
                    addDirectBtn.innerText = 'Помилка';
                    addDirectBtn.disabled = false;
                }
            } catch {
                addDirectBtn.innerText = 'Помилка';
                addDirectBtn.disabled = false;
            }
        });

        // Dashboard & Torrents List
        async function loadActiveTorrents() {
            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Torrents'), { headers: getAuthHeaders() });
                const torrents = await res.json();

                metricActiveCount.innerText = torrents.length;
                activeTorrentsList.innerHTML = '';

                if (!torrents || torrents.length === 0) {
                    activeTorrentsList.innerHTML = '<div style="text-align:center; padding: 40px; color: #94a3b8;">Немає активних роздач.</div>';
                    return;
                }

                torrents.forEach(t => {
                    const card = document.createElement('div');
                    card.className = 't2j-card';
                    card.tabIndex = 0;

                    card.innerHTML = `
                        <div class="t2j-card-info">
                            <h4 class="t2j-card-title">${escapeHtml(t.title || t.hash)}</h4>
                            <div class="t2j-card-meta">
                                <span>Статус: <b>${escapeHtml(t.stat_string || 'Active')}</b></span>
                                <span>Розмір: ${(t.torrent_size / (1024*1024*1024)).toFixed(2)} GB</span>
                                <span>Завантажено: ${(t.loaded_size / (1024*1024)).toFixed(1)} MB</span>
                            </div>
                        </div>
                        <div class="t2j-card-actions">
                            <button class="t2j-btn-stream delete-torrent-btn" style="color: #f87171; border-color: rgba(239,68,68,0.3);" tabindex="0">Видалити</button>
                        </div>
                    `;

                    card.querySelector('.delete-torrent-btn').addEventListener('click', async () => {
                        if (confirm(i18n[currentLang].deleteConfirm)) {
                            await fetch(getApiUrl(`Plugins/Torr2Jelly/Torrents/${t.hash}?deleteStrm=true`), {
                                method: 'DELETE',
                                headers: getAuthHeaders()
                            });
                            loadActiveTorrents();
                        }
                    });

                    activeTorrentsList.appendChild(card);
                });
            } catch (e) {
                activeTorrentsList.innerHTML = `<div style="color: #f87171;">Помилка завантаження торентів: ${e.message}</div>`;
            }
        }

        refreshTorrentsBtn.addEventListener('click', loadActiveTorrents);
        triggerScanBtn.addEventListener('click', async () => {
            await fetch(getApiUrl('Plugins/Torr2Jelly/Library/Scan'), { method: 'POST', headers: getAuthHeaders() });
            if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
                window.Dashboard.alert({ message: i18n[currentLang].scanTriggered });
            }
        });

        function startAutoRefresh() {
            stopAutoRefresh();
            refreshInterval = setInterval(loadActiveTorrents, 6000);
        }

        function stopAutoRefresh() {
            if (refreshInterval) {
                clearInterval(refreshInterval);
                refreshInterval = null;
            }
        }

        // Settings
        async function loadSettings() {
            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Settings'), { headers: getAuthHeaders() });
                const config = await res.json();
                cfgUseEmbedded.checked = config.UseEmbeddedTorrServer;
                cfgPort.value = config.EmbeddedTorrServerPort;
                cfgCacheSize.value = config.CacheSizeMb;
                cfgStreamUrl.value = config.TorrServerStreamUrl || '';
                cfgProwlarrUrl.value = config.ProwlarrUrl || '';
                cfgProwlarrApiKey.value = config.ProwlarrApiKey || '';
                cfgOnlyGold.checked = config.OnlyGoldTorrentsForNonAdmins;
            } catch (e) {
                console.error('Failed to load settings', e);
            }
        }

        saveSettingsBtn.addEventListener('click', async () => {
            saveSettingsBtn.disabled = true;
            saveSettingsBtn.innerText = 'Збереження...';

            const payload = {
                UseEmbeddedTorrServer: cfgUseEmbedded.checked,
                EmbeddedTorrServerPort: parseInt(cfgPort.value, 10) || 8095,
                CacheSizeMb: parseInt(cfgCacheSize.value, 10) || 256,
                TorrServerStreamUrl: cfgStreamUrl.value.trim(),
                ProwlarrUrl: cfgProwlarrUrl.value.trim(),
                ProwlarrApiKey: cfgProwlarrApiKey.value.trim(),
                OnlyGoldTorrentsForNonAdmins: cfgOnlyGold.checked
            };

            try {
                const res = await fetch(getApiUrl('Plugins/Torr2Jelly/Settings'), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    body: JSON.stringify(payload)
                });

                if (res.ok) {
                    saveSettingsBtn.innerText = '✓ Збережено!';
                    setTimeout(() => { saveSettingsBtn.innerText = 'Зберегти налаштування'; saveSettingsBtn.disabled = false; }, 2000);
                    checkStatus();
                } else {
                    saveSettingsBtn.innerText = 'Помилка';
                    saveSettingsBtn.disabled = false;
                }
            } catch {
                saveSettingsBtn.innerText = 'Помилка';
                saveSettingsBtn.disabled = false;
            }
        });

        restartDaemonBtn.addEventListener('click', async () => {
            restartDaemonBtn.disabled = true;
            restartDaemonBtn.innerText = 'Перезапуск...';
            try {
                await fetch(getApiUrl('Plugins/Torr2Jelly/Daemon/Restart'), { method: 'POST', headers: getAuthHeaders() });
                setTimeout(() => {
                    restartDaemonBtn.innerText = 'Перезапустити торент-рушій';
                    restartDaemonBtn.disabled = false;
                    checkStatus();
                }, 3000);
            } catch {
                restartDaemonBtn.disabled = false;
            }
        });

        function escapeHtml(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        }

        // Initial calls
        applyTranslations('uk');
        checkStatus();
    }

    // AMD requirejs module export for Jellyfin Web
    if (typeof define === 'function' && define.amd) {
        define(['loading'], function (loading) {
            return function (view, params) {
                view.addEventListener('viewshow', function () {
                    initController(view);
                });
            };
        });
    } else {
        document.addEventListener('DOMContentLoaded', () => {
            const page = document.getElementById('torr2jellyPage');
            if (page) initController(page);
        });
    }
})();
