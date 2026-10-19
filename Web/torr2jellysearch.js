(function () {
    'use strict';

    if (window._t2jSearchInitialized) return;
    window._t2jSearchInitialized = true;

    console.log('[Torr2Jelly] Search integration script loaded');

    var currentQuery = '';
    var debounceTimer = null;
    var abortController = null;
    var isSearching = false;
    var searchRequestId = 0; // only the latest search request may render

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
            searching: isUk ? 'Шукаємо на торент-трекерах за запитом' : 'Searching torrent indexers for',
            notFoundTitle: isUk ? 'Немає на сервері? Знайдено в торентах' : 'Not in library? Found on torrents',
            foundTitle: isUk ? 'Також доступно на торентах (Torr2Jelly)' : 'Also available on torrents (Torr2Jelly)',
            subtitle: isUk ? 'Додайте в один клік — відео миттєво з\'явиться в Jellyfin та транслюватиметься на льоту' : 'Add in one click — streams instantly without downloading the whole file',
            releasesFound: isUk ? 'роздач знайдено' : 'releases found',
            categoryLabel: isUk ? 'Категорія / Бібліотека:' : 'Category / Library:',
            goldBadge: isUk ? '⭐ Золотий' : '⭐ Gold',
            filterAll: isUk ? 'Всі' : 'All',
            filterGold: isUk ? '🏷️ З бонусами' : '🏷️ With Tags',
            addBtn: isUk ? '+ Додати & Дивитися' : '+ Add & Watch',
            adding: isUk ? 'Додаємо...' : 'Adding...',
            added: isUk ? '✓ Додано в медіатеку' : '✓ Added to library',
            addedToast: isUk ? 'успішно додано! Оновлюємо медіатеку...' : 'successfully added! Refreshing library...',
            addedPendingToast: isUk ? 'додано! Файли з\'являться за кілька хвилин, щойно TorrServer завантажить метадані.' : 'added! The files will appear shortly, once TorrServer has loaded the torrent metadata.',
            serverError: isUk ? '⚠ Помилка сервера' : '⚠ Server Error',
            failedResponse: isUk ? 'Невдала відповідь' : 'Request failed',
            connError: isUk ? 'Помилка з\'єднання' : 'Connection error',
            streamsBtn: isUk ? 'Стріми' : 'Streams',
            drawerTitle: isUk ? 'Керування торрент-стрімами' : 'Manage Torrent Streams',
            activeStreamsTab: isUk ? 'Активні версії' : 'Active Versions',
            searchAlternativesTab: isUk ? 'Пошук роздач' : 'Find Releases',
            currentBadge: isUk ? 'ОСНОВНА' : 'PRIMARY',
            versionBadge: isUk ? 'ВЕРСІЯ' : 'VERSION',
            deleteStreamConfirm: isUk ? 'Ви дійсно хочете видалити цей стрім та очистити торрент?' : 'Are you sure you want to delete this stream and remove the torrent?',
            addAsVersionBtn: isUk ? '+ Додати як версію' : '+ Add as Version',
            replaceStreamBtn: isUk ? '🔄 Замінити поточний' : '🔄 Replace Current',
            replaceConfirm: isUk ? 'Замінити поточну версію цією роздачею?' : 'Replace current stream with this release?',
            noStreamsFound: isUk ? 'Не знайдено активних .strm файлів' : 'No active .strm files found',
            refreshingMedia: isUk ? 'Оновлюємо сторінку...' : 'Refreshing media...',
            streamAddedSuccess: isUk ? 'Роздачу успішно додано як версію стріму!' : 'Release successfully added to item!',
            streamDeletedSuccess: isUk ? 'Стрім успішно видалено' : 'Stream deleted successfully',
            seriesBadge: isUk ? '📺 Серіал' : '📺 Series',
            silverBadge: isUk ? '🥈 Срібна' : '🥈 Silver',
            bronzeBadge: isUk ? '🥉 Бронзова' : '🥉 Bronze',
            authorBadge: isUk ? '💎 Авторський' : '💎 Author',
            exclusiveBadge: isUk ? '🔥 Ексклюзив' : '🔥 Exclusive',
            noStreamsHint: isUk ? 'Перейдіть на вкладку "Пошук роздач", щоб знайти та додати версію.' : 'Switch to the "Find Releases" tab to find and add a version.',
            deleteStreamTitle: isUk ? 'Видалити цей стрім' : 'Delete this stream',
            searchPlaceholder: isUk ? 'Введіть назву для пошуку...' : 'Enter a title to search...',
            findBtn: isUk ? '🔍 Знайти' : '🔍 Search',
            nothingFound: isUk ? 'Нічого не знайдено' : 'Nothing found',
            nothingFoundHint: isUk ? 'Спробуйте змінити пошуковий запит вище.' : 'Try changing the search query above.',
            alreadyInLibrary: isUk ? '✓ ВЖЕ В МЕДІАТЕЦІ' : '✓ ALREADY IN LIBRARY',
            unauthorized: isUk ? 'Потрібно знову увійти в обліковий запис Jellyfin.' : 'Please sign in to Jellyfin again.',
            forbidden: isUk ? 'Недостатньо прав для цієї дії.' : 'You do not have permission to do this.',
            searchFailed: isUk ? 'Не вдалося виконати пошук' : 'Search failed'
        };
    }

    // Inject styles
    var style = document.createElement('style');
    style.id = 't2j-search-styles';
    style.textContent = `
        .t2j-search-container {
            margin: 2em 0;
            padding: 0 1.5em;
            animation: t2jFadeIn 0.3s ease;
        }
        @keyframes t2jFadeIn {
            from { opacity: 0; transform: translateY(6px); }
            to { opacity: 1; transform: translateY(0); }
        }
        .t2j-search-header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            flex-wrap: wrap;
            gap: 10px;
            margin-bottom: 1.2em;
            padding-bottom: 0.8em;
            border-bottom: 1px solid rgba(255, 255, 255, 0.08);
        }
        .t2j-search-title-wrap {
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .t2j-search-icon {
            width: 32px;
            height: 32px;
            border-radius: 8px;
            background: linear-gradient(135deg, rgba(16, 185, 129, 0.2), rgba(6, 95, 70, 0.3));
            color: #10b981;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 16px;
            font-weight: bold;
            border: 1px solid rgba(16, 185, 129, 0.3);
        }
        .t2j-search-title {
            font-size: 1.3em;
            font-weight: 700;
            color: #fff;
            margin: 0;
        }
        .t2j-search-subtitle {
            font-size: 0.85em;
            color: #94a3b8;
            margin-top: 3px;
        }
        .t2j-search-badge {
            font-size: 0.75em;
            background: rgba(16, 185, 129, 0.15);
            color: #34d399;
            padding: 4px 10px;
            border-radius: 20px;
            font-weight: 600;
            border: 1px solid rgba(16, 185, 129, 0.3);
        }
        .t2j-filter-chips {
            display: inline-flex;
            background: rgba(15, 23, 42, 0.8);
            border: 1px solid rgba(255, 255, 255, 0.12);
            border-radius: 8px;
            padding: 2px;
            gap: 2px;
        }
        .t2j-chip {
            background: transparent;
            color: #94a3b8;
            border: none;
            padding: 3px 9px;
            border-radius: 6px;
            font-size: 0.72em;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.2s ease;
        }
        .t2j-chip:hover {
            color: #f1f5f9;
        }
        .t2j-chip.active {
            background: #334155;
            color: #ffffff;
            box-shadow: 0 1px 3px rgba(0,0,0,0.3);
        }
        .t2j-cards-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
            gap: 14px;
        }
        .t2j-card {
            background: linear-gradient(145deg, #1e293b, #0f172a);
            border: 1px solid rgba(255, 255, 255, 0.08);
            border-radius: 12px;
            padding: 14px;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            transition: transform 0.2s ease, border-color 0.2s ease, box-shadow 0.2s ease;
            position: relative;
            overflow: hidden;
        }
        .t2j-card:hover {
            transform: translateY(-2px);
            border-color: rgba(16, 185, 129, 0.4);
            box-shadow: 0 8px 24px -4px rgba(0, 0, 0, 0.5), 0 0 12px rgba(16, 185, 129, 0.15);
        }
        .t2j-card-meta {
            display: flex;
            flex-wrap: wrap;
            gap: 6px;
            align-items: center;
            margin-bottom: 8px;
        }
        .t2j-badge {
            font-size: 0.7em;
            padding: 2px 7px;
            border-radius: 4px;
            font-weight: 600;
            line-height: 1.4;
            display: inline-flex;
            align-items: center;
        }
        .t2j-badge-tracker {
            background: rgba(99, 102, 241, 0.15);
            color: #a5b4fc;
            border: 1px solid rgba(99, 102, 241, 0.25);
        }
        .t2j-badge-gold {
            background: linear-gradient(135deg, rgba(245, 158, 11, 0.2), rgba(217, 119, 6, 0.3));
            color: #fbbf24;
            border: 1px solid rgba(245, 158, 11, 0.4);
        }
        .t2j-badge-silver {
            background: rgba(148, 163, 184, 0.2);
            color: #e2e8f0;
            border: 1px solid rgba(148, 163, 184, 0.4);
        }
        .t2j-badge-bronze {
            background: rgba(234, 88, 12, 0.2);
            color: #fdba74;
            border: 1px solid rgba(234, 88, 12, 0.4);
        }
        .t2j-badge-author {
            background: rgba(168, 85, 247, 0.2);
            color: #d8b4fe;
            border: 1px solid rgba(168, 85, 247, 0.4);
        }
        .t2j-badge-exclusive {
            background: rgba(244, 63, 94, 0.2);
            color: #fda4af;
            border: 1px solid rgba(244, 63, 94, 0.4);
        }
        .t2j-badge-double {
            background: rgba(16, 185, 129, 0.2);
            color: #6ee7b7;
            border: 1px solid rgba(16, 185, 129, 0.4);
        }
        .t2j-badge-seeders {
            background: rgba(34, 197, 94, 0.15);
            color: #4ade80;
            border: 1px solid rgba(34, 197, 94, 0.25);
        }
        .t2j-badge-size {
            background: rgba(148, 163, 184, 0.1);
            color: #cbd5e1;
            border: 1px solid rgba(148, 163, 184, 0.2);
        }
        .t2j-card-title {
            font-size: 0.9em;
            font-weight: 600;
            color: #f1f5f9;
            line-height: 1.35;
            display: -webkit-box;
            -webkit-line-clamp: 2;
            -webkit-box-orient: vertical;
            overflow: hidden;
            margin-bottom: 12px;
            word-break: break-word;
        }
        .t2j-card-footer {
            display: flex;
            flex-direction: column;
            gap: 8px;
            margin-top: auto;
            padding-top: 10px;
            border-top: 1px solid rgba(255, 255, 255, 0.08);
        }
        .t2j-card-cat-wrap {
            display: flex;
            flex-direction: column;
            gap: 3px;
        }
        .t2j-card-cat-label {
            font-size: 0.72em;
            color: #94a3b8;
            font-weight: 600;
        }
        .t2j-card-cat-select {
            width: 100%;
            background: #0f172a;
            color: #f1f5f9;
            border: 1px solid rgba(255, 255, 255, 0.15);
            border-radius: 6px;
            padding: 5px 8px;
            font-size: 0.75em;
            font-weight: 500;
            outline: none;
            cursor: pointer;
            box-sizing: border-box;
        }
        .t2j-card-cat-select:focus {
            border-color: #10b981;
        }
        .t2j-add-btn {
            background: linear-gradient(135deg, #10b981, #059669);
            color: #ffffff;
            border: none;
            padding: 7px 14px;
            border-radius: 8px;
            font-size: 0.8em;
            font-weight: 600;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            transition: all 0.2s ease;
            box-shadow: 0 2px 6px rgba(16, 185, 129, 0.3);
            width: 100%;
            justify-content: center;
        }
        .t2j-add-btn:hover:not(:disabled) {
            background: linear-gradient(135deg, #059669, #047857);
            transform: translateY(-1px);
            box-shadow: 0 4px 12px rgba(16, 185, 129, 0.4);
        }
        .t2j-add-btn:disabled {
            opacity: 0.6;
            cursor: not-allowed;
        }
        .t2j-add-btn.success {
            background: #047857;
            cursor: default;
        }
        .t2j-status-spinner {
            width: 14px;
            height: 14px;
            border: 2px solid rgba(255, 255, 255, 0.3);
            border-top-color: #ffffff;
            border-radius: 50%;
            animation: t2jSpin 0.8s linear infinite;
        }
        @keyframes t2jSpin {
            to { transform: rotate(360deg); }
        }
        .t2j-toast {
            position: fixed;
            bottom: 30px;
            right: 30px;
            background: #0f172a;
            border: 1px solid #10b981;
            color: #f8fafc;
            padding: 12px 20px;
            border-radius: 10px;
            box-shadow: 0 10px 25px rgba(0, 0, 0, 0.6);
            font-size: 13px;
            font-weight: 500;
            z-index: 999999;
            display: flex;
            align-items: center;
            gap: 10px;
            animation: t2jSlideIn 0.3s ease;
        }
        @keyframes t2jSlideIn {
            from { transform: translateY(20px); opacity: 0; }
            to { transform: translateY(0); opacity: 1; }
        }
        /* Drawer & Modal Styles */
        .t2j-drawer-backdrop {
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background: rgba(0, 0, 0, 0.7);
            backdrop-filter: blur(4px);
            z-index: 99998;
            animation: t2jFadeIn 0.2s ease;
        }
        .t2j-drawer {
            position: fixed;
            top: 0;
            right: 0;
            bottom: 0;
            width: 580px;
            max-width: 95vw;
            background: #090d16;
            border-left: 1px solid rgba(255, 255, 255, 0.12);
            box-shadow: -10px 0 35px rgba(0, 0, 0, 0.8);
            z-index: 99999;
            display: flex;
            flex-direction: column;
            animation: t2jDrawerSlide 0.25s cubic-bezier(0.16, 1, 0.3, 1);
            color: #f1f5f9;
            font-family: inherit;
        }
        @keyframes t2jDrawerSlide {
            from { transform: translateX(100%); }
            to { transform: translateX(0); }
        }
        .t2j-drawer-header {
            padding: 18px 22px;
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            display: flex;
            align-items: center;
            justify-content: space-between;
            background: #0f172a;
        }
        .t2j-drawer-title {
            font-size: 1.15em;
            font-weight: 700;
            color: #fff;
            margin: 0;
            display: flex;
            align-items: center;
            gap: 8px;
        }
        .t2j-drawer-subtitle {
            font-size: 0.8em;
            color: #94a3b8;
            margin-top: 3px;
        }
        .t2j-drawer-close {
            background: transparent;
            border: none;
            color: #94a3b8;
            font-size: 22px;
            cursor: pointer;
            line-height: 1;
            padding: 4px 8px;
            border-radius: 6px;
            transition: all 0.2s;
        }
        .t2j-drawer-close:hover {
            color: #fff;
            background: rgba(255, 255, 255, 0.1);
        }
        .t2j-drawer-tabs {
            display: flex;
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            background: #0b1120;
            padding: 0 16px;
        }
        .t2j-drawer-tab {
            padding: 12px 18px;
            font-size: 0.88em;
            font-weight: 600;
            color: #94a3b8;
            border: none;
            background: transparent;
            cursor: pointer;
            border-bottom: 2px solid transparent;
            transition: all 0.2s;
        }
        .t2j-drawer-tab:hover {
            color: #f1f5f9;
        }
        .t2j-drawer-tab.active {
            color: #10b981;
            border-bottom-color: #10b981;
        }
        .t2j-drawer-body {
            flex: 1;
            overflow-y: auto;
            padding: 20px;
            display: flex;
            flex-direction: column;
            gap: 16px;
        }
        .t2j-stream-item {
            background: #1e293b;
            border: 1px solid rgba(255, 255, 255, 0.08);
            border-radius: 10px;
            padding: 14px 16px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 12px;
            transition: border-color 0.2s;
        }
        .t2j-stream-item.primary {
            border-color: rgba(16, 185, 129, 0.4);
            background: linear-gradient(135deg, rgba(16, 185, 129, 0.08), #1e293b);
        }
        .t2j-stream-info {
            display: flex;
            flex-direction: column;
            gap: 4px;
            flex: 1;
            overflow: hidden;
        }
        .t2j-stream-label {
            font-size: 0.92em;
            font-weight: 700;
            color: #f8fafc;
            word-break: break-all;
        }
        .t2j-stream-sub {
            font-size: 0.75em;
            color: #94a3b8;
            display: flex;
            align-items: center;
            gap: 8px;
            flex-wrap: wrap;
        }
        .t2j-stream-del-btn {
            background: rgba(239, 68, 68, 0.15);
            border: 1px solid rgba(239, 68, 68, 0.3);
            color: #f87171;
            border-radius: 6px;
            padding: 6px 10px;
            font-size: 0.8em;
            cursor: pointer;
            transition: all 0.2s;
        }
        .t2j-stream-del-btn:hover {
            background: #ef4444;
            color: #fff;
        }
        .t2j-drawer-search-bar {
            display: flex;
            gap: 8px;
            margin-bottom: 8px;
        }
        .t2j-drawer-input {
            flex: 1;
            background: #1e293b;
            border: 1px solid rgba(255, 255, 255, 0.15);
            border-radius: 8px;
            color: #fff;
            padding: 8px 12px;
            font-size: 0.88em;
            outline: none;
        }
        .t2j-drawer-input:focus {
            border-color: #10b981;
        }
        .t2j-drawer-search-btn {
            background: #10b981;
            color: #fff;
            border: none;
            border-radius: 8px;
            padding: 0 16px;
            font-weight: 600;
            font-size: 0.85em;
            cursor: pointer;
        }
        .t2j-card-actions {
            display: flex;
            gap: 8px;
            margin-top: 8px;
        }
        .t2j-btn-secondary {
            background: rgba(255, 255, 255, 0.1);
            border: 1px solid rgba(255, 255, 255, 0.15);
            color: #cbd5e1;
            padding: 6px 12px;
            border-radius: 6px;
            font-size: 0.78em;
            font-weight: 600;
            cursor: pointer;
            flex: 1;
            text-align: center;
            transition: all 0.2s;
        }
        .t2j-btn-secondary:hover {
            background: rgba(255, 255, 255, 0.18);
            color: #fff;
        }
        .t2j-item-streams-btn .detailButton-content {
            display: flex !important;
            flex-direction: row !important;
            align-items: center !important;
            justify-content: center !important;
            gap: 2px !important;
        }
        .t2j-stream-count {
            display: none;
            font-size: 0.72em;
            font-weight: 700;
            background: rgba(128, 128, 128, 0.25);
            color: inherit;
            padding: 1px 5px;
            border-radius: 999px;
            line-height: 1.2;
            letter-spacing: -0.2px;
            vertical-align: middle;
        }
        .t2j-item-streams-btn:hover .t2j-stream-count {
            background: rgba(128, 128, 128, 0.35);
        }
    `;
    document.head.appendChild(style);

    function showToast(message, isSuccess) {
        var existing = document.querySelector('.t2j-toast');
        if (existing) existing.remove();

        var toast = document.createElement('div');
        toast.className = 't2j-toast';
        if (!isSuccess) toast.style.borderColor = '#ef4444';
        toast.innerHTML = (isSuccess ? '<span style="color:#10b981;">✓</span> ' : '<span style="color:#ef4444;">⚠</span> ') + escapeHtml(message);
        document.body.appendChild(toast);
        setTimeout(function () {
            toast.style.transition = 'opacity 0.5s ease';
            toast.style.opacity = '0';
            setTimeout(function () { toast.remove(); }, 500);
        }, 4000);
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

    // Cached item type lookup so the Streams button only shows on Movie items.
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

    // true = show the Streams button, false = never for this item/user, null = still resolving.
    function isStreamsButtonEligible(itemId) {
        var isAdmin = getIsAdminCached();
        if (isAdmin === false) return false;
        if (isAdmin === null) return null;
        var type = getItemTypeCached(itemId);
        if (type === null) return null;
        return type === 'Movie';
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
                removeResultsContainer();
                var authMsg = authErrorMessage(res.status, getI18n());
                if (authMsg) showToast(authMsg, false);
                return;
            }

            var results = await res.json();
            if (myRequestId !== searchRequestId) return;
            await renderSearchResults(query, results || [], myRequestId);
        } catch (err) {
            if (err.name !== 'AbortError' && myRequestId === searchRequestId) {
                console.error('[Torr2Jelly] Search error:', err);
                removeResultsContainer();
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

    function renderLoadingIndicator(query) {
        var target = getSearchTargetContainer();
        if (!target) return;

        var i18n = getI18n();
        var existing = document.getElementById('t2j-search-section');
        if (!existing) {
            existing = document.createElement('div');
            existing.id = 't2j-search-section';
            existing.className = 't2j-search-container';
            if (target.before) {
                target.parent.insertBefore(existing, target.before);
            } else {
                target.parent.appendChild(existing);
            }
        }

        existing.innerHTML = `
            <div class="t2j-search-header">
                <div class="t2j-search-title-wrap">
                    <div class="t2j-search-icon">⚡</div>
                    <div>
                        <h3 class="t2j-search-title">${i18n.title}</h3>
                        <div class="t2j-search-subtitle">
                            <span class="t2j-status-spinner" style="margin-right: 5px;"></span>
                            ${i18n.searching} "${escapeHtml(query)}"...
                        </div>
                    </div>
                </div>
            </div>
        `;
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

    async function renderSearchResults(query, results, requestId) {
        var i18n = getI18n();
        if (requestId !== undefined && requestId !== searchRequestId) return;
        if (!results || results.length === 0) {
            var target = getSearchTargetContainer();
            if (target && target.mode === 'empty') {
                var existing = document.getElementById('t2j-search-section');
                if (existing) {
                    existing.innerHTML = `
                        <div class="t2j-search-header">
                            <div class="t2j-search-title-wrap">
                                <div class="t2j-search-icon">⚡</div>
                                <div>
                                    <h3 class="t2j-search-title">${i18n.title}</h3>
                                    <div class="t2j-search-subtitle">0 ${i18n.releasesFound} "${escapeHtml(query)}"</div>
                                </div>
                            </div>
                        </div>
                    `;
                }
            } else {
                removeResultsContainer();
            }
            return;
        }

        var target = getSearchTargetContainer();
        if (!target) return;

        var container = document.getElementById('t2j-search-section');
        if (!container) {
            container = document.createElement('div');
            container.id = 't2j-search-section';
            container.className = 't2j-search-container';
            if (target.before) {
                target.parent.insertBefore(container, target.before);
            } else {
                target.parent.appendChild(container);
            }
        }

        window._t2jLastQuery = query;
        window._t2jLastResults = results || [];
        if (!window._t2jCurrentFilter) window._t2jCurrentFilter = 'all';

        var taggedResults = results.filter(function (r) {
            return r.IsGold || (r.Tags && r.Tags.length > 0);
        });

        var activeList = (window._t2jCurrentFilter === 'gold') ? taggedResults : results;
        var displayResults = activeList;

        var headerHtml = `
            <div class="t2j-search-header">
                <div class="t2j-search-title-wrap">
                    <div class="t2j-search-icon">⚡</div>
                    <div>
                        <h3 class="t2j-search-title">${target.mode === 'empty' ? i18n.notFoundTitle : i18n.foundTitle}</h3>
                        <div class="t2j-search-subtitle">${i18n.subtitle}</div>
                    </div>
                </div>
                <div style="display:flex; align-items:center; gap:8px;">
                    <div class="t2j-filter-chips">
                        <button type="button" class="t2j-chip ${window._t2jCurrentFilter === 'all' ? 'active' : ''}" onclick="window._t2jSetFilter && window._t2jSetFilter('all')">${i18n.filterAll} (${results.length})</button>
                        <button type="button" class="t2j-chip ${window._t2jCurrentFilter === 'gold' ? 'active' : ''}" onclick="window._t2jSetFilter && window._t2jSetFilter('gold')">${i18n.filterGold} (${taggedResults.length})</button>
                    </div>
                    <div class="t2j-search-badge">${activeList.length}</div>
                </div>
            </div>
        `;

        var libs = await getAvailableLibraries();
        // A newer search (or leaving the page) may have happened while the library list was loading.
        if (requestId !== undefined && requestId !== searchRequestId) return;
        var showLib = libs.find(function (l) {
            var n = (l.Name || '').toLowerCase();
            var t = (l.Type || '').toLowerCase();
            return t === 'show' || t === 'tvshows' || n.indexOf('show') !== -1 || n.indexOf('серіал') !== -1;
        });
        var movieLib = libs.find(function (l) {
            var n = (l.Name || '').toLowerCase();
            var t = (l.Type || '').toLowerCase();
            return t === 'movie' || n.indexOf('movie') !== -1 || n.indexOf('фільм') !== -1;
        });

        var cardsHtml = '<div class="t2j-cards-grid">';
        displayResults.forEach(function (item, idx) {
            var rawLink = pickLink(item);
            var title = item.Title || '';
            var size = item.SizeFormatted || '';
            var seeds = parseInt(item.Seeders, 10) || 0;
            var tracker = item.Indexer || 'Torrent';
            var isGold = !!(item.IsGold || item.isGold);
            var isShow = isShowTitle(title);

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

            var tagBadgesHtml = '';
            rawTags.forEach(function (tg) {
                var ltg = String(tg || '').toLowerCase();
                if (ltg === 'freeleech' || ltg === 'gold') {
                    tagBadgesHtml += '<span class="t2j-badge t2j-badge-gold">' + i18n.goldBadge + '</span>';
                } else if (ltg === 'halfleech' || ltg === 'silver') {
                    tagBadgesHtml += '<span class="t2j-badge t2j-badge-silver">' + i18n.silverBadge + '</span>';
                } else if (ltg === 'bronze') {
                    tagBadgesHtml += '<span class="t2j-badge t2j-badge-bronze">' + i18n.bronzeBadge + '</span>';
                } else if (ltg === 'internal' || ltg === 'author' || ltg.indexOf('автор') !== -1) {
                    tagBadgesHtml += '<span class="t2j-badge t2j-badge-author">' + i18n.authorBadge + '</span>';
                } else if (ltg === 'exclusive' || ltg.indexOf('ексклюзив') !== -1) {
                    tagBadgesHtml += '<span class="t2j-badge t2j-badge-exclusive">' + i18n.exclusiveBadge + '</span>';
                } else if (ltg === 'doubleupload' || ltg === '2xupload') {
                    tagBadgesHtml += '<span class="t2j-badge t2j-badge-double">🚀 2x</span>';
                }
            });

            var targetLibName = isShow ? (showLib ? showLib.Name : 'Shows') : (movieLib ? movieLib.Name : 'Movies');
            var cardCatId = 't2jGlobalCat_' + idx;
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

            cardsHtml += `
                <div class="t2j-card">
                    <div>
                        <div class="t2j-card-meta">
                            <span class="t2j-badge t2j-badge-tracker">${escapeHtml(tracker)}</span>
                            ${tagBadgesHtml}
                            <span class="t2j-badge t2j-badge-seeders">▲ ${seeds}</span>
                            <span class="t2j-badge t2j-badge-size">${escapeHtml(size)}</span>
                            ${isShow ? '<span class="t2j-badge t2j-badge-tracker" style="color: #67e8f9; border-color: rgba(6, 182, 212, 0.4);">' + escapeHtml(i18n.seriesBadge) + '</span>' : ''}
                        </div>
                        <div class="t2j-card-title" title="${escapeHtml(title)}">${escapeHtml(title)}</div>
                    </div>
                    <div class="t2j-card-footer">
                        <div class="t2j-card-cat-wrap">
                            <span class="t2j-card-cat-label">${escapeHtml(i18n.categoryLabel)}</span>
                            <select class="t2j-card-cat-select" id="${cardCatId}">
                                ${catOptionsHtml}
                            </select>
                        </div>
                        <button type="button" class="t2j-add-btn" data-cat-id="${cardCatId}" data-link="${encodeURIComponent(rawLink)}" data-title="${encodeURIComponent(title)}" data-poster="${encodeURIComponent(item.PosterUrl || '')}" data-tmdb="${parseInt(item.TmdbId, 10) || 0}">
                            <span>${i18n.addBtn}</span>
                        </button>
                    </div>
                </div>
            `;
        });
        cardsHtml += '</div>';

        container.innerHTML = headerHtml + cardsHtml;

        // Attach click handlers to add buttons
        var buttons = container.querySelectorAll('.t2j-add-btn');
        buttons.forEach(function (btn) {
            btn.onclick = async function () {
                var link = decodeURIComponent(btn.getAttribute('data-link') || '');
                var itemTitle = decodeURIComponent(btn.getAttribute('data-title') || '');
                var posterUrl = decodeURIComponent(btn.getAttribute('data-poster') || '');
                var tmdbId = parseInt(btn.getAttribute('data-tmdb') || '0', 10);
                var catId = btn.getAttribute('data-cat-id');
                var catSelect = catId ? document.getElementById(catId) : null;

                if (!link) return;

                // Pick selected category from dropdown, fallback to auto-detection
                var isShow = isShowTitle(itemTitle);
                var category = (catSelect && catSelect.value) ? catSelect.value : (isShow ? 'Shows' : 'Movies');

                btn.disabled = true;
                btn.innerHTML = '<span class="t2j-status-spinner"></span> <span>' + i18n.adding + '</span>';

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
                        btn.className = 't2j-add-btn success';
                        btn.innerHTML = '<span>' + i18n.added + '</span>';
                        showToast('✓ "' + itemTitle + '" ' + (pending ? i18n.addedPendingToast : i18n.addedToast), true);

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
                        btn.disabled = false;
                        btn.innerHTML = '<span>' + i18n.serverError + '</span>';
                        showToast(authErrorMessage(res.status, i18n) ? errMsg : (i18n.failedResponse + ': ' + errMsg), false);
                    }
                } catch (e) {
                    btn.disabled = false;
                    btn.innerHTML = '<span>' + i18n.serverError + '</span>';
                    showToast(i18n.connError + ': ' + e.message, false);
                }
            };
        });
    }

    window._t2jSetFilter = function (filterMode) {
        window._t2jCurrentFilter = filterMode;
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

        if (!searchInput._t2jBound) {
            searchInput._t2jBound = true;
            searchInput.addEventListener('input', function () {
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
    // Multi-Version & Alternate Streams Drawer UI
    // ==========================================
    var _drawerData = null;
    var _activeDrawerTab = 'streams'; // 'streams' or 'search'
    var _drawerSearchFilter = 'all';
    var _drawerToken = 0;        // bumped every time a drawer is opened or closed
    var _drawerItemId = null;    // item the current drawer belongs to

    async function openStreamsDrawer(itemId) {
        var existing = document.getElementById('t2j-drawer-root');
        if (existing) existing.remove();

        // Fresh drawer state for every open, so data/tab/filter of a previous item can never leak into
        // this one, and responses that arrive for a previous open are ignored.
        var myToken = ++_drawerToken;
        _drawerItemId = itemId;
        _drawerData = null;
        _activeDrawerTab = 'streams';
        _drawerSearchFilter = 'all';

        var root = document.createElement('div');
        root.id = 't2j-drawer-root';

        var backdrop = document.createElement('div');
        backdrop.className = 't2j-drawer-backdrop';
        function closeDrawer() {
            root.remove();
            if (_drawerToken === myToken) {
                _drawerToken++;
                _drawerItemId = null;
                _drawerData = null;
            }
            refreshStreamsButtonCount(itemId);
        }
        backdrop.onclick = closeDrawer;

        var drawer = document.createElement('div');
        drawer.className = 't2j-drawer';

        var i18n = getI18n();

        drawer.innerHTML = `
            <div class="t2j-drawer-header">
                <div>
                    <h3 class="t2j-drawer-title">⚡ ${i18n.drawerTitle}</h3>
                    <div class="t2j-drawer-subtitle" id="t2j-drawer-item-title">${i18n.searching}...</div>
                </div>
                <button type="button" class="t2j-drawer-close" id="t2j-drawer-close-btn">&times;</button>
            </div>
            <div class="t2j-drawer-tabs">
                <button type="button" class="t2j-drawer-tab active" id="t2j-tab-streams">${i18n.activeStreamsTab}</button>
                <button type="button" class="t2j-drawer-tab" id="t2j-tab-search">${i18n.searchAlternativesTab}</button>
            </div>
            <div class="t2j-drawer-body" id="t2j-drawer-body">
                <div style="text-align: center; padding: 40px 0; color: #94a3b8;">
                    <div class="t2j-status-spinner" style="margin: 0 auto 12px; width: 24px; height: 24px;"></div>
                    <div>${i18n.searching}...</div>
                </div>
            </div>
        `;

        root.appendChild(backdrop);
        root.appendChild(drawer);
        document.body.appendChild(root);

        document.getElementById('t2j-drawer-close-btn').onclick = closeDrawer;

        var tabStreams = document.getElementById('t2j-tab-streams');
        var tabSearch = document.getElementById('t2j-tab-search');

        tabStreams.onclick = function () {
            _activeDrawerTab = 'streams';
            tabStreams.className = 't2j-drawer-tab active';
            tabSearch.className = 't2j-drawer-tab';
            renderDrawerContent(itemId);
        };

        tabSearch.onclick = function () {
            _activeDrawerTab = 'search';
            tabSearch.className = 't2j-drawer-tab active';
            tabStreams.className = 't2j-drawer-tab';
            renderDrawerContent(itemId);
        };

        // Fetch alternatives and current streams
        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/Alternatives'), { headers: getAuthHeaders() });
            if (myToken !== _drawerToken) return;
            if (res.ok) {
                var loaded = await res.json();
                if (myToken !== _drawerToken) return;
                _drawerData = loaded;
                var titleEl = document.getElementById('t2j-drawer-item-title');
                if (titleEl) {
                    titleEl.textContent = _drawerData.Title + (_drawerData.Year ? ' (' + _drawerData.Year + ')' : '');
                }
                var streamsCount = (_drawerData.ExistingStreams || []).length;
                tabStreams.textContent = i18n.activeStreamsTab + ' (' + streamsCount + ')';
                var altsCount = (_drawerData.Alternatives || []).length;
                tabSearch.textContent = i18n.searchAlternativesTab + ' (' + altsCount + ')';

                renderDrawerContent(itemId);
            } else {
                var failBody = document.getElementById('t2j-drawer-body');
                var failMsg = authErrorMessage(res.status, i18n) || i18n.failedResponse;
                if (failBody) {
                    failBody.innerHTML = '<div style="color: #ef4444; padding: 20px;">' + escapeHtml(failMsg) + '</div>';
                }
                if (authErrorMessage(res.status, i18n)) showToast(failMsg, false);
            }
        } catch (e) {
            if (myToken !== _drawerToken) return;
            var errBody = document.getElementById('t2j-drawer-body');
            if (errBody) {
                errBody.innerHTML = '<div style="color: #ef4444; padding: 20px;">' + escapeHtml(i18n.connError + ': ' + (e && e.message ? e.message : e)) + '</div>';
            }
        }
    }

    function renderDrawerContent(itemId) {
        var body = document.getElementById('t2j-drawer-body');
        if (!body || !_drawerData || _drawerItemId !== itemId) return;

        var i18n = getI18n();

        if (_activeDrawerTab === 'streams') {
            var streams = _drawerData.ExistingStreams || [];
            if (streams.length === 0) {
                body.innerHTML = `
                    <div style="text-align: center; padding: 40px 20px; color: #94a3b8;">
                        <div style="font-size: 32px; margin-bottom: 10px;">📂</div>
                        <div style="font-size: 1.1em; font-weight: 600; color: #cbd5e1;">${i18n.noStreamsFound}</div>
                        <div style="font-size: 0.85em; margin-top: 6px;">${escapeHtml(i18n.noStreamsHint)}</div>
                    </div>
                `;
                return;
            }

            var html = '<div style="display: flex; flex-direction: column; gap: 10px;">';
            streams.forEach(function (s) {
                var isPrimary = s.IsPrimary;
                var shortHash = s.TorrentHash ? (s.TorrentHash.length > 8 ? s.TorrentHash.substring(0, 8) : s.TorrentHash) : '';

                html += `
                    <div class="t2j-stream-item ${isPrimary ? 'primary' : ''}">
                        <div class="t2j-stream-info">
                            <div style="display: flex; align-items: center; gap: 6px; margin-bottom: 4px;">
                                <span class="t2j-badge ${isPrimary ? 't2j-badge-tracker' : 't2j-badge-silver'}" style="${isPrimary ? 'background: #059669; color: #fff;' : ''}">
                                    ${isPrimary ? i18n.currentBadge : i18n.versionBadge}
                                </span>
                                ${shortHash ? `<span class="t2j-badge t2j-badge-tracker" style="font-family: monospace;">#${escapeHtml(shortHash)}</span>` : ''}
                            </div>
                            <div class="t2j-stream-label">${escapeHtml(s.VersionLabel || s.Name)}</div>
                            <div class="t2j-stream-sub">
                                <span title="${escapeHtml(s.Path)}">${escapeHtml(s.Name)}</span>
                            </div>
                        </div>
                        ${s.TorrentHash ? `
                            <button type="button" class="t2j-stream-del-btn" title="${escapeHtml(i18n.deleteStreamTitle)}" data-hash="${escapeHtml(s.TorrentHash)}" data-name="${escapeHtml(s.VersionLabel || s.Name)}">
                                🗑️
                            </button>
                        ` : ''}
                    </div>
                `;
            });
            html += '</div>';
            body.innerHTML = html;

            body.querySelectorAll('.t2j-stream-del-btn').forEach(function (btn) {
                btn.onclick = async function () {
                    var hash = btn.getAttribute('data-hash');
                    var name = btn.getAttribute('data-name');
                    if (!hash) return;
                    if (!confirm(i18n.deleteStreamConfirm + '\n\n"' + name + '"')) return;

                    btn.disabled = true;
                    btn.innerHTML = '<span class="t2j-status-spinner"></span>';

                    try {
                        var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/Streams/' + encodeURIComponent(hash)), {
                            method: 'DELETE',
                            headers: getAuthHeaders()
                        });
                        if (res.ok) {
                            showToast('✓ ' + i18n.streamDeletedSuccess, true);
                            if (_drawerItemId === itemId) openStreamsDrawer(itemId);
                            refreshStreamsButtonCount(itemId);
                        } else {
                            btn.disabled = false;
                            btn.innerHTML = '🗑️';
                            showToast(authErrorMessage(res.status, i18n) || i18n.serverError, false);
                        }
                    } catch (e) {
                        btn.disabled = false;
                        btn.innerHTML = '🗑️';
                        showToast(i18n.connError, false);
                    }
                };
            });
        } else {
            // Tab Search
            var alts = _drawerData.Alternatives || [];
            var queryVal = _drawerData.SearchQuery || (_drawerData.OriginalTitle || _drawerData.Title || '');

            var taggedAlts = alts.filter(function (r) {
                return r.IsGold || (r.Tags && r.Tags.length > 0);
            });

            var activeList = (_drawerSearchFilter === 'gold') ? taggedAlts : alts;

            var html = `
                <div class="t2j-drawer-search-bar">
                    <input type="text" class="t2j-drawer-input" id="t2j-drawer-search-input" value="${escapeHtml(queryVal)}" placeholder="${escapeHtml(i18n.searchPlaceholder)}" />
                    <button type="button" class="t2j-drawer-search-btn" id="t2j-drawer-search-btn">${escapeHtml(i18n.findBtn)}</button>
                </div>
                <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px;">
                    <div class="t2j-filter-chips">
                        <button type="button" class="t2j-chip ${_drawerSearchFilter === 'all' ? 'active' : ''}" id="t2j-filter-all">${i18n.filterAll} (${alts.length})</button>
                        <button type="button" class="t2j-chip ${_drawerSearchFilter === 'gold' ? 'active' : ''}" id="t2j-filter-gold">${i18n.filterGold} (${taggedAlts.length})</button>
                    </div>
                    <div style="font-size: 0.78em; color: #94a3b8;">${activeList.length} ${i18n.releasesFound}</div>
                </div>
            `;

            if (activeList.length === 0) {
                html += `
                    <div style="text-align: center; padding: 40px 20px; color: #94a3b8;">
                        <div style="font-size: 32px; margin-bottom: 10px;">🔍</div>
                        <div style="font-size: 1.05em; font-weight: 600; color: #cbd5e1;">${escapeHtml(i18n.nothingFound)}</div>
                        <div style="font-size: 0.85em; margin-top: 6px;">${escapeHtml(i18n.nothingFoundHint)}</div>
                    </div>
                `;
            } else {
                html += '<div style="display: flex; flex-direction: column; gap: 12px;">';
                var existingStreams = _drawerData.ExistingStreams || [];
                var primaryStream = existingStreams.find(function (s) { return s.IsPrimary; }) || existingStreams[0];

                activeList.forEach(function (item, idx) {
                    var title = item.Title || '';
                    var rawLink = pickLink(item);
                    var size = item.SizeFormatted || '';
                    var seeds = parseInt(item.Seeders, 10) || 0;
                    var tracker = item.Indexer || 'Torrent';
                    var isGold = !!(item.IsGold || item.isGold);

                    var rawTags = (item.Tags || item.tags || []).slice();
                    var hasFreeleech = isGold || rawTags.some(function (tg) { return /freeleech|gold/i.test(tg); });

                    var tagBadgesHtml = '';
                    if (hasFreeleech) {
                        tagBadgesHtml += '<span class="t2j-badge t2j-badge-gold">' + i18n.goldBadge + '</span>';
                    }

                    var isAdded = existingStreams.some(function (es) {
                        return es.TorrentHash && item.InfoHash && es.TorrentHash.toLowerCase() === item.InfoHash.toLowerCase();
                    });

                    html += `
                        <div class="t2j-card" style="margin-bottom: 0;">
                            <div>
                                <div class="t2j-card-meta">
                                    <span class="t2j-badge t2j-badge-tracker">${escapeHtml(tracker)}</span>
                                    ${tagBadgesHtml}
                                    <span class="t2j-badge t2j-badge-seeders">▲ ${seeds}</span>
                                    <span class="t2j-badge t2j-badge-size">${escapeHtml(size)}</span>
                                    ${isAdded ? '<span class="t2j-badge" style="background: rgba(16, 185, 129, 0.2); color: #34d399;">' + escapeHtml(i18n.alreadyInLibrary) + '</span>' : ''}
                                </div>
                                <div class="t2j-card-title" title="${escapeHtml(title)}">${escapeHtml(title)}</div>
                            </div>
                            <div class="t2j-card-actions">
                                <button type="button" class="t2j-add-btn t2j-btn-add-version" data-idx="${idx}" data-link="${encodeURIComponent(rawLink)}" data-title="${encodeURIComponent(title)}" data-tracker="${escapeHtml(tracker)}" data-seeds="${seeds}">
                                    <span>${i18n.addAsVersionBtn}</span>
                                </button>
                                ${primaryStream && primaryStream.TorrentHash ? `
                                    <button type="button" class="t2j-btn-secondary t2j-btn-replace" data-idx="${idx}" data-link="${encodeURIComponent(rawLink)}" data-title="${encodeURIComponent(title)}" data-tracker="${escapeHtml(tracker)}" data-seeds="${seeds}" data-oldhash="${escapeHtml(primaryStream.TorrentHash)}">
                                        <span>${i18n.replaceStreamBtn}</span>
                                    </button>
                                ` : ''}
                            </div>
                        </div>
                    `;
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
                searchBtn.innerHTML = '<span class="t2j-status-spinner"></span>';
                try {
                    var sres = await fetch(getApiUrl('Plugins/Torr2Jelly/Search?query=' + encodeURIComponent(q)), { headers: getAuthHeaders() });
                    // The drawer was closed or re-opened for another item while we waited: drop the response.
                    if (drawerTokenAtRender !== _drawerToken || !_drawerData) return;
                    if (sres.ok) {
                        var alts = await sres.json();
                        if (drawerTokenAtRender !== _drawerToken || !_drawerData) return;
                        _drawerData.Alternatives = alts || [];
                        _drawerData.SearchQuery = q;
                        var tabSearch = document.getElementById('t2j-tab-search');
                        if (tabSearch) tabSearch.textContent = i18n.searchAlternativesTab + ' (' + _drawerData.Alternatives.length + ')';
                        renderDrawerContent(itemId);
                    } else {
                        showToast(authErrorMessage(sres.status, i18n) || i18n.searchFailed, false);
                    }
                } catch (e) {
                    showToast(i18n.connError, false);
                } finally {
                    searchBtn.disabled = false;
                    searchBtn.innerHTML = escapeHtml(i18n.findBtn);
                }
            }

            if (searchBtn) searchBtn.onclick = triggerDrawerSearch;
            if (inputEl) {
                inputEl.onkeydown = function (e) {
                    if (e.key === 'Enter') triggerDrawerSearch();
                };
            }

            var filterAll = document.getElementById('t2j-filter-all');
            var filterGold = document.getElementById('t2j-filter-gold');
            if (filterAll) {
                filterAll.onclick = function () {
                    _drawerSearchFilter = 'all';
                    renderDrawerContent(itemId);
                };
            }
            if (filterGold) {
                filterGold.onclick = function () {
                    _drawerSearchFilter = 'gold';
                    renderDrawerContent(itemId);
                };
            }

            body.querySelectorAll('.t2j-btn-add-version').forEach(function (btn) {
                btn.onclick = async function () {
                    var link = decodeURIComponent(btn.getAttribute('data-link') || '');
                    var title = decodeURIComponent(btn.getAttribute('data-title') || '');
                    var tracker = btn.getAttribute('data-tracker') || '';
                    var seeds = parseInt(btn.getAttribute('data-seeds') || '0', 10);
                    if (!link) return;

                    btn.disabled = true;
                    btn.innerHTML = '<span class="t2j-status-spinner"></span> <span>' + i18n.adding + '</span>';

                    try {
                        var payload = {
                            Link: link,
                            Tracker: tracker,
                            Seeders: seeds,
                            TorrentTitle: title,
                            ReplaceExisting: false
                        };

                        var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/AlternateStream'), {
                            method: 'POST',
                            headers: getAuthHeaders(),
                            body: JSON.stringify(payload)
                        });

                        if (res.ok) {
                            btn.className = 't2j-add-btn success';
                            btn.innerHTML = '<span>' + i18n.added + '</span>';
                            showToast('✓ ' + i18n.streamAddedSuccess, true);

                            // Reopen the drawer only if it is still showing this same item.
                            setTimeout(function () {
                                if (_drawerItemId === itemId) openStreamsDrawer(itemId);
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
                            btn.disabled = false;
                            btn.innerHTML = '<span>' + i18n.serverError + '</span>';
                            showToast(authErrorMessage(res.status, i18n) ? errMsg : (i18n.failedResponse + ': ' + errMsg), false);
                        }
                    } catch (e) {
                        btn.disabled = false;
                        btn.innerHTML = '<span>' + i18n.serverError + '</span>';
                        showToast(i18n.connError + ': ' + e.message, false);
                    }
                };
            });

            body.querySelectorAll('.t2j-btn-replace').forEach(function (btn) {
                btn.onclick = async function () {
                    var link = decodeURIComponent(btn.getAttribute('data-link') || '');
                    var title = decodeURIComponent(btn.getAttribute('data-title') || '');
                    var tracker = btn.getAttribute('data-tracker') || '';
                    var seeds = parseInt(btn.getAttribute('data-seeds') || '0', 10);
                    var oldHash = btn.getAttribute('data-oldhash') || '';
                    if (!link) return;

                    if (!confirm(i18n.replaceConfirm)) return;

                    btn.disabled = true;
                    btn.innerHTML = '<span class="t2j-status-spinner"></span> <span>' + i18n.adding + '</span>';

                    try {
                        var payload = {
                            Link: link,
                            Tracker: tracker,
                            Seeders: seeds,
                            TorrentTitle: title,
                            ReplaceExisting: true,
                            OldTorrentHash: oldHash
                        };

                        var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/AlternateStream'), {
                            method: 'POST',
                            headers: getAuthHeaders(),
                            body: JSON.stringify(payload)
                        });

                        if (res.ok) {
                            btn.style.background = '#047857';
                            btn.innerHTML = '<span>' + i18n.added + '</span>';
                            showToast('✓ ' + i18n.streamAddedSuccess, true);

                            // Reopen the drawer only if it is still showing this same item.
                            setTimeout(function () {
                                if (_drawerItemId === itemId) openStreamsDrawer(itemId);
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
                            btn.disabled = false;
                            btn.innerHTML = '<span>' + i18n.serverError + '</span>';
                            showToast(authErrorMessage(res.status, i18n) ? errMsg : (i18n.failedResponse + ': ' + errMsg), false);
                        }
                    } catch (e) {
                        btn.disabled = false;
                        btn.innerHTML = '<span>' + i18n.serverError + '</span>';
                        showToast(i18n.connError + ': ' + e.message, false);
                    }
                };
            });
        }
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

    async function updateStreamsButtonCount(btn, itemId) {
        if (!btn || !itemId) return;
        var countEl = btn.querySelector('.t2j-stream-count');
        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Items/' + encodeURIComponent(itemId) + '/Streams'), { headers: getAuthHeaders() });
            if (res.ok) {
                var streams = await res.json();
                var i18n = getI18n();
                if (Array.isArray(streams) && streams.length > 0) {
                    if (countEl) {
                        countEl.textContent = streams.length;
                        countEl.style.display = 'inline-flex';
                    }
                    btn.setAttribute('title', i18n.drawerTitle + ' (' + streams.length + ')');
                } else {
                    if (countEl) {
                        countEl.textContent = '';
                        countEl.style.display = 'none';
                    }
                    btn.setAttribute('title', i18n.drawerTitle);
                }
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
    function ensureButtonInContainer(page, buttonsContainer, itemId) {
        if (!buttonsContainer) return;

        var existingBtn = buttonsContainer.querySelector('.t2j-item-streams-btn');
        if (existingBtn) {
            if (existingBtn.querySelector('.detailButton-text')) {
                existingBtn.remove();
            } else {
                if (existingBtn._t2jItemId !== itemId) {
                    existingBtn._t2jItemId = itemId;
                    existingBtn.onclick = function (e) {
                        e.preventDefault();
                        e.stopPropagation();
                        openStreamsDrawer(itemId);
                    };
                    updateStreamsButtonCount(existingBtn, itemId);
                }
                return;
            }
        }

        injectStreamsButton(page, buttonsContainer, itemId);

        if (!buttonsContainer._t2jObserverAttached) {
            buttonsContainer._t2jObserverAttached = true;
            if (_buttonsObserver) {
                try { _buttonsObserver.disconnect(); } catch (e) {}
            }
            _buttonsObserver = new MutationObserver(function () {
                var currentPage = getActiveItemDetailPage();
                if (!currentPage) return;
                var currentId = getActiveItemId(currentPage);
                var curContainer = currentPage.querySelector('.mainDetailButtons');
                if (curContainer && currentId && !curContainer.querySelector('.t2j-item-streams-btn')
                    && isStreamsButtonEligible(currentId) === true) {
                    injectStreamsButton(currentPage, curContainer, currentId);
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

        // Streams management is admin-only on the server and only meaningful for movies, so only
        // admins get the button and only on Movie items. null = still resolving (admin/type lookup).
        var eligible = isStreamsButtonEligible(itemId);
        if (eligible === null) return;
        if (eligible === false) {
            page.querySelectorAll('.t2j-item-streams-btn').forEach(function (b) { b.remove(); });
            return;
        }

        allContainers.forEach(function (container) {
            ensureButtonInContainer(page, container, itemId);
        });
    }

    async function injectStreamsButton(page, buttonsContainer, itemId) {
        var i18n = getI18n();
        var btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.type = 'button';
        btn.className = 'button-flat detailButton emby-button t2j-item-streams-btn';
        btn.setAttribute('title', i18n.drawerTitle);
        btn._t2jItemId = itemId;

        btn.innerHTML = `
            <div class="detailButton-content" style="flex-direction: row; align-items: center; justify-content: center; gap: 2px;">
                <span class="material-icons detailButton-icon" aria-hidden="true">bolt</span>
                <span class="t2j-stream-count" style="display: none; font-size: 0.72em; font-weight: 700; background: rgba(128, 128, 128, 0.25); color: inherit; padding: 1px 5px; border-radius: 999px; line-height: 1.2; margin-left: 2px;"></span>
            </div>
        `;

        btn.onclick = function (e) {
            e.preventDefault();
            e.stopPropagation();
            openStreamsDrawer(btn._t2jItemId || itemId);
        };

        buttonsContainer.appendChild(btn);
        updateStreamsButtonCount(btn, itemId);
    }

    // Page watchers
    function checkAllPages() {
        checkSearchPage();
        checkItemDetailPage();
    }

    document.addEventListener('viewshow', checkAllPages);
    document.addEventListener('pageshow', checkAllPages);
    window.addEventListener('hashchange', function () {
        // Navigating away from the search route: forget the previous query/results immediately.
        if ((window.location.hash || '').indexOf('/search') === -1) {
            resetSearchState();
        }
        setTimeout(checkAllPages, 50);
        setTimeout(checkAllPages, 250);
        setTimeout(checkAllPages, 600);
    });
    window.addEventListener('popstate', checkAllPages);
    setInterval(function () { if (!document.hidden) checkAllPages(); }, 400);
    setTimeout(checkAllPages, 100);
    setTimeout(checkAllPages, 500);
})();
