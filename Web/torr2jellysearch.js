(function () {
    'use strict';

    // Re-injecting a newer copy of this script replaces the running one (no page reload needed). A copy that
    // predates the dispose hook cannot be replaced, so a plain second load of such a copy stays a no-op.
    if (typeof window._t2jSearchDispose === 'function') {
        try { window._t2jSearchDispose(); } catch (e) { }
    } else if (window._t2jSearchInitialized) {
        return;
    }
    window._t2jSearchInitialized = true;

    console.log('[Torr2Jelly] Search integration script loaded');

    var disposed = false;
    var bindToken = {}; // marks the DOM nodes this copy of the script has already hooked (a newer copy re-hooks them)
    var pollTimer = null;
    var currentQuery = '';
    var debounceTimer = null;
    var abortController = null;
    var isSearching = false;
    var searchRequestId = 0; // only the latest search request may render

    // Search page section state: the category the user picked (per query) and whether "Show all" was pressed.
    var SEARCH_PAGE_SIZE = 8;
    var _searchCategory = { query: '', value: '' };
    var _searchShowAll = false;

    // Language: explicit override (set on the Torr2Jelly admin page) wins; otherwise default to
    // Ukrainian when the Jellyfin UI language or the browser language is Ukrainian, else English.
    function getLang() {
        try {
            var stored = localStorage.getItem('torr2jelly_lang');
            if (stored) return String(stored).toLowerCase();
        } catch (e) { }

        var candidates = [];
        try {
            if (window.Globalize && typeof window.Globalize.getCurrentLocale === 'function') {
                candidates.push(window.Globalize.getCurrentLocale());
            }
        } catch (e) { }
        try { candidates.push(document.documentElement.getAttribute('lang')); } catch (e) { }
        try {
            candidates.push(navigator.language);
            if (navigator.languages && navigator.languages.length) candidates.push(navigator.languages[0]);
        } catch (e) { }

        for (var i = 0; i < candidates.length; i++) {
            var c = candidates[i];
            if (c && String(c).toLowerCase().indexOf('uk') === 0) return 'uk';
        }
        return 'en';
    }

    function getI18n() {
        var isUk = getLang() === 'uk';
        return {
            title: isUk ? 'Torr2Jelly Торенти' : 'Torr2Jelly Torrents',
            torrentsTitle: isUk ? 'Торенти' : 'Torrents',
            searching: isUk ? 'Шукаємо на торент-трекерах за запитом' : 'Searching torrent indexers for',
            notFoundTitle: isUk ? 'Немає на сервері? Знайдено в торентах' : 'Not in library? Found on torrents',
            foundTitle: isUk ? 'Також доступно на торентах (Torr2Jelly)' : 'Also available on torrents (Torr2Jelly)',
            subtitle: isUk ? 'Додайте в один клік — відео миттєво з\'явиться в Jellyfin та транслюватиметься на льоту' : 'Add in one click — streams instantly without downloading the whole file',
            releasesFound: isUk ? 'роздач знайдено' : 'releases found',
            noReleases: isUk ? 'Роздач не знайдено' : 'No releases found',
            categoryLabel: isUk ? 'Категорія / Бібліотека:' : 'Category / Library:',
            addTo: isUk ? 'Додати до:' : 'Add to:',
            goldBadge: isUk ? 'Золотий' : 'Gold',
            doubleBadge: '2x',
            filterAll: isUk ? 'Всі' : 'All',
            filterGold: isUk ? 'З бонусами' : 'With Tags',
            onlyTagged: isUk ? 'Лише з бонусами' : 'Only with tags',
            showAll: isUk ? 'Показати всі' : 'Show all',
            seedersTitle: isUk ? 'Сідери' : 'Seeders',
            addBtn: isUk ? 'Додати' : 'Add',
            adding: isUk ? 'Додаємо...' : 'Adding...',
            added: isUk ? 'Додано в медіатеку' : 'Added to library',
            addedToast: isUk ? 'успішно додано! Оновлюємо медіатеку...' : 'successfully added! Refreshing library...',
            addedPendingToast: isUk ? 'додано! Файли з\'являться за кілька хвилин, щойно TorrServer завантажить метадані.' : 'added! The files will appear shortly, once TorrServer has loaded the torrent metadata.',
            serverError: isUk ? 'Помилка сервера' : 'Server Error',
            failedResponse: isUk ? 'Невдала відповідь' : 'Request failed',
            connError: isUk ? 'Помилка з\'єднання' : 'Connection error',
            loading: isUk ? 'Завантаження...' : 'Loading...',
            closeBtn: isUk ? 'Закрити' : 'Close',
            streamsBtn: isUk ? 'Стріми' : 'Streams',
            drawerTitle: isUk ? 'Торент-версії' : 'Torrent versions',
            activeStreamsTab: isUk ? 'Версії' : 'Versions',
            searchAlternativesTab: isUk ? 'Пошук роздач' : 'Find releases',
            currentBadge: isUk ? 'Основна' : 'Primary',
            versionBadge: isUk ? 'Версія' : 'Version',
            deleteStreamConfirm: isUk ? 'Ви дійсно хочете видалити цей стрім та очистити торрент?' : 'Are you sure you want to delete this stream and remove the torrent?',
            addAsVersionBtn: isUk ? 'Додати як версію' : 'Add as version',
            replaceStreamBtn: isUk ? 'Замінити поточний' : 'Replace current',
            replaceConfirm: isUk ? 'Замінити поточну версію цією роздачею?' : 'Replace current stream with this release?',
            noStreamsFound: isUk ? 'Не знайдено активних .strm файлів' : 'No active .strm files found',
            refreshingMedia: isUk ? 'Оновлюємо сторінку...' : 'Refreshing media...',
            streamAddedSuccess: isUk ? 'Роздачу успішно додано як версію стріму!' : 'Release successfully added to item!',
            streamDeletedSuccess: isUk ? 'Стрім успішно видалено' : 'Stream deleted successfully',
            seriesBadge: isUk ? 'Серіал' : 'Series',
            silverBadge: isUk ? 'Срібна' : 'Silver',
            bronzeBadge: isUk ? 'Бронзова' : 'Bronze',
            authorBadge: isUk ? 'Авторський' : 'Author',
            exclusiveBadge: isUk ? 'Ексклюзив' : 'Exclusive',
            noStreamYet: isUk ? 'Стріму ще немає — відкрийте «Пошук роздач», щоб додати його' : 'No stream yet — open Find releases to add one',
            noStreamsHint: isUk ? 'Перейдіть на вкладку "Пошук роздач", щоб знайти та додати версію.' : 'Switch to the "Find releases" tab to find and add a version.',
            deleteStreamTitle: isUk ? 'Видалити цей стрім' : 'Delete this stream',
            searchLabel: isUk ? 'Пошук' : 'Search',
            searchPlaceholder: isUk ? 'Введіть назву для пошуку...' : 'Enter a title to search...',
            searchBusy: isUk ? 'Шукаємо...' : 'Searching...',
            findBtn: isUk ? 'Знайти' : 'Search',
            nothingFound: isUk ? 'Нічого не знайдено' : 'Nothing found',
            nothingFoundHint: isUk ? 'Спробуйте змінити пошуковий запит вище.' : 'Try changing the search query above.',
            alreadyInLibrary: isUk ? 'Вже в медіатеці' : 'Already in library',
            unauthorized: isUk ? 'Потрібно знову увійти в обліковий запис Jellyfin.' : 'Please sign in to Jellyfin again.',
            forbidden: isUk ? 'Недостатньо прав для цієї дії.' : 'You do not have permission to do this.',
            searchFailed: isUk ? 'Не вдалося виконати пошук' : 'Search failed',
            // Series pages: add season / full-series torrents to the series itself.
            seriesBtnTitle: isUk ? 'Додати торенти в цей серіал' : 'Add torrents to this series',
            seriesDialogTitle: isUk ? 'Додати до серіалу' : 'Add to series',
            seriesAddTitle: isUk ? 'Додати до цього серіалу' : 'Add to this series',
            seriesAddedTitle: isUk ? 'Додано до цього серіалу' : 'Added to this series',
            seriesAddedToast: function (count, seriesName) {
                if (isUk) {
                    var mod10 = count % 10, mod100 = count % 100;
                    var noun = (mod10 === 1 && mod100 !== 11) ? 'епізод'
                        : ((mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) ? 'епізоди' : 'епізодів');
                    return 'Додано ' + count + ' ' + noun + (seriesName ? ' до «' + seriesName + '»' : '');
                }
                return 'Added ' + count + (count === 1 ? ' episode' : ' episodes') + (seriesName ? ' to ' + seriesName : '');
            },
            seriesPendingToast: isUk ? 'Торент додано; епізоди з\'являться за кілька хвилин' : 'Torrent added; episodes will appear in a few minutes'
        };
    }

    // Inject styles. Layout glue only: every component is a native Jellyfin class (paperList, listItem, dialog,
    // emby-tab-button, toast, ...), and the few colours below come from Jellyfin's theme variables (the value
    // after the comma is Jellyfin's own default, used when a theme does not define the variable).
    var oldStyle = document.getElementById('t2j-search-styles');
    if (oldStyle && oldStyle.parentNode) oldStyle.parentNode.removeChild(oldStyle);
    var style = document.createElement('style');
    style.id = 't2j-search-styles';
    style.textContent = `
        .t2j-section { margin-bottom: 1.5em; }
        .t2j-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 0.25em 1.5em; }
        .t2j-head > .sectionTitle { margin-top: 1em; margin-bottom: 0.5em; }
        .t2j-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5em 1.5em; }
        .t2j-tools .selectContainer, .t2j-tools .checkboxContainer { margin: 0; }
        .t2j-tools .checkboxContainer, .t2j-filter-row .checkboxContainer { width: auto; margin-bottom: 0; }
        .t2j-state { padding: 0.5em 0 1em; }
        .t2j-state-error { color: var(--jf-palette-error-main, #c62828); }
        .t2j-list { margin: 0.25em 0 0; }
        .t2j-row .listItemIcon { margin: 0 0 0 0.9em; color: var(--jf-palette-text-secondary, rgba(255, 255, 255, 0.7)); }
        .t2j-body { min-width: 0; }
        .t2j-title, .t2j-title-text { color: var(--jf-palette-text-primary, #fff); }
        .t2j-title { white-space: normal; word-break: break-word; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; }
        .t2j-title-row { display: flex; align-items: center; gap: 0.5em; }
        .t2j-title-text { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .t2j-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 0.15em 0.5em; }
        .t2j-meta .mediaInfoText, .t2j-title-row .mediaInfoText { margin: 0; }
        .t2j-title-row .mediaInfoText { flex-shrink: 0; }
        .t2j-seeds { display: inline-flex; align-items: center; }
        .t2j-seeds .material-icons { font-size: 1.1em; }
        .t2j-chip-primary { color: var(--jf-palette-primary-main, #00a4dc); border-color: var(--jf-palette-primary-main, #00a4dc); }
        .t2j-actions { display: flex; align-items: center; flex-shrink: 0; margin-right: 0.4em; }
        .t2j-act-done { color: var(--jf-palette-success-main, #66bb6a); }
        .t2j-act-error { color: var(--jf-palette-error-main, #c62828); }
        .t2j-more { margin-left: 0; }
        .t2j-toast .t2j-toast-icon { margin-right: 0.5em; vertical-align: middle; }
        .t2j-toast .t2j-toast-icon.t2j-ok { color: var(--jf-palette-success-main, #66bb6a); }
        .t2j-toast .t2j-toast-icon.t2j-err { color: var(--jf-palette-error-main, #c62828); }
        .t2j-dialog { width: 46em; max-width: 94vw; max-height: 86vh; z-index: 999999; background: var(--jf-palette-background-default, #101010); color: var(--jf-palette-text-primary, #fff); }
        .t2j-dialog .formDialogHeaderTitle { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .t2j-dialog-subtitle { font-weight: 400; }
        .t2j-tabs { flex-shrink: 0; padding: 0 0.5em; border-bottom: 1px solid var(--jf-palette-divider, rgba(255, 255, 255, 0.12)); }
        .t2j-tabs .emby-tabs-slider { display: flex; }
        .t2j-tabs .emby-tab-button { padding: 0.9em 1.1em; border-bottom: 0.14em solid transparent; }
        .t2j-tabs .emby-tab-button.emby-tab-button-active { border-bottom-color: var(--jf-palette-primary-main, #00a4dc); }
        .t2j-dialog-body { flex: 1 1 auto; min-height: 12em; overflow-y: auto; padding: 0.5em 1em 1em; }
        .t2j-series-bar { flex-shrink: 0; padding: 0.5em 1em 0; }
        .t2j-find-bar { margin-bottom: 0; }
        .t2j-find-row { display: flex; align-items: stretch; gap: 0.5em; }
        .t2j-find-row .emby-input { flex: 1 1 auto; min-width: 0; }
        .t2j-find-row .t2j-find-btn { flex: 0 0 auto; margin: 0; padding-top: 0; padding-bottom: 0; }
        .t2j-filter-row { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 0.25em 1em; margin: 0.75em 0 0.25em; }
        @media (max-width: 50em) {
            .t2j-dialog { width: 100%; max-width: none; height: 100%; max-height: none; border-radius: 0; }
        }
    `;
    document.head.appendChild(style);

    // Icons: Jellyfin's bundled "Material Icons" font. 12.x maps an icon to a class name (<span class="material-icons add">);
    // other builds use the ligature text (<span class="material-icons">add</span>). Probe once per icon which form the
    // running client understands, so an icon is never blank and never drawn twice.
    var _iconClassMode = {};
    function iconUsesClass(name) {
        if (Object.prototype.hasOwnProperty.call(_iconClassMode, name)) return _iconClassMode[name];
        if (!document.body || typeof window.getComputedStyle !== 'function') return false;
        var usesClass = false;
        try {
            var probe = document.createElement('span');
            probe.className = 'material-icons ' + name;
            probe.setAttribute('aria-hidden', 'true');
            probe.style.cssText = 'position:absolute;left:-9999px;visibility:hidden;pointer-events:none;';
            document.body.appendChild(probe);
            var content = window.getComputedStyle(probe, '::before').content;
            document.body.removeChild(probe);
            usesClass = !!content && content !== 'none' && content !== 'normal' && content.length > 2;
        } catch (e) { usesClass = false; }
        _iconClassMode[name] = usesClass;
        return usesClass;
    }

    function iconHtml(name, extraClass) {
        var cls = 'material-icons' + (extraClass ? ' ' + extraClass : '');
        if (iconUsesClass(name)) {
            return '<span class="' + cls + ' ' + name + '" aria-hidden="true"></span>';
        }
        return '<span class="' + cls + '" aria-hidden="true">' + name + '</span>';
    }

    // Native Jellyfin toast: <div class="toastContainer"><div class="toast">...</div></div>.
    function getToastContainer() {
        var container = document.querySelector('.toastContainer');
        if (!container) {
            container = document.createElement('div');
            container.className = 'toastContainer';
            document.body.appendChild(container);
        }
        return container;
    }

    function showToast(message, isSuccess) {
        var existing = document.querySelector('.toast.t2j-toast');
        if (existing && existing.parentNode) existing.parentNode.removeChild(existing);

        var text = String(message == null ? '' : message);
        var toast = document.createElement('div');
        toast.className = 'toast t2j-toast';
        toast.setAttribute('role', isSuccess ? 'status' : 'alert');
        toast.innerHTML = iconHtml(isSuccess ? 'check_circle' : 'error_outline', 't2j-toast-icon ' + (isSuccess ? 't2j-ok' : 't2j-err'));
        toast.appendChild(document.createTextNode(text));
        getToastContainer().appendChild(toast);
        void toast.offsetWidth; // reflow so the slide-in transition runs
        toast.classList.add('toastVisible');

        setTimeout(function () {
            toast.classList.add('toastHide');
            setTimeout(function () {
                if (toast.parentNode) toast.parentNode.removeChild(toast);
            }, 350);
        }, text.length > 80 ? 6000 : 3500);
    }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function getApiUrl(endpoint) {
        var ep = String(endpoint || '').replace(/^\/+/, '');
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(ep);
        }
        // Base-URL-safe fallbacks: never assume the server lives at the host root.
        if (window.ApiClient && typeof window.ApiClient.serverAddress === 'function') {
            var base = String(window.ApiClient.serverAddress() || '').replace(/\/+$/, '');
            if (base) return base + '/' + ep;
        }
        // The web client is served from {BaseUrl}/web/index.html, so ../ lands on {BaseUrl}/.
        return '../' + ep;
    }

    // Prefer a magnet that carries trackers, or the only link there is. A tracker-less magnet (built from the
    // info-hash alone) must not beat the indexer's .torrent link: a private tracker's torrent never resolves
    // without the tracker announce URL that the .torrent file contains.
    function pickLink(item) {
        if (!item) return '';
        var magnet = item.MagnetUrl || '';
        var download = item.DownloadUrl || '';
        if (magnet && (!download || /[?&]tr(?:\.\d+)?=/i.test(magnet))) return magnet;
        return download || magnet;
    }

    // POST Torrents answers 200 with Success=false when the torrent was added but TorrServer has not loaded its
    // file list yet: the .strm files follow with the next sync. Every other failure is a non-2xx response.
    async function isAddedPending(res) {
        try {
            var data = await res.json();
            return !!data && (data.Success === false || data.success === false);
        } catch (e) {
            return false;
        }
    }

    // Series detection with word boundaries so tokens like "DTS5.1" or "1080x720" are not mistaken
    // for season/episode markers. (\b is ASCII-only in JS, so the Cyrillic alternatives stay unbounded.)
    var SHOW_TITLE_REGEX = /(?:\bS\d{1,2}(?:E\d+)?\b|\bseasons?\b(?![\s._-]+of\b)|сезон|\b\d{1,2}x\d{1,2}\b|серій|серії|серії|\bep\d{1,3}\b)/i;
    function isShowTitle(title) {
        return SHOW_TITLE_REGEX.test(title || '');
    }

    // Picture quality labels read from a release title: resolution first, then HDR / Dolby Vision.
    function parseQuality(title) {
        var t = String(title || '');
        var out = [];
        if (/\b(?:2160p|4k)\b/i.test(t)) out.push('4K');
        else if (/\b1080[pi]\b/i.test(t)) out.push('1080p');
        else if (/\b720p\b/i.test(t)) out.push('720p');
        else if (/\b(?:576|480)[pi]\b/i.test(t)) out.push('SD');
        else if (/\buhd\b/i.test(t)) out.push('4K');
        if (/\b(?:dolby[\s._-]*vision|dovi)\b|\bDV\b/i.test(t)) out.push('DV');
        if (/\bHDR(?!ip)/i.test(t)) out.push('HDR');
        return out;
    }

    // Friendly message for auth failures; null for everything else.
    function authErrorMessage(status, i18n) {
        if (status === 401) return i18n.unauthorized;
        if (status === 403) return i18n.forbidden;
        return null;
    }

    // Best human-readable error for a failed response (friendly for 401/403, server "error" field otherwise).
    async function readErrorMessage(res, i18n) {
        var friendly = authErrorMessage(res.status, i18n);
        if (friendly) return friendly;
        var errData = await res.json().catch(function () { return {}; });
        return (errData && errData.error) ? String(errData.error) : (i18n.failedResponse + ' (' + res.status + ')');
    }

    // Cached "is the current user a Jellyfin administrator" lookup (per user id). Failures are retried
    // at most every 30 s so the 400 ms page watcher can't hammer the server.
    var _adminState = { userId: null, value: null, pending: false, failedAt: 0 };
    function getCurrentUserIdSafe() {
        try {
            if (window.ApiClient && typeof window.ApiClient.getCurrentUserId === 'function') {
                return window.ApiClient.getCurrentUserId() || null;
            }
        } catch (e) { }
        return null;
    }

    // Returns true / false when known, null while the lookup is in flight.
    function getIsAdminCached() {
        var userId = getCurrentUserIdSafe();
        if (!userId || !window.ApiClient || typeof window.ApiClient.getCurrentUser !== 'function') return false;

        if (_adminState.userId !== userId) {
            _adminState = { userId: userId, value: null, pending: false, failedAt: 0 };
        }
        if (_adminState.value !== null) return _adminState.value;
        if (_adminState.pending) return null;
        if (_adminState.failedAt && (Date.now() - _adminState.failedAt) < 30000) return false;

        _adminState.pending = true;
        var requestedFor = userId;
        var finish = function (value, failed) {
            if (_adminState.userId !== requestedFor) return;
            _adminState.pending = false;
            if (failed) {
                _adminState.failedAt = Date.now();
            } else {
                _adminState.value = value;
            }
        };
        try {
            Promise.resolve(window.ApiClient.getCurrentUser()).then(function (user) {
                finish(!!(user && user.Policy && user.Policy.IsAdministrator), false);
            }).catch(function () { finish(false, true); });
        } catch (e) {
            finish(false, true);
        }
        return null;
    }

    // Cached item type lookup so the item page buttons only show on Movie and Series items.
    var _itemTypeCache = {}; // itemId -> { type: string|null, pending: bool, failedAt: number }
    function getItemTypeCached(itemId) {
        var entry = _itemTypeCache[itemId];
        if (entry && entry.type) return entry.type;
        if (entry && entry.pending) return null;
        if (entry && entry.failedAt && (Date.now() - entry.failedAt) < 30000) return '';
        if (!window.ApiClient || typeof window.ApiClient.getItem !== 'function') return '';
        var userId = getCurrentUserIdSafe();
        if (!userId) return '';

        entry = _itemTypeCache[itemId] = { type: null, pending: true, failedAt: 0 };
        try {
            Promise.resolve(window.ApiClient.getItem(userId, itemId)).then(function (item) {
                entry.pending = false;
                if (item && item.Type) {
                    entry.type = String(item.Type);
                } else {
                    entry.failedAt = Date.now();
                }
            }).catch(function () {
                entry.pending = false;
                entry.failedAt = Date.now();
            });
        } catch (e) {
            entry.pending = false;
            entry.failedAt = Date.now();
        }
        return null;
    }

    // 'movie' = versions button, 'series' = add-to-series button (admins only), false = never for this
    // item/user, null = still resolving.
    function getStreamsButtonKind(itemId) {
        var isAdmin = getIsAdminCached();
        if (isAdmin === false) return false;
        if (isAdmin === null) return null;
        var type = getItemTypeCached(itemId);
        if (type === null) return null;
        if (type === 'Movie') return 'movie';
        if (type === 'Series') return 'series';
        return false;
    }

    function getAuthHeaders() {
        var headers = { 'Accept': 'application/json', 'Content-Type': 'application/json' };
        var client = window.ApiClient;
        if (!client) {
            return headers;
        }
        // Prefer the full "MediaBrowser Client=..., Token=..." header ApiClient builds itself.
        if (typeof client.setRequestHeaders === 'function') {
            try { client.setRequestHeaders(headers); } catch (e) { }
        }
        var token = typeof client.accessToken === 'function' ? client.accessToken() : null;
        if (token) {
            if (!headers['Authorization']) {
                headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
            }
            headers['X-Emby-Token'] = token;
        }
        return headers;
    }

    // ==========================================
    // Shared release row (search page section + "Find releases" tab)
    // ==========================================

    // One neutral outlined chip, the same look as the item page's mediaInfoItem labels.
    function chipHtml(text, primary) {
        return '<div class="mediaInfoItem mediaInfoText' + (primary ? ' t2j-chip-primary' : '') + '">' + escapeHtml(text) + '</div>';
    }

    // Tag chips of a release: freeleech/gold in the accent colour, the other known tags neutral.
    function buildTagChipsHtml(item, i18n) {
        var isGold = !!(item.IsGold || item.isGold);
        var rawTags = (item.Tags || item.tags || []).slice();
        var hasFreeleech = isGold || rawTags.some(function (tg) { return /freeleech|gold/i.test(tg); });
        var hasHalfleech = !hasFreeleech && rawTags.some(function (tg) { return /halfleech|silver|50%/i.test(tg); });
        var hasBronze = !hasFreeleech && !hasHalfleech && rawTags.some(function (tg) { return /bronze/i.test(tg); });

        rawTags = rawTags.filter(function (tg) {
            return !/^(freeleech|gold|halfleech|silver|bronze)$/i.test(String(tg || '').trim());
        });

        if (hasFreeleech) {
            rawTags.unshift('freeleech');
        } else if (hasHalfleech) {
            rawTags.unshift('halfleech');
        } else if (hasBronze) {
            rawTags.unshift('bronze');
        }

        var html = '';
        rawTags.forEach(function (tg) {
            var ltg = String(tg || '').toLowerCase();
            if (ltg === 'freeleech' || ltg === 'gold') {
                html += chipHtml(i18n.goldBadge, true);
            } else if (ltg === 'halfleech' || ltg === 'silver') {
                html += chipHtml(i18n.silverBadge, false);
            } else if (ltg === 'bronze') {
                html += chipHtml(i18n.bronzeBadge, false);
            } else if (ltg === 'internal' || ltg === 'author' || ltg.indexOf('автор') !== -1) {
                html += chipHtml(i18n.authorBadge, false);
            } else if (ltg === 'exclusive' || ltg.indexOf('ексклюзив') !== -1) {
                html += chipHtml(i18n.exclusiveBadge, false);
            } else if (ltg === 'doubleupload' || ltg === '2xupload') {
                html += chipHtml(i18n.doubleBadge, false);
            }
        });
        return html;
    }

    // Trailing icon button of a row. data-icon is the idle icon; data-act says what a click does.
    function actionButtonHtml(act, idx, iconName, title) {
        return '<button is="paper-icon-button-light" type="button" class="listItemButton paper-icon-button-light t2j-act"'
            + ' data-act="' + act + '" data-idx="' + idx + '" data-icon="' + iconName + '" data-title0="' + escapeHtml(title) + '"'
            + ' title="' + escapeHtml(title) + '" aria-label="' + escapeHtml(title) + '">'
            + iconHtml(iconName) + '</button>';
    }

    // idle | busy | done | error. Busy swaps the icon for an hourglass and disables the button; done keeps it
    // enabled (so it keeps its focus) but clicks are ignored; error stays clickable so the add can be retried.
    function setActState(btn, state, title) {
        if (!btn) return;
        var iconName = btn.getAttribute('data-icon') || 'add';
        if (state === 'busy') iconName = 'hourglass_empty';
        else if (state === 'done') iconName = 'check';
        else if (state === 'error') iconName = 'error_outline';
        btn.setAttribute('data-state', state);
        btn.disabled = (state === 'busy');
        btn.classList.remove('t2j-act-done');
        btn.classList.remove('t2j-act-error');
        if (state === 'done') btn.classList.add('t2j-act-done');
        if (state === 'error') btn.classList.add('t2j-act-error');
        var label = title || btn.getAttribute('data-title0') || '';
        btn.setAttribute('title', label);
        btn.setAttribute('aria-label', label);
        btn.innerHTML = iconHtml(iconName);
    }

    function isActLocked(btn) {
        var state = btn.getAttribute('data-state');
        return state === 'busy' || state === 'done';
    }

    function closestAct(el, root) {
        while (el && el !== root) {
            if (el.nodeType === 1 && el.hasAttribute('data-act')) return el;
            el = el.parentNode;
        }
        return null;
    }

    // A release as a native list row: leading movie/tv icon, title, "indexer · size · seeders" + chips, trailing actions.
    // leadIconOverride fixes the icon where the context already says what the release is (the series dialog).
    function releaseRowHtml(item, idx, i18n, actionsHtml, extraChipsHtml, leadIconOverride) {
        var title = item.Title || '';
        var size = item.SizeFormatted || '';
        var seeds = parseInt(item.Seeders, 10) || 0;
        var tracker = item.Indexer || 'Torrent';
        var leadIcon = leadIconOverride || (isShowTitle(title) ? 'tv' : 'movie');

        var quality = '';
        parseQuality(title).forEach(function (q) { quality += chipHtml(q, false); });

        var sep = '<span aria-hidden="true">&middot;</span>';
        var meta = '<span>' + escapeHtml(tracker) + '</span>';
        if (size) meta += sep + '<span>' + escapeHtml(size) + '</span>';
        meta += sep + '<span class="t2j-seeds" title="' + escapeHtml(i18n.seedersTitle) + '">' + iconHtml('arrow_upward') + seeds + '</span>';

        return '<div class="listItem listItem-border t2j-row" data-action="none">'
            + iconHtml(leadIcon, 'listItemIcon listItemIcon-transparent')
            + '<div class="listItemBody t2j-body">'
            + '<div class="listItemBodyText t2j-title" title="' + escapeHtml(title) + '">' + escapeHtml(title) + '</div>'
            + '<div class="listItemBodyText secondary t2j-meta">' + meta + quality + buildTagChipsHtml(item, i18n) + (extraChipsHtml || '') + '</div>'
            + '</div>'
            + '<div class="t2j-actions">' + actionsHtml + '</div>'
            + '</div>';
    }

    // ==========================================
    // Search page section
    // ==========================================

    // Cancels any in-flight search and invalidates its request id so a late response can never render.
    function cancelPendingSearch() {
        clearTimeout(debounceTimer);
        searchRequestId++;
        if (abortController) {
            try { abortController.abort(); } catch (e) { }
            abortController = null;
        }
        isSearching = false;
    }

    // Forget everything about the previous search (used when the user leaves the search page).
    function resetSearchState() {
        cancelPendingSearch();
        currentQuery = '';
        window._t2jLastQuery = '';
        window._t2jLastResults = [];
        _searchCategory = { query: '', value: '' };
        _searchShowAll = false;
        removeResultsContainer();
    }

    async function performSearch(query) {
        query = (query || '').trim();
        if (query.length < 2) {
            cancelPendingSearch();
            removeResultsContainer();
            return;
        }

        if (abortController) {
            try { abortController.abort(); } catch (e) { }
        }
        var myAbort = new AbortController();
        abortController = myAbort;
        var myRequestId = ++searchRequestId;

        isSearching = true;
        renderLoadingIndicator(query);

        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Search?query=' + encodeURIComponent(query)), {
                headers: getAuthHeaders(),
                signal: myAbort.signal
            });
            if (myRequestId !== searchRequestId) return;

            if (!res.ok) {
                var i18nFail = getI18n();
                var authMsg = authErrorMessage(res.status, i18nFail);
                if (authMsg) {
                    removeResultsContainer();
                    showToast(authMsg, false);
                } else {
                    var failMsg = await readErrorMessage(res, i18nFail);
                    if (myRequestId !== searchRequestId) return;
                    renderSectionMessage(i18nFail.searchFailed + ': ' + failMsg, true);
                }
                return;
            }

            var results = await res.json();
            if (myRequestId !== searchRequestId) return;
            await renderSearchResults(query, results || [], myRequestId);
        } catch (err) {
            if (err.name !== 'AbortError' && myRequestId === searchRequestId) {
                console.error('[Torr2Jelly] Search error:', err);
                renderSectionMessage(getI18n().connError + ': ' + (err && err.message ? err.message : err), true);
            }
        } finally {
            // A stale request finishing late must not clear the "searching" flag of the newer one.
            if (myRequestId === searchRequestId) {
                isSearching = false;
            }
        }
    }

    function removeResultsContainer() {
        var el = document.getElementById('t2j-search-section');
        if (el) el.remove();
    }

    function getSearchTargetContainer() {
        var searchPage = document.getElementById('searchPage') || document.querySelector('.searchPage');
        if (!searchPage) return null;

        var noItemsMsg = searchPage.querySelector('.noItemsMessage');
        if (noItemsMsg) {
            return { parent: noItemsMsg.parentNode, before: noItemsMsg.nextSibling, mode: 'empty' };
        }

        var searchResults = searchPage.querySelector('.searchResults');
        if (searchResults) {
            return { parent: searchResults, before: null, mode: 'results' };
        }

        return { parent: searchPage, before: null, mode: 'fallback' };
    }

    // The section is a native Jellyfin section: <div class="verticalSection"> with an h2.sectionTitle.
    function ensureResultsContainer(target) {
        var container = document.getElementById('t2j-search-section');
        if (!container) {
            container = document.createElement('div');
            container.id = 't2j-search-section';
            container.className = 'verticalSection t2j-section';
            if (target.before) {
                target.parent.insertBefore(container, target.before);
            } else {
                target.parent.appendChild(container);
            }
        }
        return container;
    }

    function sectionHeadHtml(i18n, toolsHtml) {
        return '<div class="t2j-head padded-left padded-right">'
            + '<h2 class="sectionTitle sectionTitle-cards">' + escapeHtml(i18n.torrentsTitle) + '</h2>'
            + (toolsHtml ? '<div class="t2j-tools">' + toolsHtml + '</div>' : '')
            + '</div>';
    }

    function stateLineHtml(text, isError) {
        return '<div class="fieldDescription t2j-state' + (isError ? ' t2j-state-error' : '') + '" role="status">' + escapeHtml(text) + '</div>';
    }

    // Loading, empty and error states: the section title plus one calm line of text.
    function renderSectionMessage(text, isError) {
        var target = getSearchTargetContainer();
        if (!target) return;
        var container = ensureResultsContainer(target);
        container.innerHTML = sectionHeadHtml(getI18n(), '')
            + '<div class="padded-left padded-right">' + stateLineHtml(text, isError) + '</div>';
    }

    function renderLoadingIndicator(query) {
        var i18n = getI18n();
        renderSectionMessage(i18n.searching + ' "' + query + '"...', false);
    }

    var _t2jCachedLibs = null;
    async function getAvailableLibraries() {
        if (_t2jCachedLibs && _t2jCachedLibs.length > 0) return _t2jCachedLibs;
        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Libraries'), { headers: getAuthHeaders() });
            if (res.ok) {
                var libs = await res.json();
                if (Array.isArray(libs) && libs.length > 0) {
                    _t2jCachedLibs = libs;
                    return _t2jCachedLibs;
                }
            }
        } catch (e) {}
        return [
            { Name: 'Movies', Type: 'movie' },
            { Name: 'Shows', Type: 'show' },
            { Name: 'Cartoons', Type: 'movie' }
        ];
    }

    // The library to preselect for movies or shows: one whose type AND name say so wins, then either one
    // (so "Movies" beats a movie-type "Cartoons" that merely sorts first).
    function pickLibrary(libs, wantShow) {
        function typeMatches(l) {
            var t = (l.Type || '').toLowerCase();
            return wantShow ? (t === 'show' || t === 'tvshows') : t === 'movie';
        }
        function nameMatches(l) {
            var n = (l.Name || '').toLowerCase();
            return wantShow ? (n.indexOf('show') !== -1 || n.indexOf('серіал') !== -1) : (n.indexOf('movie') !== -1 || n.indexOf('фільм') !== -1);
        }
        return libs.find(function (l) { return typeMatches(l) && nameMatches(l); })
            || libs.find(function (l) { return typeMatches(l) || nameMatches(l); });
    }

    async function renderSearchResults(query, results, requestId) {
        var i18n = getI18n();
        if (requestId !== undefined && requestId !== searchRequestId) return;
        results = results || [];

        var target = getSearchTargetContainer();
        if (results.length === 0) {
            if (target && target.mode === 'empty') {
                renderSectionMessage(i18n.noReleases, false);
            } else {
                removeResultsContainer();
            }
            return;
        }

        if (!target) return;

        var container = ensureResultsContainer(target);
        // Jellyfin appends its own result sections as they load: keep this one below all of them.
        if (target.mode === 'results' && container.parentNode === target.parent && container.nextElementSibling) {
            target.parent.appendChild(container);
        }

        window._t2jLastQuery = query;
        window._t2jLastResults = results;
        if (!window._t2jCurrentFilter) window._t2jCurrentFilter = 'all';

        var libs = await getAvailableLibraries();
        // A newer search (or leaving the page) may have happened while the library list was loading.
        if (requestId !== undefined && requestId !== searchRequestId) return;

        var showLib = pickLibrary(libs, true);
        var movieLib = pickLibrary(libs, false);

        // One category for the whole section. Its default follows the majority of the results (movies win a tie),
        // and a choice the user made sticks for the rest of this query.
        if (_searchCategory.query !== query) {
            _searchCategory = { query: query, value: '' };
            _searchShowAll = false;
        }
        var showCount = 0;
        results.forEach(function (r) { if (isShowTitle(r.Title)) showCount++; });
        var autoIsShow = showCount > (results.length - showCount);
        var autoLibName = autoIsShow ? (showLib ? showLib.Name : 'Shows') : (movieLib ? movieLib.Name : 'Movies');
        var targetLibName = _searchCategory.value || autoLibName;

        var catOptionsHtml = '';
        var matched = false;
        libs.forEach(function (lib) {
            var libName = typeof lib === 'string' ? lib : (lib.Name || '');
            var isSelected = (libName === targetLibName);
            if (isSelected) matched = true;
            catOptionsHtml += '<option value="' + escapeHtml(libName) + '"' + (isSelected ? ' selected' : '') + '>' + escapeHtml(libName) + '</option>';
        });
        if (!matched && libs.length > 0) {
            catOptionsHtml = '<option value="' + escapeHtml(targetLibName) + '" selected>' + escapeHtml(targetLibName) + '</option>' + catOptionsHtml;
        }

        var taggedResults = results.filter(function (r) {
            return r.IsGold || (r.Tags && r.Tags.length > 0);
        });
        var filterOn = (window._t2jCurrentFilter === 'gold');
        var list = filterOn ? taggedResults : results;
        var visibleCount = _searchShowAll ? list.length : Math.min(list.length, SEARCH_PAGE_SIZE);

        var toolsHtml =
            '<div class="selectContainer selectContainer-inline t2j-cat">'
            + '<select is="emby-select" id="t2j-cat-select" class="emby-select-withcolor" label="' + escapeHtml(i18n.addTo) + '" aria-label="' + escapeHtml(i18n.addTo) + '">'
            + catOptionsHtml + '</select></div>'
            + '<label class="checkboxContainer">'
            + '<input is="emby-checkbox" type="checkbox" id="t2j-filter-tagged"' + (filterOn ? ' checked' : '') + '>'
            + '<span>' + escapeHtml(i18n.onlyTagged) + ' (' + taggedResults.length + ')</span></label>';

        var rowsHtml = '';
        for (var i = 0; i < visibleCount; i++) {
            rowsHtml += releaseRowHtml(list[i], i, i18n, actionButtonHtml('add', i, 'add', i18n.addBtn), '');
        }

        var bodyHtml = '<div class="padded-left padded-right">';
        if (list.length === 0) {
            bodyHtml += stateLineHtml(i18n.noReleases, false);
        } else {
            bodyHtml += '<div class="paperList t2j-list">' + rowsHtml + '</div>';
            if (list.length > visibleCount) {
                bodyHtml += '<button is="emby-button" type="button" class="button-flat emby-button t2j-more" id="t2j-show-all">'
                    + '<span>' + escapeHtml(i18n.showAll) + ' (' + list.length + ')</span></button>';
            }
        }
        bodyHtml += '</div>';

        // A re-render (filter, "Show all") must not drop keyboard focus from the control that triggered it.
        var focusedId = '';
        if (document.activeElement && container.contains(document.activeElement)) {
            focusedId = document.activeElement.id || '';
        }

        container.innerHTML = sectionHeadHtml(i18n, toolsHtml) + bodyHtml;

        if (focusedId) {
            var refocus = document.getElementById(focusedId);
            if (refocus && typeof refocus.focus === 'function') refocus.focus();
        }

        var catSelect = container.querySelector('#t2j-cat-select');
        if (catSelect) {
            catSelect.addEventListener('change', function () {
                _searchCategory = { query: query, value: catSelect.value || '' };
            });
        }

        var filterBox = container.querySelector('#t2j-filter-tagged');
        if (filterBox) {
            filterBox.addEventListener('change', function () {
                window._t2jSetFilter(filterBox.checked ? 'gold' : 'all');
            });
        }

        var moreBtn = container.querySelector('#t2j-show-all');
        if (moreBtn) {
            moreBtn.addEventListener('click', function () {
                _searchShowAll = true;
                renderSearchResults(query, results, searchRequestId).then(function () {
                    var firstNew = document.querySelector('#t2j-search-section .t2j-act[data-idx="' + SEARCH_PAGE_SIZE + '"]');
                    if (firstNew && typeof firstNew.focus === 'function') firstNew.focus();
                });
            });
        }

        var listEl = container.querySelector('.t2j-list');
        if (listEl) {
            listEl.addEventListener('click', function (e) {
                var btn = closestAct(e.target, listEl);
                if (!btn || btn.getAttribute('data-act') !== 'add') return;
                var item = list[parseInt(btn.getAttribute('data-idx'), 10)];
                if (item) addSearchResult(btn, item, i18n);
            });
        }
    }

    // Adds one search result to the library in the section's selected category.
    async function addSearchResult(btn, item, i18n) {
        var link = pickLink(item);
        var itemTitle = item.Title || '';
        var posterUrl = item.PosterUrl || '';
        var tmdbId = parseInt(item.TmdbId, 10) || 0;

        if (!link) return;
        if (isActLocked(btn)) return;

        // Pick selected category from dropdown, fallback to auto-detection
        var catSelect = document.getElementById('t2j-cat-select');
        var isShow = isShowTitle(itemTitle);
        var category = (catSelect && catSelect.value) ? catSelect.value : (isShow ? 'Shows' : 'Movies');

        setActState(btn, 'busy', i18n.adding);

        try {
            var payload = {
                Link: link,
                Title: itemTitle,
                Category: category,
                PosterUrl: posterUrl,
                TmdbId: tmdbId > 0 ? tmdbId : null,
                SyncNow: true
            };

            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Torrents'), {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify(payload)
            });

            if (res.ok) {
                var pending = await isAddedPending(res);
                setActState(btn, 'done', i18n.added);
                showToast('"' + itemTitle + '" ' + (pending ? i18n.addedPendingToast : i18n.addedToast), true);

                // No separate library scan here: the add request above uses SyncNow, which already
                // writes the .strm files and notifies Jellyfin about the changed path.

                // After 2.5 seconds, refresh page or re-trigger search
                setTimeout(function () {
                    if (window.location.hash.indexOf('/search') !== -1) {
                        var searchInput = document.querySelector('#searchPage input.emby-input');
                        if (searchInput) {
                            searchInput.dispatchEvent(new Event('input', { bubbles: true }));
                        }
                    }
                }, 2500);
            } else {
                var errMsg = await readErrorMessage(res, i18n);
                setActState(btn, 'error', i18n.serverError);
                showToast(authErrorMessage(res.status, i18n) ? errMsg : (i18n.failedResponse + ': ' + errMsg), false);
            }
        } catch (e) {
            setActState(btn, 'error', i18n.serverError);
            showToast(i18n.connError + ': ' + e.message, false);
        }
    }

    window._t2jSetFilter = function (filterMode) {
        window._t2jCurrentFilter = filterMode;
        _searchShowAll = false;
        if (window._t2jLastQuery && window._t2jLastResults) {
            renderSearchResults(window._t2jLastQuery, window._t2jLastResults, searchRequestId);
        }
    };

    // Observer and query sync
    var _onSearchPage = false;
    function checkSearchPage() {
        var searchPage = document.getElementById('searchPage') || document.querySelector('.searchPage');
        var onSearchRoute = (window.location.hash || '').indexOf('/search') !== -1;
        if (!searchPage || searchPage.classList.contains('hide') || !onSearchRoute) {
            // Left the search page (SPA navigation): drop the old query, in-flight request and results.
            if (_onSearchPage) {
                _onSearchPage = false;
                resetSearchState();
            } else {
                removeResultsContainer();
            }
            return;
        }
        _onSearchPage = true;

        var searchInput = searchPage.querySelector('input.emby-input') ||
                          searchPage.querySelector('input[type="search"]') ||
                          searchPage.querySelector('input');

        if (!searchInput) return;

        if (searchInput._t2jBound !== bindToken) {
            searchInput._t2jBound = bindToken;
            searchInput.addEventListener('input', function () {
                if (disposed) return;
                searchInput._t2jTyped = true;
                var val = (searchInput.value || '').trim();
                if (val === currentQuery) return;
                currentQuery = val;
                if (val.length < 2) {
                    // Box cleared (or too short): stop the running search and drop its results.
                    cancelPendingSearch();
                    removeResultsContainer();
                    return;
                }
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(function () {
                    performSearch(currentQuery);
                }, 450);
            });
        }

        var hash = window.location.hash || '';
        var queryMatch = hash.match(/[?&]query=([^&]+)/);
        var urlQuery = '';
        if (queryMatch) {
            try { urlQuery = decodeURIComponent(queryMatch[1]).trim(); } catch (e) { urlQuery = ''; }
        }
        // Once the user has typed in the box, the box is the source of truth; the URL query is only a
        // starting value (otherwise clearing the box would resurrect the old query from the URL).
        var rawEffective = searchInput._t2jTyped ? (searchInput.value || '') : (searchInput.value || urlQuery || '');
        var effectiveQuery = rawEffective.trim();

        if (effectiveQuery !== currentQuery) {
            if (effectiveQuery.length < 2) {
                // Cleared programmatically (no input event fired)
                cancelPendingSearch();
                currentQuery = effectiveQuery;
                removeResultsContainer();
            } else if (!isSearching) {
                currentQuery = effectiveQuery;
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(function () {
                    performSearch(currentQuery);
                }, 450);
            }
        }
    }


    // ==========================================
    // Item page dialogs (native Jellyfin form dialog): torrent versions of a movie, add-to-series for a series
    // ==========================================
    var _drawerData = null;      // movie dialog: { ExistingStreams, Alternatives, Title, Year, ... }; null lists = not loaded yet
    var _seriesData = null;      // series dialog state (see openSeriesDialog); only one of the two is set while a dialog is open
    var _activeDrawerTab = 'streams'; // 'streams' or 'search'
    var _drawerSearchFilter = 'all';
    var _drawerToken = 0;        // bumped every time a dialog is opened or closed
    var _drawerItemId = null;    // item the current dialog belongs to
    var _drawerRoot = null;      // the .dialogContainer element while open
    var _drawerReturnFocus = null;
    var _drawerBodyLocked = false;

    function lockBodyScroll() {
        if (document.body && !document.body.classList.contains('bodyWithPopupOpen')) {
            document.body.classList.add('bodyWithPopupOpen');
            _drawerBodyLocked = true;
        }
    }

    function unlockBodyScroll() {
        if (!_drawerBodyLocked) return;
        _drawerBodyLocked = false;
        // Another Jellyfin dialog may still be open: it owns the lock then.
        if (document.body && !document.querySelector('.dialogContainer')) {
            document.body.classList.remove('bodyWithPopupOpen');
        }
    }

    function getDialogFocusables(root) {
        var nodes = root.querySelectorAll('button, [href], input, select, textarea, [tabindex]');
        var out = [];
        for (var i = 0; i < nodes.length; i++) {
            var n = nodes[i];
            if (n.disabled || n.getAttribute('tabindex') === '-1' || n.type === 'hidden') continue;
            if (n.offsetParent === null) continue;
            out.push(n);
        }
        return out;
    }

    // Esc / the TV "back" keys close the dialog; Tab stays inside it.
    function onDrawerKeyDown(e) {
        if (!_drawerRoot) return;
        var key = e.key;
        var code = e.keyCode;
        if (key === 'Escape' || key === 'Esc' || code === 27 || code === 461 || code === 10009 || key === 'GoBack' || key === 'BrowserBack') {
            e.preventDefault();
            e.stopPropagation();
            closeStreamsDialog();
            return;
        }
        if (key === 'Tab' || code === 9) {
            var focusables = getDialogFocusables(_drawerRoot);
            if (focusables.length === 0) {
                e.preventDefault();
                return;
            }
            var first = focusables[0];
            var last = focusables[focusables.length - 1];
            var active = document.activeElement;
            if (!_drawerRoot.contains(active)) {
                e.preventDefault();
                first.focus();
            } else if (e.shiftKey && active === first) {
                e.preventDefault();
                last.focus();
            } else if (!e.shiftKey && active === last) {
                e.preventDefault();
                first.focus();
            }
        }
    }

    // Removes the dialog, invalidates everything still loading for it and gives focus back to what opened it.
    function closeStreamsDialog(skipFocusRestore) {
        var root = _drawerRoot;
        if (!root) return;
        var itemId = _drawerItemId;
        var returnTo = _drawerReturnFocus;
        _drawerRoot = null;
        _drawerReturnFocus = null;
        document.removeEventListener('keydown', onDrawerKeyDown, true);
        if (root.parentNode) root.parentNode.removeChild(root);
        _drawerToken++;
        _drawerItemId = null;
        _drawerData = null;
        _seriesData = null;
        unlockBodyScroll();
        if (itemId) refreshStreamsButtonCount(itemId);

        if (!skipFocusRestore) {
            try {
                if (returnTo && document.body.contains(returnTo) && typeof returnTo.focus === 'function') {
                    returnTo.focus();
                } else {
                    var fallbackBtn = document.querySelector('.t2j-item-streams-btn');
                    if (fallbackBtn) fallbackBtn.focus();
                }
            } catch (e) { }
        }
    }

    function tabButtonHtml(id, label, active) {
        return '<button is="emby-button" type="button" role="tab" id="' + id + '" aria-selected="' + (active ? 'true' : 'false') + '"'
            + ' class="emby-tab-button emby-button' + (active ? ' emby-tab-button-active' : '') + '">'
            + '<div class="emby-button-foreground">' + escapeHtml(label) + '</div></button>';
    }

    function setTabLabel(tabEl, text) {
        if (!tabEl) return;
        var fg = tabEl.querySelector('.emby-button-foreground');
        (fg || tabEl).textContent = text;
    }

    // "Versions (N)" / "Find releases (N)": a count only once its list is known.
    function updateDrawerTabLabels() {
        var i18n = getI18n();
        var data = _drawerData;
        var tabStreams = document.getElementById('t2j-tab-streams');
        var tabSearch = document.getElementById('t2j-tab-search');
        setTabLabel(tabStreams, i18n.activeStreamsTab + (data && data.ExistingStreams ? ' (' + data.ExistingStreams.length + ')' : ''));
        setTabLabel(tabSearch, i18n.searchAlternativesTab + (data && data.Alternatives ? ' (' + data.Alternatives.length + ')' : ''));
    }

    function syncDrawerTabs() {
        var tabStreams = document.getElementById('t2j-tab-streams');
        var tabSearch = document.getElementById('t2j-tab-search');
        var streamsActive = (_activeDrawerTab === 'streams');
        [[tabStreams, streamsActive], [tabSearch, !streamsActive]].forEach(function (pair) {
            if (!pair[0]) return;
            if (pair[1]) pair[0].classList.add('emby-tab-button-active');
            else pair[0].classList.remove('emby-tab-button-active');
            pair[0].setAttribute('aria-selected', pair[1] ? 'true' : 'false');
        });
    }

    // The header's secondary text after the dialog title ("· Item name").
    function setDialogSubtitle(text) {
        var titleEl = document.getElementById('t2j-drawer-item-title');
        if (titleEl && text) titleEl.textContent = '· ' + text;
    }

    function updateDrawerItemTitle() {
        var data = _drawerData;
        if (!data || !data.Title) return;
        setDialogSubtitle(data.Title + (data.Year ? ' (' + data.Year + ')' : ''));
    }

    // Builds the native dialog shared by the movie and series dialogs (backdrop, header with close button, Esc /
    // back / backdrop closing, scroll lock) around bodyHtml, and makes it the one open dialog. Returns the new
    // dialog token: the caller sets its own state right after (same tick) and passes the token to every async step.
    function openDialogShell(previousFocus, titleText, bodyHtml) {
        if (_drawerRoot) closeStreamsDialog(true);

        // Fresh dialog state for every open, so data of a previous item can never leak into this one, and
        // responses that arrive for a previous open are ignored.
        var myToken = ++_drawerToken;
        _drawerReturnFocus = previousFocus;

        var i18n = getI18n();

        var root = document.createElement('div');
        root.id = 't2j-drawer-root';
        root.className = 'dialogContainer';

        var backdrop = document.createElement('div');
        backdrop.className = 'dialogBackdrop';

        var dialog = document.createElement('div');
        dialog.className = 'focuscontainer dialog formDialog t2j-dialog';
        dialog.setAttribute('role', 'dialog');
        dialog.setAttribute('aria-modal', 'true');
        dialog.setAttribute('aria-labelledby', 't2j-dialog-title');
        dialog.innerHTML =
            '<div class="formDialogHeader">'
            + '<button is="paper-icon-button-light" type="button" class="paper-icon-button-light" id="t2j-drawer-close-btn" title="' + escapeHtml(i18n.closeBtn) + '" aria-label="' + escapeHtml(i18n.closeBtn) + '">' + iconHtml('close') + '</button>'
            + '<h3 class="formDialogHeaderTitle" id="t2j-dialog-title">' + escapeHtml(titleText)
            + ' <span class="secondaryText t2j-dialog-subtitle" id="t2j-drawer-item-title"></span></h3>'
            + '</div>'
            + bodyHtml;

        root.appendChild(backdrop);
        root.appendChild(dialog);
        document.body.appendChild(root);
        _drawerRoot = root;
        lockBodyScroll();
        document.addEventListener('keydown', onDrawerKeyDown, true);
        setTimeout(function () { backdrop.classList.add('dialogBackdropOpened'); }, 20);

        // Backdrop (or the empty area around the dialog) closes it.
        root.addEventListener('click', function (e) {
            if (e.target === root || e.target === backdrop) closeStreamsDialog();
        });
        document.getElementById('t2j-drawer-close-btn').addEventListener('click', function () { closeStreamsDialog(); });

        return myToken;
    }

    // The item behind a dialog, through ApiClient (best effort). onItem runs only while the dialog that asked is still open.
    function loadDialogItem(itemId, token, onItem) {
        var userId = getCurrentUserIdSafe();
        if (!userId || !window.ApiClient || typeof window.ApiClient.getItem !== 'function') return;
        try {
            Promise.resolve(window.ApiClient.getItem(userId, itemId)).then(function (item) {
                if (token !== _drawerToken || !item || !item.Name) return;
                onItem(item);
            }).catch(function () { });
        } catch (e) { }
    }

    // Search box + button of the "Find releases" tab and of the series dialog. The label sits on its own line and
    // the input and button share one row, so the button is exactly as tall as the input. The input is a plain
    // element with the native class: an upgraded emby-input would insert its own label inside that row.
    function findBarHtml(inputId, buttonId, value, i18n) {
        return '<div class="inputContainer t2j-find-bar">'
            + '<label class="inputLabel" for="' + inputId + '">' + escapeHtml(i18n.searchLabel) + '</label>'
            + '<div class="t2j-find-row">'
            + '<input type="text" class="emby-input" id="' + inputId + '" value="' + escapeHtml(value) + '" placeholder="' + escapeHtml(i18n.searchPlaceholder) + '">'
            + '<button is="emby-button" type="button" class="raised button-submit emby-button t2j-find-btn" id="' + buttonId + '"><span>' + escapeHtml(i18n.findBtn) + '</span></button>'
            + '</div>'
            + '</div>';
    }

    async function openStreamsDrawer(itemId, triggerEl) {
        var previousFocus = triggerEl || document.activeElement;
        var i18n = getI18n();

        var myToken = openDialogShell(previousFocus, i18n.drawerTitle,
            '<div class="t2j-tabs" role="tablist"><div class="emby-tabs-slider">'
            + tabButtonHtml('t2j-tab-streams', i18n.activeStreamsTab, true)
            + tabButtonHtml('t2j-tab-search', i18n.searchAlternativesTab, false)
            + '</div></div>'
            + '<div class="t2j-dialog-body" id="t2j-drawer-body" role="tabpanel"></div>');
        _drawerItemId = itemId;
        _drawerData = { ExistingStreams: null, Alternatives: null, Title: '', Year: null, OriginalTitle: '', SearchQuery: '', typedQuery: null, streamsError: '', altsError: '', altsLoading: false };
        _activeDrawerTab = 'streams';
        _drawerSearchFilter = 'all';

        var tabStreams = document.getElementById('t2j-tab-streams');
        var tabSearch = document.getElementById('t2j-tab-search');

        tabStreams.addEventListener('click', function () {
            if (myToken !== _drawerToken) return;
            _activeDrawerTab = 'streams';
            syncDrawerTabs();
            renderDrawerContent(itemId);
        });

        tabSearch.addEventListener('click', function () {
            if (myToken !== _drawerToken) return;
            _activeDrawerTab = 'search';
            syncDrawerTabs();
            renderDrawerContent(itemId);
            ensureDrawerAlternatives(itemId, myToken);
        });

        renderDrawerContent(itemId);
        try { tabStreams.focus(); } catch (e) { }

        // Versions first (a fast call); the release search runs lazily, the first time its tab is opened.
        loadDrawerItemTitle(itemId, myToken);
        loadDrawerStreams(itemId, myToken);
    }

    // The header shows the item's name; Alternatives later overrides it with the server's own title.
    function loadDrawerItemTitle(itemId, token) {
        loadDialogItem(itemId, token, function (item) {
            if (!_drawerData || _drawerData.Title) return;
            _drawerData.Title = String(item.Name);
            _drawerData.Year = item.ProductionYear || null;
            updateDrawerItemTitle();
        });
    }

    async function loadDrawerStreams(itemId, token) {
        var i18n = getI18n();
        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/Streams'), { headers: getAuthHeaders() });
            if (token !== _drawerToken) return false;
            if (res.ok) {
                var streams = await res.json();
                if (token !== _drawerToken) return false;
                _drawerData.ExistingStreams = Array.isArray(streams) ? streams : [];
                _drawerData.streamsError = '';
                updateDrawerTabLabels();
                renderDrawerContent(itemId);
                return true;
            }
            var authMsg = authErrorMessage(res.status, i18n);
            _drawerData.streamsError = authMsg || (i18n.failedResponse + ' (' + res.status + ')');
            if (authMsg) showToast(authMsg, false);
        } catch (e) {
            if (token !== _drawerToken) return false;
            _drawerData.streamsError = i18n.connError + ': ' + (e && e.message ? e.message : e);
        }
        renderDrawerContent(itemId);
        return false;
    }

    // Loads the release search for the dialog's item (once; reopening the tab retries after a failure).
    async function ensureDrawerAlternatives(itemId, token) {
        var data = _drawerData;
        if (!data || data.Alternatives || data.altsLoading) return;
        var i18n = getI18n();
        data.altsLoading = true;
        data.altsError = '';
        renderDrawerContent(itemId);

        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/Alternatives'), { headers: getAuthHeaders() });
            if (token !== _drawerToken) return;
            if (res.ok) {
                var loaded = await res.json();
                if (token !== _drawerToken) return;
                data.Alternatives = (loaded && loaded.Alternatives) || [];
                if (loaded && Array.isArray(loaded.ExistingStreams)) data.ExistingStreams = loaded.ExistingStreams;
                if (loaded && loaded.Title) {
                    data.Title = loaded.Title;
                    data.Year = loaded.Year || null;
                }
                data.OriginalTitle = (loaded && loaded.OriginalTitle) || '';
                updateDrawerItemTitle();
            } else {
                var authMsg = authErrorMessage(res.status, i18n);
                data.altsError = authMsg || i18n.failedResponse;
                if (authMsg) showToast(authMsg, false);
            }
        } catch (e) {
            if (token !== _drawerToken) return;
            data.altsError = i18n.connError + ': ' + (e && e.message ? e.message : e);
        }
        data.altsLoading = false;
        updateDrawerTabLabels();
        renderDrawerContent(itemId);
    }

    // After an add / replace / delete: back to the Versions tab with a fresh list (only for the dialog that asked).
    function reloadDrawerStreams(itemId, token) {
        if (token !== _drawerToken || _drawerItemId !== itemId || !_drawerData) return;
        _activeDrawerTab = 'streams';
        syncDrawerTabs();
        loadDrawerStreams(itemId, token);
    }

    function versionRowHtml(s, idx, i18n) {
        var isPrimary = !!s.IsPrimary;
        var shortHash = s.TorrentHash ? (s.TorrentHash.length > 8 ? s.TorrentHash.substring(0, 8) : s.TorrentHash) : '';
        var label = s.VersionLabel || s.Name || '';

        var meta = '<span title="' + escapeHtml(s.Path) + '">' + escapeHtml(s.Name) + '</span>';
        if (shortHash) meta += '<span aria-hidden="true">&middot;</span><span>#' + escapeHtml(shortHash) + '</span>';

        return '<div class="listItem listItem-border t2j-row" data-action="none">'
            + iconHtml(isPrimary ? 'star' : 'layers', 'listItemIcon listItemIcon-transparent')
            + '<div class="listItemBody t2j-body">'
            + '<div class="listItemBodyText t2j-title-row">'
            + '<span class="t2j-title-text" title="' + escapeHtml(label) + '">' + escapeHtml(label) + '</span>'
            + (isPrimary ? chipHtml(i18n.currentBadge, true) : '')
            + '</div>'
            + '<div class="listItemBodyText secondary t2j-meta">' + meta + '</div>'
            + '</div>'
            + '<div class="t2j-actions">' + (s.TorrentHash ? actionButtonHtml('delete', idx, 'delete', i18n.deleteStreamTitle) : '') + '</div>'
            + '</div>';
    }

    function renderDrawerContent(itemId) {
        var body = document.getElementById('t2j-drawer-body');
        if (!body || !_drawerData || _drawerItemId !== itemId) return;

        var i18n = getI18n();
        var data = _drawerData;

        if (_activeDrawerTab === 'streams') {
            if (!data.ExistingStreams) {
                body.innerHTML = stateLineHtml(data.streamsError || i18n.loading, !!data.streamsError);
                return;
            }

            var streams = data.ExistingStreams;
            if (streams.length === 0) {
                // Also what a movie that only has a placeholder looks like: the next step is the "Find releases" tab.
                body.innerHTML = stateLineHtml(i18n.noStreamYet, false)
                    + '<button is="emby-button" type="button" class="raised button-submit emby-button" id="t2j-goto-search"><span>' + escapeHtml(i18n.searchAlternativesTab) + '</span></button>';
                var gotoSearch = document.getElementById('t2j-goto-search');
                var searchTabBtn = document.getElementById('t2j-tab-search');
                if (gotoSearch && searchTabBtn) {
                    gotoSearch.addEventListener('click', function () { searchTabBtn.click(); });
                }
                return;
            }

            var html = '<div class="paperList t2j-list">';
            streams.forEach(function (s, idx) {
                html += versionRowHtml(s, idx, i18n);
            });
            html += '</div>';
            body.innerHTML = html;

            var versionsList = body.querySelector('.t2j-list');
            versionsList.addEventListener('click', function (e) {
                var btn = closestAct(e.target, versionsList);
                if (!btn || btn.getAttribute('data-act') !== 'delete') return;
                var s = streams[parseInt(btn.getAttribute('data-idx'), 10)];
                if (s) deleteDrawerStream(itemId, btn, s, i18n);
            });
        } else {
            renderDrawerSearchTab(body, itemId, i18n, data);
        }
    }

    async function deleteDrawerStream(itemId, btn, stream, i18n) {
        var hash = stream.TorrentHash;
        var name = stream.VersionLabel || stream.Name;
        if (!hash) return;
        if (isActLocked(btn)) return;
        if (!confirm(i18n.deleteStreamConfirm + '\n\n"' + name + '"')) return;

        var token = _drawerToken;
        setActState(btn, 'busy', i18n.loading);

        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/Streams/' + encodeURIComponent(hash)), {
                method: 'DELETE',
                headers: getAuthHeaders()
            });
            if (res.ok) {
                showToast(i18n.streamDeletedSuccess, true);
                reloadDrawerStreams(itemId, token);
                refreshStreamsButtonCount(itemId);
            } else {
                setActState(btn, 'idle');
                showToast(authErrorMessage(res.status, i18n) || i18n.serverError, false);
            }
        } catch (e) {
            setActState(btn, 'idle');
            showToast(i18n.connError, false);
        }
    }

    // "Find releases" tab: search box, tagged filter, then the same rows as the search page with add / replace actions.
    function renderDrawerSearchTab(body, itemId, i18n, data) {
        if (!data.Alternatives) {
            body.innerHTML = stateLineHtml(data.altsError || (i18n.searching + '...'), !!data.altsError);
            return;
        }

        var alts = data.Alternatives;
        var queryVal = (data.typedQuery != null) ? data.typedQuery : (data.SearchQuery || (data.OriginalTitle || data.Title || ''));

        var taggedAlts = alts.filter(function (r) {
            return r.IsGold || (r.Tags && r.Tags.length > 0);
        });
        var filterOn = (_drawerSearchFilter === 'gold');
        var activeList = filterOn ? taggedAlts : alts;

        var existingStreams = data.ExistingStreams || [];
        var primaryStream = existingStreams.find(function (s) { return s.IsPrimary; }) || existingStreams[0];

        var html =
            findBarHtml('t2j-drawer-search-input', 't2j-drawer-search-btn', queryVal, i18n)
            + '<div class="t2j-filter-row">'
            + '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" id="t2j-filter-tagged-dlg"' + (filterOn ? ' checked' : '') + '>'
            + '<span>' + escapeHtml(i18n.onlyTagged) + ' (' + taggedAlts.length + ')</span></label>'
            + '<div class="fieldDescription">' + activeList.length + ' ' + escapeHtml(i18n.releasesFound) + '</div>'
            + '</div>';

        if (activeList.length === 0) {
            html += stateLineHtml(i18n.nothingFound, false) + '<div class="fieldDescription">' + escapeHtml(i18n.nothingFoundHint) + '</div>';
        } else {
            html += '<div class="paperList t2j-list">';
            activeList.forEach(function (item, idx) {
                var isAdded = existingStreams.some(function (es) {
                    return es.TorrentHash && item.InfoHash && es.TorrentHash.toLowerCase() === item.InfoHash.toLowerCase();
                });
                var actions = actionButtonHtml('add', idx, 'add', i18n.addAsVersionBtn);
                if (primaryStream && primaryStream.TorrentHash) {
                    actions += actionButtonHtml('replace', idx, 'swap_horiz', i18n.replaceStreamBtn);
                }
                html += releaseRowHtml(item, idx, i18n, actions, isAdded ? chipHtml(i18n.alreadyInLibrary, false) : '');
            });
            html += '</div>';
        }

        body.innerHTML = html;

        var inputEl = document.getElementById('t2j-drawer-search-input');
        var searchBtn = document.getElementById('t2j-drawer-search-btn');
        var drawerTokenAtRender = _drawerToken;
        async function triggerDrawerSearch() {
            var q = (inputEl.value || '').trim();
            if (!q) return;
            searchBtn.disabled = true;
            setButtonText(searchBtn, i18n.searchBusy);
            try {
                var sres = await fetch(getApiUrl('Plugins/Torr2Jelly/Search?query=' + encodeURIComponent(q)), { headers: getAuthHeaders() });
                // The dialog was closed or re-opened for another item while we waited: drop the response.
                if (drawerTokenAtRender !== _drawerToken || !_drawerData) return;
                if (sres.ok) {
                    var found = await sres.json();
                    if (drawerTokenAtRender !== _drawerToken || !_drawerData) return;
                    _drawerData.Alternatives = found || [];
                    _drawerData.SearchQuery = q;
                    _drawerData.typedQuery = null;
                    updateDrawerTabLabels();
                    renderDrawerContent(itemId);
                } else {
                    showToast(authErrorMessage(sres.status, i18n) || i18n.searchFailed, false);
                }
            } catch (e) {
                showToast(i18n.connError, false);
            } finally {
                searchBtn.disabled = false;
                setButtonText(searchBtn, i18n.findBtn);
            }
        }

        if (searchBtn) searchBtn.addEventListener('click', triggerDrawerSearch);
        if (inputEl) {
            inputEl.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') triggerDrawerSearch();
            });
        }

        var filterBox = document.getElementById('t2j-filter-tagged-dlg');
        if (filterBox) {
            filterBox.addEventListener('change', function () {
                _drawerSearchFilter = filterBox.checked ? 'gold' : 'all';
                if (inputEl) data.typedQuery = inputEl.value;
                renderDrawerContent(itemId);
                var again = document.getElementById('t2j-filter-tagged-dlg');
                if (again && typeof again.focus === 'function') again.focus();
            });
        }

        var releasesList = body.querySelector('.t2j-list');
        if (releasesList) {
            releasesList.addEventListener('click', function (e) {
                var btn = closestAct(e.target, releasesList);
                if (!btn) return;
                var act = btn.getAttribute('data-act');
                var item = activeList[parseInt(btn.getAttribute('data-idx'), 10)];
                if (!item) return;
                if (act === 'add') {
                    submitAlternateStream(itemId, btn, item, '', i18n);
                } else if (act === 'replace' && primaryStream && primaryStream.TorrentHash) {
                    submitAlternateStream(itemId, btn, item, primaryStream.TorrentHash, i18n);
                }
            });
        }
    }

    function setButtonText(buttonEl, text) {
        var span = buttonEl ? buttonEl.querySelector('span') : null;
        (span || buttonEl).textContent = text;
    }

    // Add a release as another version of the item, or (oldHash set) replace the primary one with it.
    async function submitAlternateStream(itemId, btn, item, oldHash, i18n) {
        var link = pickLink(item);
        if (!link) return;
        if (isActLocked(btn)) return;
        var replacing = !!oldHash;
        if (replacing && !confirm(i18n.replaceConfirm)) return;

        var token = _drawerToken;
        setActState(btn, 'busy', i18n.adding);

        try {
            var payload = {
                Link: link,
                Tracker: item.Indexer || 'Torrent',
                Seeders: parseInt(item.Seeders, 10) || 0,
                TorrentTitle: item.Title || '',
                ReplaceExisting: replacing
            };
            if (replacing) payload.OldTorrentHash = oldHash;

            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/AlternateStream'), {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify(payload)
            });

            if (res.ok) {
                setActState(btn, 'done', i18n.added);
                showToast(i18n.streamAddedSuccess, true);

                // Show the new list only if the dialog is still on this same item.
                setTimeout(function () {
                    reloadDrawerStreams(itemId, token);
                }, 1200);
                refreshStreamsButtonCount(itemId);

                if (window.ApiClient && window.ApiClient.getItem) {
                    window.ApiClient.getItem(window.ApiClient.getCurrentUserId(), itemId).then(function (updItem) {
                        if (window.Events && typeof window.Events.trigger === 'function') {
                            window.Events.trigger(window.ApiClient, 'itemupdate', [updItem]);
                        }
                    }).catch(function () {});
                }
            } else {
                var errMsg = await readErrorMessage(res, i18n);
                setActState(btn, 'error', i18n.serverError);
                showToast(authErrorMessage(res.status, i18n) ? errMsg : (i18n.failedResponse + ': ' + errMsg), false);
            }
        } catch (e) {
            setActState(btn, 'error', i18n.serverError);
            showToast(i18n.connError + ': ' + e.message, false);
        }
    }

    // ==========================================
    // Series dialog: add season / full-series torrents to a series
    // ==========================================

    // A results request belongs to the series dialog that started it. It is stale (and must not render) once that
    // dialog was closed or replaced, or once a newer request for the same dialog started.
    function isSeriesRequestStale(data, reqId) {
        return _seriesData !== data || data.token !== _drawerToken || data.reqId !== reqId;
    }

    function openSeriesDialog(itemId, triggerEl) {
        var previousFocus = triggerEl || document.activeElement;
        var i18n = getI18n();

        var myToken = openDialogShell(previousFocus, i18n.seriesDialogTitle,
            '<div class="t2j-series-bar">' + findBarHtml('t2j-series-search-input', 't2j-series-search-btn', '', i18n) + '</div>'
            + '<div class="t2j-dialog-body" id="t2j-drawer-body"></div>');
        _drawerItemId = itemId;
        var data = _seriesData = {
            itemId: itemId,
            token: myToken,
            Title: '',
            OriginalTitle: '',
            Results: null,   // releases of the last load / search; null = still loading
            error: '',       // shown instead of the list when the last load / search failed
            showAll: false,
            reqId: 0,        // only the latest results request may render
            searching: '',   // query of the manual search in flight, so a double click does not repeat it
            edited: false,   // the user typed in the search box: stop prefilling it
            states: {}       // release link -> 'busy' | 'done' | 'error'; survives re-renders of the list
        };

        var inputEl = document.getElementById('t2j-series-search-input');
        var searchBtn = document.getElementById('t2j-series-search-btn');
        inputEl.addEventListener('input', function () { data.edited = true; });
        inputEl.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.isComposing) {
                e.preventDefault();
                runSeriesSearch();
            }
        });
        searchBtn.addEventListener('click', function () { runSeriesSearch(); });

        renderSeriesResults();
        // Typing starts right away with a mouse; touch and remote-control devices would pop up a keyboard instead.
        var finePointer = false;
        try { finePointer = !!(window.matchMedia && window.matchMedia('(pointer: fine)').matches); } catch (e) { }
        try { (finePointer ? inputEl : searchBtn).focus(); } catch (e) { }

        // The item's name fills the header and the search box at once; the release search below can take a while.
        loadDialogItem(itemId, myToken, function (item) {
            if (_seriesData !== data) return;
            if (!data.Title) {
                data.Title = String(item.Name);
                setDialogSubtitle(data.Title);
            }
            prefillSeriesQuery(item.OriginalTitle || item.Name);
        });
        loadSeriesAlternatives();
    }

    // Puts the series name into the (still empty, untouched) search box.
    function prefillSeriesQuery(text) {
        var data = _seriesData;
        var inputEl = document.getElementById('t2j-series-search-input');
        if (!data || !inputEl || data.edited || inputEl.value || !text) return;
        inputEl.value = String(text);
    }

    // Common GET for the dialog's results: marks them loading, then renders the list or the error of the latest request.
    async function fetchSeriesResults(url, extractResults) {
        var data = _seriesData;
        if (!data) return;
        var i18n = getI18n();
        var reqId = ++data.reqId;
        data.Results = null;
        data.error = '';
        data.showAll = false;
        renderSeriesResults();

        try {
            var res = await fetch(url, { headers: getAuthHeaders() });
            if (isSeriesRequestStale(data, reqId)) return;
            if (res.ok) {
                var payload = await res.json();
                if (isSeriesRequestStale(data, reqId)) return;
                data.Results = extractResults(payload, data);
            } else {
                var friendly = authErrorMessage(res.status, i18n);
                var failure = friendly || (i18n.searchFailed + ': ' + await readErrorMessage(res, i18n));
                if (isSeriesRequestStale(data, reqId)) return;
                data.error = failure;
                if (friendly) showToast(friendly, false);
            }
        } catch (e) {
            if (isSeriesRequestStale(data, reqId)) return;
            data.error = i18n.connError + ': ' + (e && e.message ? e.message : e);
        }
        data.searching = '';
        renderSeriesResults();
    }

    // Initial results: the same IMDb-then-title search the movie "Find releases" tab starts with.
    function loadSeriesAlternatives() {
        var data = _seriesData;
        if (!data) return;
        return fetchSeriesResults(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(data.itemId) + '/Alternatives'), function (loaded, d) {
            if (loaded && loaded.Title) {
                d.Title = String(loaded.Title);
                setDialogSubtitle(d.Title);
            }
            d.OriginalTitle = (loaded && loaded.OriginalTitle) || '';
            prefillSeriesQuery(d.OriginalTitle || d.Title);
            return (loaded && Array.isArray(loaded.Alternatives)) ? loaded.Alternatives : [];
        });
    }

    // Manual search, exactly like the "Find releases" tab: GET Search?query=...
    function runSeriesSearch() {
        var data = _seriesData;
        var inputEl = document.getElementById('t2j-series-search-input');
        if (!data || !inputEl) return;
        var query = (inputEl.value || '').trim();
        if (!query || data.searching === query) return;
        data.searching = query;
        return fetchSeriesResults(getApiUrl('Plugins/Torr2Jelly/Search?query=' + encodeURIComponent(query)), function (found) {
            return Array.isArray(found) ? found : [];
        });
    }

    // Brings every add button of the list in line with the states kept for the dialog (a re-render, an add that
    // finishes later, "Show all" must neither lose a check mark nor re-enable a button whose add is running).
    function applySeriesActStates() {
        var data = _seriesData;
        var body = document.getElementById('t2j-drawer-body');
        if (!data || !data.Results || !body) return;
        var i18n = getI18n();
        var buttons = body.querySelectorAll('.t2j-act[data-act="addseries"]');
        for (var i = 0; i < buttons.length; i++) {
            var btn = buttons[i];
            var item = data.Results[parseInt(btn.getAttribute('data-idx'), 10)];
            var state = (item && data.states[pickLink(item)]) || 'idle';
            if ((btn.getAttribute('data-state') || 'idle') === state) continue;
            var title = state === 'busy' ? i18n.adding
                : (state === 'done' ? i18n.seriesAddedTitle : (state === 'error' ? i18n.serverError : ''));
            setActState(btn, state, title);
        }
    }

    // Loading / error / empty state line, or the first 8 releases (all of them after "Show all") as the shared rows.
    function renderSeriesResults() {
        var data = _seriesData;
        var body = document.getElementById('t2j-drawer-body');
        if (!data || !body) return;
        var i18n = getI18n();

        if (data.error) {
            body.innerHTML = stateLineHtml(data.error, true);
            return;
        }
        if (!data.Results) {
            body.innerHTML = stateLineHtml(i18n.searchBusy, false);
            return;
        }
        var list = data.Results;
        if (list.length === 0) {
            body.innerHTML = stateLineHtml(i18n.nothingFound, false) + '<div class="fieldDescription">' + escapeHtml(i18n.nothingFoundHint) + '</div>';
            return;
        }

        var visibleCount = data.showAll ? list.length : Math.min(list.length, SEARCH_PAGE_SIZE);
        var html = '<div class="paperList t2j-list">';
        for (var i = 0; i < visibleCount; i++) {
            html += releaseRowHtml(list[i], i, i18n, actionButtonHtml('addseries', i, 'add', i18n.seriesAddTitle), '', 'tv');
        }
        html += '</div>';
        if (list.length > visibleCount) {
            html += '<button is="emby-button" type="button" class="button-flat emby-button t2j-more" id="t2j-series-show-all">'
                + '<span>' + escapeHtml(i18n.showAll) + ' (' + list.length + ')</span></button>';
        }
        body.innerHTML = html;
        applySeriesActStates();

        var listEl = body.querySelector('.t2j-list');
        listEl.addEventListener('click', function (e) {
            var btn = closestAct(e.target, listEl);
            if (!btn || btn.getAttribute('data-act') !== 'addseries') return;
            var item = list[parseInt(btn.getAttribute('data-idx'), 10)];
            if (item) addSeriesTorrent(item);
        });

        var moreBtn = body.querySelector('#t2j-series-show-all');
        if (moreBtn) {
            moreBtn.addEventListener('click', function () {
                var scrollTop = body.scrollTop;
                data.showAll = true;
                renderSeriesResults();
                body.scrollTop = scrollTop;
                var firstNew = body.querySelector('.t2j-act[data-idx="' + SEARCH_PAGE_SIZE + '"]');
                if (firstNew && typeof firstNew.focus === 'function') firstNew.focus();
            });
        }
    }

    // Adds one release to the series: the server resolves the episodes of the torrent and writes them into the series.
    async function addSeriesTorrent(item) {
        var data = _seriesData;
        if (!data) return;
        var link = pickLink(item);
        if (!link) return;
        // One add per release at a time, and a release that went in is not added twice.
        if (data.states[link] === 'busy' || data.states[link] === 'done') return;

        var i18n = getI18n();
        var seriesName = data.Title;
        data.states[link] = 'busy';
        applySeriesActStates();
        var finalState = 'error';

        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(data.itemId) + '/SeriesTorrent'), {
                method: 'POST',
                headers: getAuthHeaders(),
                body: JSON.stringify({ Link: link, TorrentTitle: item.Title || '' })
            });

            if (res.ok) {
                // Success and !Pending: the episodes are written (EpisodesWritten). Pending: the torrent was accepted but
                // TorrServer has not listed its files yet, so the episodes follow with a later sync.
                var result = await res.json().catch(function () { return null; });
                var outcome = result || {};
                var pending = outcome.Pending === true || outcome.pending === true;
                var success = outcome.Success === true || outcome.success === true;
                var written = parseInt(outcome.EpisodesWritten != null ? outcome.EpisodesWritten : outcome.episodesWritten, 10);

                if (!pending && !success && result) {
                    var reason = outcome.Message || outcome.message || '';
                    showToast(i18n.failedResponse + (reason ? ': ' + reason : ''), false);
                } else {
                    finalState = 'done';
                    if (!pending && success && isFinite(written)) {
                        showToast(i18n.seriesAddedToast(written, seriesName), true);
                    } else {
                        showToast(i18n.seriesPendingToast, true);
                    }
                }
            } else {
                var errMsg = await readErrorMessage(res, i18n);
                showToast(authErrorMessage(res.status, i18n) ? errMsg : (i18n.failedResponse + ': ' + errMsg), false);
            }
        } catch (e) {
            showToast(i18n.connError + ': ' + (e && e.message ? e.message : e), false);
        }

        // The dialog may have been closed meanwhile: its state object is then simply dropped with it.
        data.states[link] = finalState;
        applySeriesActStates();
    }

    // ==========================================
    // Resilient Item Details Page & Button Watcher
    // ==========================================
    function getActiveItemDetailPage() {
        var pages = document.querySelectorAll('.itemDetailPage, #itemDetailPage');
        if (pages.length === 0) return null;

        var fullUrl = window.location.href;
        var match = fullUrl.match(/[?&]id=([a-f0-9\-]+)/i);
        var urlId = match ? match[1] : null;

        // 1. Look for non-hidden page matching current URL item ID
        for (var i = 0; i < pages.length; i++) {
            var p = pages[i];
            if (p.classList.contains('hide') || p.style.display === 'none') continue;
            var pid = p.getAttribute('data-itemid');
            if (urlId && pid && pid.toLowerCase() === urlId.toLowerCase()) {
                return p;
            }
        }

        // 2. Look for any active visible page
        for (var j = pages.length - 1; j >= 0; j--) {
            var pj = pages[j];
            if (!pj.classList.contains('hide') && pj.style.display !== 'none') {
                return pj;
            }
        }

        // 3. Fallback: most recently created page matching URL
        if (urlId) {
            for (var k = pages.length - 1; k >= 0; k--) {
                if (pages[k].getAttribute('data-itemid') === urlId) {
                    return pages[k];
                }
            }
        }

        return pages[pages.length - 1];
    }

    function getActiveItemId(page) {
        var fullUrl = window.location.href;
        var match = fullUrl.match(/[?&]id=([a-f0-9\-]+)/i);
        if (match && match[1]) return match[1];
        if (page && page.getAttribute('data-itemid')) return page.getAttribute('data-itemid');
        return null;
    }

    function openItemDialog(kind, itemId, triggerEl) {
        if (kind === 'series') {
            openSeriesDialog(itemId, triggerEl);
        } else {
            openStreamsDrawer(itemId, triggerEl);
        }
    }

    // The movie button carries the version count only in its title ("Torrent versions (2)"); there is no count pill.
    // The series button has no count: its title stays "Add torrents to this series".
    async function updateStreamsButtonCount(btn, itemId) {
        if (!btn || !itemId || btn._t2jKind === 'series') return;
        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/Streams'), { headers: getAuthHeaders() });
            if (res.ok) {
                var streams = await res.json();
                var i18n = getI18n();
                var label = (Array.isArray(streams) && streams.length > 0) ? (i18n.drawerTitle + ' (' + streams.length + ')') : i18n.drawerTitle;
                btn.setAttribute('title', label);
                btn.setAttribute('aria-label', label);
            }
        } catch (e) {}
    }

    function refreshStreamsButtonCount(itemId) {
        document.querySelectorAll('.t2j-item-streams-btn').forEach(function (btn) {
            if (!itemId || btn._t2jItemId === itemId) {
                updateStreamsButtonCount(btn, btn._t2jItemId || itemId);
            }
        });
    }

    var _buttonsObserver = null;
    function ensureButtonInContainer(page, buttonsContainer, itemId, kind) {
        if (!buttonsContainer) return;

        var existingBtn = buttonsContainer.querySelector('.t2j-item-streams-btn');
        if (existingBtn) {
            // A button from an older markup (with a text label or a count pill), or one of the other kind (the page
            // was reused for another item type), is replaced by the current one.
            if (existingBtn.querySelector('.detailButton-text, .t2j-stream-count') || existingBtn._t2jKind !== kind) {
                existingBtn.remove();
            } else {
                if (existingBtn._t2jItemId !== itemId) {
                    existingBtn._t2jItemId = itemId;
                    existingBtn.onclick = function (e) {
                        e.preventDefault();
                        e.stopPropagation();
                        openItemDialog(kind, itemId, existingBtn);
                    };
                    updateStreamsButtonCount(existingBtn, itemId);
                }
                return;
            }
        }

        injectStreamsButton(page, buttonsContainer, itemId, kind);

        if (buttonsContainer._t2jObserverAttached !== bindToken) {
            buttonsContainer._t2jObserverAttached = bindToken;
            if (_buttonsObserver) {
                try { _buttonsObserver.disconnect(); } catch (e) {}
            }
            _buttonsObserver = new MutationObserver(function () {
                if (disposed) return;
                var currentPage = getActiveItemDetailPage();
                if (!currentPage) return;
                var currentId = getActiveItemId(currentPage);
                var curContainer = currentPage.querySelector('.mainDetailButtons');
                if (curContainer && currentId && !curContainer.querySelector('.t2j-item-streams-btn')) {
                    var currentKind = getStreamsButtonKind(currentId);
                    if (currentKind) injectStreamsButton(currentPage, curContainer, currentId, currentKind);
                }
            });
            _buttonsObserver.observe(buttonsContainer, { childList: true });
        }
    }

    function checkItemDetailPage() {
        var page = getActiveItemDetailPage();
        if (!page) return;

        var itemId = getActiveItemId(page);
        if (!itemId) return;

        var allContainers = page.querySelectorAll('.mainDetailButtons');
        if (allContainers.length === 0) return;

        // Torrent management is admin-only on the server: only admins get a button, and which one depends on the
        // item: versions for a Movie, add-to-series for a Series. null = still resolving (admin/type lookup).
        var kind = getStreamsButtonKind(itemId);
        if (kind === null) return;
        if (kind === false) {
            page.querySelectorAll('.t2j-item-streams-btn').forEach(function (b) { b.remove(); });
            return;
        }

        allContainers.forEach(function (container) {
            ensureButtonInContainer(page, container, itemId, kind);
        });
    }

    // Native detail button (icon only), exactly like Play / Favorite next to it.
    async function injectStreamsButton(page, buttonsContainer, itemId, kind) {
        var i18n = getI18n();
        var isSeries = (kind === 'series');
        var label = isSeries ? i18n.seriesBtnTitle : i18n.drawerTitle;
        var btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.type = 'button';
        btn.className = 'button-flat detailButton emby-button t2j-item-streams-btn' + (isSeries ? ' t2j-item-series-btn' : '');
        btn.setAttribute('title', label);
        btn.setAttribute('aria-label', label);
        btn._t2jItemId = itemId;
        btn._t2jKind = kind;

        btn.innerHTML = '<div class="detailButton-content">' + iconHtml(isSeries ? 'playlist_add' : 'video_library', 'detailButton-icon') + '</div>';

        btn.onclick = function (e) {
            e.preventDefault();
            e.stopPropagation();
            openItemDialog(kind, btn._t2jItemId || itemId, btn);
        };

        buttonsContainer.appendChild(btn);
        updateStreamsButtonCount(btn, itemId);
    }

    // Page watchers
    function checkAllPages() {
        if (disposed) return;
        checkSearchPage();
        checkItemDetailPage();
    }

    function onHashChange() {
        if (disposed) return;
        // A dialog belongs to the page it was opened on.
        closeStreamsDialog(true);
        // Navigating away from the search route: forget the previous query/results immediately.
        if ((window.location.hash || '').indexOf('/search') === -1) {
            resetSearchState();
        }
        setTimeout(checkAllPages, 50);
        setTimeout(checkAllPages, 250);
        setTimeout(checkAllPages, 600);
    }

    document.addEventListener('viewshow', checkAllPages);
    document.addEventListener('pageshow', checkAllPages);
    window.addEventListener('hashchange', onHashChange);
    window.addEventListener('popstate', checkAllPages);
    pollTimer = setInterval(function () { if (!document.hidden) checkAllPages(); }, 400);
    setTimeout(checkAllPages, 100);
    setTimeout(checkAllPages, 500);

    // Lets a newer copy of this script take over (see the top of the file).
    window._t2jSearchDispose = function () {
        disposed = true;
        clearInterval(pollTimer);
        clearTimeout(debounceTimer);
        cancelPendingSearch();
        document.removeEventListener('viewshow', checkAllPages);
        document.removeEventListener('pageshow', checkAllPages);
        window.removeEventListener('hashchange', onHashChange);
        window.removeEventListener('popstate', checkAllPages);
        if (_buttonsObserver) {
            try { _buttonsObserver.disconnect(); } catch (e) { }
        }
        closeStreamsDialog(true);
        removeResultsContainer();
        document.querySelectorAll('.t2j-item-streams-btn, .toast.t2j-toast').forEach(function (el) {
            if (el.parentNode) el.parentNode.removeChild(el);
        });
        if (style.parentNode) style.parentNode.removeChild(style);
        window._t2jSearchDispose = null;
        window._t2jSearchInitialized = false;
    };
})();
