(function () {
    'use strict';

    if (window._t2jSearchInitialized) return;
    window._t2jSearchInitialized = true;

    console.log('[Torr2Jelly] Search integration script loaded');

    var currentQuery = '';
    var debounceTimer = null;
    var abortController = null;
    var isSearching = false;

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
            margin-top: 2px;
        }
        .t2j-search-badge {
            background: rgba(16, 185, 129, 0.15);
            color: #34d399;
            border: 1px solid rgba(16, 185, 129, 0.3);
            border-radius: 9999px;
            padding: 2px 10px;
            font-size: 0.75em;
            font-weight: 600;
        }
        .t2j-cards-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(290px, 1fr));
            gap: 14px;
        }
        .t2j-card {
            background: rgba(30, 41, 59, 0.6);
            backdrop-filter: blur(8px);
            -webkit-backdrop-filter: blur(8px);
            border: 1px solid rgba(255, 255, 255, 0.07);
            border-radius: 12px;
            padding: 14px;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            transition: all 0.2s ease;
            position: relative;
            overflow: hidden;
        }
        .t2j-card:hover {
            transform: translateY(-2px);
            border-color: rgba(16, 185, 129, 0.35);
            box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.5), 0 0 15px rgba(16, 185, 129, 0.1);
            background: rgba(30, 41, 59, 0.8);
        }
        .t2j-card-meta {
            display: flex;
            align-items: center;
            gap: 6px;
            flex-wrap: wrap;
            margin-bottom: 8px;
        }
        .t2j-badge {
            font-size: 10px;
            font-weight: 600;
            padding: 2px 6px;
            border-radius: 5px;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }
        .t2j-badge-tracker {
            background: rgba(59, 130, 246, 0.15);
            color: #60a5fa;
            border: 1px solid rgba(59, 130, 246, 0.3);
        }
        .t2j-badge-gold {
            background: rgba(245, 158, 11, 0.15);
            color: #fbbf24;
            border: 1px solid rgba(245, 158, 11, 0.3);
        }
        .t2j-badge-seeders {
            background: rgba(16, 185, 129, 0.15);
            color: #34d399;
            border: 1px solid rgba(16, 185, 129, 0.3);
        }
        .t2j-badge-size {
            background: rgba(148, 163, 184, 0.15);
            color: #cbd5e1;
            border: 1px solid rgba(148, 163, 184, 0.25);
        }
        .t2j-card-title {
            color: #f8fafc;
            font-weight: 600;
            font-size: 0.95em;
            line-height: 1.35;
            margin-bottom: 12px;
            word-break: break-word;
            display: -webkit-box;
            -webkit-line-clamp: 2;
            -webkit-box-orient: vertical;
            overflow: hidden;
        }
        .t2j-card-footer {
            margin-top: auto;
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 10px;
            padding-top: 10px;
            border-top: 1px solid rgba(255, 255, 255, 0.05);
        }
        .t2j-add-btn {
            width: 100%;
            background: linear-gradient(135deg, #10b981, #059669);
            color: #ffffff;
            border: none;
            border-radius: 8px;
            padding: 8px 12px;
            font-size: 12px;
            font-weight: 600;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 6px;
            transition: all 0.2s ease;
        }
        .t2j-add-btn:hover:not(:disabled) {
            background: linear-gradient(135deg, #059669, #047857);
            box-shadow: 0 4px 12px rgba(16, 185, 129, 0.3);
        }
        .t2j-add-btn:disabled {
            opacity: 0.7;
            cursor: not-allowed;
        }
        .t2j-add-btn.success {
            background: #047857 !important;
            color: #a7f3d0 !important;
        }
        .t2j-status-spinner {
            display: inline-block;
            width: 12px;
            height: 12px;
            border: 2px solid rgba(255, 255, 255, 0.3);
            border-top-color: #fff;
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
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(endpoint);
        }
        return '/' + endpoint.replace(/^\/+/, '');
    }

    function getAuthHeaders() {
        var headers = { 'Accept': 'application/json', 'Content-Type': 'application/json' };
        if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
            var token = window.ApiClient.accessToken();
            if (token) {
                headers['X-Emby-Token'] = token;
                headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
            }
        }
        return headers;
    }

    async function performSearch(query) {
        query = (query || '').trim();
        if (query.length < 2) {
            removeResultsContainer();
            return;
        }

        if (abortController) {
            abortController.abort();
        }
        abortController = new AbortController();

        isSearching = true;
        renderLoadingIndicator(query);

        try {
            var res = await fetch(getApiUrl('Plugins/Torr2Jelly/Search?query=' + encodeURIComponent(query)), {
                headers: getAuthHeaders(),
                signal: abortController.signal
            });

            if (!res.ok) {
                removeResultsContainer();
                return;
            }

            var results = await res.json();
            renderSearchResults(query, results || []);
        } catch (err) {
            if (err.name !== 'AbortError') {
                console.error('[Torr2Jelly] Search error:', err);
                removeResultsContainer();
            }
        } finally {
            isSearching = false;
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
                        <h3 class="t2j-search-title">Torr2Jelly Торенти</h3>
                        <div class="t2j-search-subtitle">
                            <span class="t2j-status-spinner" style="margin-right: 5px;"></span>
                            Шукаємо на торент-трекерах за запитом "${escapeHtml(query)}"...
                        </div>
                    </div>
                </div>
            </div>
        `;
    }

    function renderSearchResults(query, results) {
        if (!results || results.length === 0) {
            var target = getSearchTargetContainer();
            if (target && target.mode === 'empty') {
                var existing = document.getElementById('t2j-search-section');
                if (existing) {
                    existing.innerHTML = `
                        <div class="t2j-search-header">
                            <div class="t2j-search-title-wrap">
                                <div class="t2j-search-icon" style="color: #64748b; border-color: rgba(100, 116, 139, 0.3);">⚡</div>
                                <div>
                                    <h3 class="t2j-search-title" style="color: #94a3b8;">Torr2Jelly</h3>
                                    <div class="t2j-search-subtitle">На торент-трекерах роздач за запитом "${escapeHtml(query)}" не знайдено.</div>
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

        // Sort results: Gold first, then Seeders descending
        results.sort(function (a, b) {
            if (a.IsGold && !b.IsGold) return -1;
            if (!a.IsGold && b.IsGold) return 1;
            return (b.Seeders || 0) - (a.Seeders || 0);
        });

        // Limit to top 9 best releases
        var displayResults = results.slice(0, 9);

        var headerHtml = `
            <div class="t2j-search-header">
                <div class="t2j-search-title-wrap">
                    <div class="t2j-search-icon">⚡</div>
                    <div>
                        <h3 class="t2j-search-title">${target.mode === 'empty' ? 'Немає на сервері? Знайдено в торентах' : 'Також доступно на торентах (Torr2Jelly)'}</h3>
                        <div class="t2j-search-subtitle">Додайте в один клік — фільм миттєво з'явиться в Jellyfin та почне стрімінг без очікування</div>
                    </div>
                </div>
                <div class="t2j-search-badge">${results.length} роздач знайдено</div>
            </div>
        `;

        var cardsHtml = '<div class="t2j-cards-grid">';
        displayResults.forEach(function (item, index) {
            var rawLink = item.DownloadUrl || item.MagnetUrl || '';
            var title = item.Title || '';
            var size = item.SizeFormatted || '';
            var seeds = item.Seeders || 0;
            var tracker = item.Indexer || 'Torrent';
            var isGold = item.IsGold || item.IsFreeleech;

            cardsHtml += `
                <div class="t2j-card">
                    <div>
                        <div class="t2j-card-meta">
                            <span class="t2j-badge t2j-badge-tracker">${escapeHtml(tracker)}</span>
                            ${isGold ? '<span class="t2j-badge t2j-badge-gold">⭐ Золотий</span>' : ''}
                            <span class="t2j-badge t2j-badge-seeders">▲ ${seeds}</span>
                            <span class="t2j-badge t2j-badge-size">${escapeHtml(size)}</span>
                        </div>
                        <div class="t2j-card-title" title="${escapeHtml(title)}">${escapeHtml(title)}</div>
                    </div>
                    <div class="t2j-card-footer">
                        <button type="button" class="t2j-add-btn" data-link="${encodeURIComponent(rawLink)}" data-title="${encodeURIComponent(title)}" data-poster="${encodeURIComponent(item.PosterUrl || '')}" data-tmdb="${item.TmdbId || 0}">
                            <span>+ Додати & Дивитися</span>
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

                if (!link) return;

                btn.disabled = true;
                btn.innerHTML = '<span class="t2j-status-spinner"></span> <span>Додаємо...</span>';

                try {
                    var payload = {
                        Link: link,
                        Title: itemTitle,
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
                        btn.className = 't2j-add-btn success';
                        btn.innerHTML = '<span>✓ Додано в медіатеку</span>';
                        showToast('✓ "' + itemTitle + '" успішно додано! Оновлюємо медіатеку...', true);

                        // Trigger scan
                        fetch(getApiUrl('Plugins/Torr2Jelly/Library/Scan'), {
                            method: 'POST',
                            headers: getAuthHeaders()
                        }).catch(function () {});

                        // After 2.5 seconds, refresh page or re-trigger search
                        setTimeout(function () {
                            if (window.location.hash.indexOf('/search') !== -1) {
                                // Trigger Jellyfin search update if possible
                                var searchInput = document.querySelector('#searchPage input.emby-input');
                                if (searchInput) {
                                    searchInput.dispatchEvent(new Event('input', { bubbles: true }));
                                }
                            }
                        }, 2500);
                    } else {
                        var errData = await res.json().catch(function () { return {}; });
                        btn.disabled = false;
                        btn.innerHTML = '<span>⚠ Помилка сервера</span>';
                        showToast('Помилка додавання: ' + (errData.error || 'Невдала відповідь'), false);
                    }
                } catch (e) {
                    btn.disabled = false;
                    btn.innerHTML = '<span>⚠ Помилка</span>';
                    showToast('Помилка з\'єднання: ' + e.message, false);
                }
            };
        });
    }

    // Observer and query sync
    function checkSearchPage() {
        var searchPage = document.getElementById('searchPage') || document.querySelector('.searchPage');
        if (!searchPage) {
            removeResultsContainer();
            return;
        }

        // Find search input
        var searchInput = searchPage.querySelector('input.emby-input') ||
                          searchPage.querySelector('input[type="search"]') ||
                          searchPage.querySelector('input');

        if (!searchInput) return;

        if (!searchInput._t2jBound) {
            searchInput._t2jBound = true;
            searchInput.addEventListener('input', function () {
                var val = (searchInput.value || '').trim();
                if (val !== currentQuery) {
                    currentQuery = val;
                    clearTimeout(debounceTimer);
                    debounceTimer = setTimeout(function () {
                        performSearch(currentQuery);
                    }, 450);
                }
            });
        }

        // Also check URL query param
        var hash = window.location.hash || '';
        var queryMatch = hash.match(/[?&]query=([^&]+)/);
        var urlQuery = queryMatch ? decodeURIComponent(queryMatch[1]).trim() : '';
        var effectiveQuery = (searchInput.value || urlQuery || '').trim();

        if (effectiveQuery && effectiveQuery !== currentQuery && !isSearching) {
            currentQuery = effectiveQuery;
            clearTimeout(debounceTimer);
            debounceTimer = setTimeout(function () {
                performSearch(currentQuery);
            }, 450);
        }
    }

    // Interval watcher for SPA route changes
    setInterval(checkSearchPage, 600);

    // Initial check
    setTimeout(checkSearchPage, 500);
})();
