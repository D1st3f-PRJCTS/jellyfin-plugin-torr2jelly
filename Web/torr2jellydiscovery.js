// Loaded before the search integration; receives its auth, routing and native-dialog helpers.
window._t2jCreateDiscovery = function (host) {
    'use strict';
    var stopped = false, user = null, generation = 0, windowName = 'week';
    var section = null, modal = null, returnFocus = null, detailsAbort = null;
    var cache = {}, loading = false, loadToken = 0, requestAbort = null;
    var busy = {}, outcomes = {}, currentMovie = null, lastLoadAt = 0;
    var esc = host.escape;
    function icon(name, cls) { return host.icon ? host.icon(name, cls) : '<span class="material-icons ' + (cls || '') + '" aria-hidden="true">' + name + '</span>'; }
    function t(uk, en) { return host.lang().indexOf('uk') === 0 ? uk : en; }
    function button(text, cls, symbol) { return '<button type="button" is="emby-button" class="emby-button ' + (cls || 'raised') + '">' + (symbol ? icon(symbol) : '') + '<span>' + esc(text) + '</span></button>'; }
    function image(url, cls, alt) {
        if (!/^https:\/\/image\.tmdb\.org\/t\/p\/[a-z0-9]+\/[A-Za-z0-9._-]+$/.test(url || '')) return '';
        return '<img loading="lazy" class="' + cls + '" src="' + esc(url) + '" alt="' + esc(alt || '') + '">';
    }
    function imageFallbacks(root) {
        root.querySelectorAll('img').forEach(function (img) { img.onerror = function () { img.remove(); }; });
    }
    function normalize(value) {
        // The host's JSON settings differ between Jellyfin versions; accept PascalCase and camelCase recursively.
        if (Array.isArray(value)) return value.map(normalize);
        if (!value || typeof value !== 'object') return value;
        var result = {};
        Object.keys(value).forEach(function (key) { result[key.charAt(0).toUpperCase() + key.slice(1)] = normalize(value[key]); });
        return result;
    }
    async function request(path, options) {
        options = options || {};
        options.headers = host.auth();
        var res = await fetch(host.url('Plugins/Torr2Jelly/Discover/' + path), options);
        var data = await res.json().catch(function () { return {}; });
        if (!res.ok) { var error = new Error(data.code || data.Code || 'discovery_failed'); error.status = res.status; throw error; }
        return normalize(data);
    }
    function errorText(code) {
        if (code === 'tmdb_key_missing') return t('Додайте ключ TMDB у налаштуваннях Torr2Jelly.', 'Add a TMDB key in Torr2Jelly settings.');
        if (code === 'tmdb_key_invalid') return t('TMDB відхилив ключ доступу.', 'TMDB rejected the API key.');
        if (code === 'category_unavailable') return t('Налаштуйте бібліотеку фільмів у Torr2Jelly.', 'Configure a movie library in Torr2Jelly.');
        if (code === 'discovery_timeout') return t('Час пошуку вичерпано. Перевірте заглушку в бібліотеці або повторіть пошук.', 'The search timed out. Check the library placeholder or try again.');
        if (code === 'job_not_found') return t('Сервер перезапустився або завдання застаріло. Перевірте бібліотеку й повторіть пошук за потреби.', 'The server restarted or the job expired. Check the library and retry if needed.');
        return t('Не вдалося завантажити дані. Спробуйте ще раз.', 'Could not load the data. Please try again.');
    }
    function outcomeText(result) {
        if (result.Status === 'stream') return t('Додано перевірений реліз: ', 'Verified release added: ') + result.ReleaseTitle;
        if (result.Status === 'exists') return t('Фільм уже є в бібліотеці.', 'This movie is already in the library.');
        var reason = result.Reason;
        if (reason === 'search_failed') return t('Заглушка збережена. Частина пошуку завершилася помилкою; перевіреного релізу не отримано.', 'Placeholder saved. A search provider failed; no verified release was obtained.');
        if (reason === 'torrserver_unavailable') return t('Заглушка збережена. TorrServer недоступний.', 'Placeholder saved. TorrServer is unavailable.');
        if (reason === 'write_failed') return t('Заглушка збережена. Не вдалося записати стрім.', 'Placeholder saved. Could not write the stream.');
        return t('Заглушка збережена. Немає релізу з підтвердженими ID та файлами. Можна вибрати вручну.', 'Placeholder saved. No release has verified IDs and files. You can choose one manually.');
    }

    var style = document.createElement('style');
    // Jellyfin supplies card sizes, typography, theme colors, focus and button treatment.
    // These rules only arrange the discovery content inside those native components.
    style.textContent = `
        .t2j-discovery-head{display:flex;align-items:center;gap:1.2em;flex-wrap:wrap;padding-inline-end:7em}
        .t2j-discovery-head .sectionTitle{margin-inline-end:0}
        .t2j-discovery-period{display:flex;align-items:center}
        .t2j-discovery-period .emby-tab-button{padding:.6em .9em}
        .t2j-discovery-scroll{overflow-x:auto;overflow-y:hidden;scrollbar-width:none;scroll-behavior:smooth}
        .t2j-discovery-scroll::-webkit-scrollbar{display:none}
        .t2j-discovery-row{display:flex;flex-flow:row nowrap;white-space:nowrap}
        .t2j-discovery-card{flex:0 0 auto;cursor:pointer}
        .t2j-discovery-card .cardImageContainer{border:0;padding:0;font:inherit;color:inherit;cursor:pointer}
        .t2j-discovery-poster{position:absolute;inset:0;display:block;width:100%;height:100%;object-fit:cover}
        .t2j-discovery-card .cardText{overflow:hidden;text-overflow:ellipsis}
        .t2j-discovery-indicator{display:flex;align-items:center;gap:.25em;padding:.25em .4em}
        .t2j-discovery-indicator .material-icons{font-size:1em}
        .t2j-discovery-state{white-space:normal;padding-block:1em}
        .t2j-discovery-modal .t2j-discovery-dialog{width:64em;max-width:94vw;max-height:90vh}
        .t2j-discovery-modal .t2j-dialog-body{padding:1em 1.5em 1.5em}
        .t2j-discovery-details-top{display:flex;gap:2em;align-items:flex-start}
        .t2j-discovery-detail-poster{width:12em;flex:none;aspect-ratio:2/3;object-fit:cover;border-radius:var(--jf-shape-borderRadius,4px)}
        .t2j-discovery-info{flex:1;min-width:0}
        .t2j-discovery-info .itemName{margin-top:0}
        .t2j-discovery-info h2.itemName{font-size:2em;margin-bottom:.25em}
        .t2j-discovery-info .originalTitle{margin-bottom:1em}
        .t2j-discovery-info .itemMiscInfo{display:flex;flex-wrap:wrap;align-items:center;gap:.4em 1em;margin:.6em 0}
        .t2j-discovery-info .mediaInfoItem{margin:0}
        .t2j-discovery-rating-source{margin-inline-start:.4em;font-size:.85em}
        .t2j-discovery-actions{display:flex;align-items:center;flex-wrap:wrap;gap:.5em;margin:1.3em 0}
        .t2j-discovery-actions .material-icons{margin-inline-end:.4em}
        .t2j-discovery-actions .emby-button{margin-inline-start:0}
        .t2j-discovery-actions .button-submit{background:var(--jf-palette-primary-main,#00a4dc);color:var(--jf-palette-primary-contrastText,#fff)}
        .t2j-discovery-overview{white-space:pre-line;line-height:1.5;margin:1.5em 0}
        .t2j-discovery-cast{display:flex;overflow-x:auto;gap:0;scrollbar-width:thin}
        .t2j-discovery-actor{flex:0 0 8em;min-width:0;white-space:normal}
        .t2j-discovery-actor .cardImageContainer{position:absolute;inset:0}
        .t2j-discovery-actor .cardText{white-space:normal}
        .t2j-discovery-credit{margin-top:1.5em}
        .t2j-discovery-status:empty{display:none}
        .t2j-discovery-status{margin:.7em 0;white-space:pre-line}
        @media(max-width:50em){.t2j-discovery-modal .t2j-discovery-dialog{width:100%;max-width:none;height:100%;max-height:none;border-radius:0}.t2j-discovery-modal .t2j-dialog-body{padding:1em}.t2j-discovery-details-top{gap:1em}.t2j-discovery-detail-poster{width:7em}.t2j-discovery-info h2.itemName{font-size:1.5em}.t2j-discovery-head{gap:0;justify-content:space-between;padding-inline-end:0}.t2j-discovery-period{padding-inline-end:3.3%}.t2j-discovery-actor{flex-basis:7em}}
        @media(prefers-reduced-motion:reduce){.t2j-discovery-scroll{scroll-behavior:auto}}
    `;
    document.head.appendChild(style);

    function cacheKey() { return user + ':' + host.lang() + ':' + windowName; }
    function stateLine(text, retry) {
        if (!section) return;
        var body = section.querySelector('.t2j-discovery-row');
        body.innerHTML = '<div class="padded-left padded-right t2j-discovery-state" role="status">' + esc(text) + (retry ? button(t('Спробувати ще раз', 'Retry'), 'button-flat t2j-discovery-retry') : '') + '</div>';
        var retryButton = body.querySelector('button');
        if (retryButton) retryButton.onclick = function () { delete cache[cacheKey()]; load(); };
    }
    function renderMovies(movies) {
        if (!section) return;
        if (!movies.length) { stateLine(t('Популярних фільмів поки немає.', 'No trending movies available.'), true); return; }
        var body = section.querySelector('.t2j-discovery-row');
        var scroller = section.querySelector('.t2j-discovery-scroll'), offset = scroller.scrollLeft;
        body.innerHTML = movies.map(function (movie) {
            var state = movie.InLibraryAs === 'stream' ? t('У бібліотеці', 'In library') : movie.InLibraryAs === 'placeholder' ? t('Заглушка', 'Placeholder') : '';
            return '<div class="card overflowPortraitCard card-hoverable t2j-discovery-card" data-tmdb="' + movie.TmdbId + '"><div class="cardBox cardBox-bottompadded"><div class="cardScalable">'
                + '<div class="cardPadder cardPadder-overflowPortrait">' + icon('movie', 'cardImageIcon') + '</div>'
                + '<button type="button" class="cardImageContainer coveredImage cardContent" aria-label="' + esc([movie.Title, movie.Year, state].filter(Boolean).join(' · ')) + '">'
                + image(movie.PosterUrl, 't2j-discovery-poster', '') + '</button>'
                + (state ? '<div class="cardIndicators" aria-hidden="true"><div class="indicator t2j-discovery-indicator" title="' + esc(state) + '">' + icon(movie.InLibraryAs === 'stream' ? 'check' : 'bookmark') + '</div></div>' : '')
                + '</div><div class="cardText cardTextCentered cardText-first" title="' + esc(movie.Title) + '"><bdi>' + esc(movie.Title) + '</bdi></div>'
                + '<div class="cardText cardTextCentered cardText-secondary"><bdi>' + esc(movie.Year || '') + '</bdi></div></div></div>';
        }).join('');
        body.querySelectorAll('[data-tmdb]').forEach(function (card) { card.onclick = function () { open(parseInt(card.getAttribute('data-tmdb'), 10), card.querySelector('button')); }; });
        imageFallbacks(body);
        scroller.scrollLeft = offset;
        updateScroll();
    }
    function updateScroll() {
        if (!section) return;
        var row = section.querySelector('.t2j-discovery-scroll');
        var previous = section.querySelector('[data-direction="left"]'), next = section.querySelector('[data-direction="right"]');
        var offset = Math.abs(row.scrollLeft), max = Math.max(0, row.scrollWidth - row.clientWidth);
        previous.disabled = offset < 2;
        next.disabled = offset >= max - 2;
    }
    async function load(keepCards) {
        var token = ++loadToken, myGeneration = generation, key = cacheKey(), selected = windowName;
        if (requestAbort) requestAbort.abort();
        requestAbort = new AbortController();
        var abort = requestAbort;
        var timeout = setTimeout(function () { abort.abort(); }, 25000);
        loading = true;
        lastLoadAt = Date.now();
        if (!keepCards) stateLine(t('Завантаження…', 'Loading…'), false);
        try {
            var movies = await request('Movies?window=' + selected, { signal: abort.signal });
            if (stopped || generation !== myGeneration || token !== loadToken) return;
            cache[key] = { at: Date.now(), movies: movies };
            renderMovies(movies);
        } catch (error) {
            if (!stopped && generation === myGeneration && token === loadToken) stateLine(errorText(error.message), true);
        } finally { clearTimeout(timeout); if (token === loadToken) loading = false; }
    }
    function close(skipFocus) {
        // This module must never release a scroll lock owned by the existing torrent dialog.
        if (!modal) return;
        if (detailsAbort) detailsAbort.abort();
        detailsAbort = null;
        currentMovie = null;
        if (modal) modal.remove();
        modal = null;
        document.removeEventListener('keydown', onKey, true);
        host.unlock();
        if (!skipFocus && returnFocus) {
            var target = returnFocus;
            // Completing Add redraws the row; restore focus to that movie's new poster button.
            if (!document.body.contains(target) && section) {
                var oldCard = target.closest('[data-tmdb]');
                if (oldCard) section.querySelectorAll('[data-tmdb]').forEach(function (card) {
                    if (card.getAttribute('data-tmdb') === oldCard.getAttribute('data-tmdb')) target = card.querySelector('button');
                });
            }
            if (target && document.body.contains(target)) target.focus();
        }
        returnFocus = null;
    }
    function onKey(event) {
        if (!modal) return;
        if (event.key === 'Escape' || event.key === 'BrowserBack' || event.keyCode === 461 || event.keyCode === 10009) {
            event.preventDefault(); event.stopPropagation(); close(); return;
        }
        if (event.key === 'Tab') {
            var items = host.focusables(modal);
            if (!items.length) return;
            var first = items[0], last = items[items.length - 1];
            if (!modal.contains(document.activeElement) || (!event.shiftKey && document.activeElement === last)) { event.preventDefault(); first.focus(); }
            else if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        }
    }
    function manual(movie) {
        close(true);
        window.location.hash = movie.ItemId ? '#/details?id=' + encodeURIComponent(movie.ItemId) : '#/search?query=' + encodeURIComponent((movie.OriginalTitle || movie.Title) + ' ' + (movie.Year || ''));
    }
    function renderDetails(movie) {
        if (!modal) return;
        currentMovie = movie;
        var body = modal.querySelector('.t2j-dialog-body');
        var rating = function (source, n, votes) {
            var value = typeof n === 'number' ? n.toFixed(1) : t('немає даних', 'not available');
            var title = source + ' ' + value + (votes ? ' · ' + Number(votes).toLocaleString() + ' ' + t('голосів', 'votes') : '');
            return '<div class="starRatingContainer mediaInfoItem" title="' + esc(title) + '" aria-label="' + esc(title) + '">' + icon('star', 'starIcon') + esc(value) + '<span class="secondaryText t2j-discovery-rating-source">' + source + '</span></div>';
        };
        body.innerHTML = '<div class="t2j-discovery-details-top">' + image(movie.PosterUrl, 't2j-discovery-detail-poster', movie.Title)
            + '<div class="t2j-discovery-info"><div class="nameContainer"><h2 class="itemName infoText" id="t2j-discovery-movie-title"><bdi>' + esc(movie.Title) + '</bdi></h2>'
            + (movie.OriginalTitle && movie.OriginalTitle !== movie.Title ? '<h4 class="itemName infoText originalTitle">' + esc(movie.OriginalTitle) + '</h4>' : '') + '</div>'
            + '<div class="itemMiscInfo itemMiscInfo-primary">' + [movie.Year, movie.Runtime ? movie.Runtime + ' ' + t('хв', 'min') : ''].filter(Boolean).map(function (value) { return '<div class="mediaInfoItem">' + esc(value) + '</div>'; }).join('') + '</div>'
            + '<div class="itemMiscInfo">' + rating('IMDb', movie.ImdbRating, movie.ImdbVotes) + rating('TMDB', movie.TmdbRating, movie.TmdbVotes) + '</div>'
            + '<p class="secondaryText">' + esc((movie.Genres || []).join(', ')) + '</p>'
            + '<div class="t2j-discovery-actions">' + button('', 'raised button-submit t2j-discovery-add', 'add') + button(t('Знайти вручну', 'Find manually'), 'button-flat t2j-discovery-manual', 'search') + '</div></div></div>'
            + '<div class="t2j-discovery-status" role="status" aria-live="polite"></div>'
            + '<p class="overview t2j-discovery-overview">' + esc(movie.Overview || t('Опис поки відсутній.', 'No overview available.')) + '</p>'
            + '<h3 class="sectionTitle sectionTitle-cards">' + esc(t('Актори', 'Cast')) + '</h3><div class="t2j-discovery-cast">' + (movie.Actors || []).map(function (actor) {
                return '<div class="card portraitCard t2j-discovery-actor"><div class="cardBox cardBox-bottompadded"><div class="cardScalable"><div class="cardPadder cardPadder-portrait">' + icon('person', 'cardImageIcon') + '</div><div class="cardImageContainer coveredImage">' + image(actor.ImageUrl, 't2j-discovery-poster', actor.Name) + '</div></div><div class="cardText cardTextCentered cardText-first"><bdi>' + esc(actor.Name) + '</bdi></div><div class="cardText cardTextCentered cardText-secondary"><bdi>' + esc(actor.Character || '') + '</bdi></div></div></div>';
            }).join('') + '</div>'
            + '<div class="fieldDescription t2j-discovery-credit"><a class="button-link" href="https://www.themoviedb.org/" target="_blank" rel="noopener noreferrer">TMDB</a> · This product uses the TMDB API but is not endorsed or certified by TMDB.<br>'
            + '<a class="button-link" href="https://data.imdb.com/non-commercial-datasets/" target="_blank" rel="noopener noreferrer">IMDb non-commercial datasets</a></div>';
        body.querySelector('.t2j-discovery-manual').onclick = function () { manual(movie); };
        modal.querySelector('[role="dialog"]').setAttribute('aria-labelledby', 't2j-discovery-movie-title');
        imageFallbacks(body);
        body.querySelector('.t2j-discovery-add').onclick = function () {
            if (movie.InLibraryAs === 'stream') manual(movie);
            else add(movie);
        };
        updateActions(movie);
    }
    function updateActions(movie) {
        if (!modal || currentMovie !== movie) return;
        var adding = !!busy[movie.TmdbId], btn = modal.querySelector('.t2j-discovery-add');
        btn.disabled = adding;
        var label = adding ? t('Шукаємо та перевіряємо…', 'Searching and verifying…') : movie.InLibraryAs === 'stream' ? t('У бібліотеці', 'In library') : movie.InLibraryAs === 'placeholder' ? t('Шукати й додати', 'Search and add') : t('Додати', 'Add');
        btn.innerHTML = icon(movie.InLibraryAs === 'stream' ? 'check' : 'add') + '<span>' + esc(label) + '</span>';
        var status = modal.querySelector('.t2j-discovery-status');
        if (adding) status.textContent = t('Це може зайняти кілька хвилин. Вікно можна закрити.', 'This may take a few minutes. You can close this dialog.');
        else if (outcomes[movie.TmdbId]) status.textContent = outcomeText(outcomes[movie.TmdbId]);
    }
    async function open(id, opener) {
        close(true);
        host.closeOther();
        returnFocus = opener;
        var root = document.createElement('div');
        root.className = 'dialogContainer t2j-discovery-modal';
        root.innerHTML = '<div class="dialogBackdrop"></div><div role="dialog" aria-modal="true" aria-labelledby="t2j-discovery-title" class="focuscontainer dialog formDialog t2j-dialog t2j-discovery-dialog">'
            + '<div class="formDialogHeader"><button type="button" is="paper-icon-button-light" class="paper-icon-button-light t2j-discovery-close" aria-label="' + esc(t('Закрити', 'Close')) + '">' + icon('close') + '</button><h2 class="formDialogHeaderTitle" id="t2j-discovery-title">' + esc(t('Про фільм', 'Movie details')) + '</h2></div><div class="t2j-dialog-body" role="status">' + esc(t('Завантаження…', 'Loading…')) + '</div></div>';
        modal = root;
        document.body.appendChild(root);
        host.lock();
        root.querySelector('.t2j-discovery-close').onclick = function () { close(); };
        root.querySelector('.dialogBackdrop').onclick = function () { close(); };
        document.addEventListener('keydown', onKey, true);
        root.querySelector('button').focus();
        var myGeneration = generation;
        detailsAbort = new AbortController();
        var abort = detailsAbort, timeout = setTimeout(function () { abort.abort(); }, 25000);
        try {
            var movie = await request('Movies/' + id, { signal: abort.signal });
            if (stopped || modal !== root || generation !== myGeneration) return;
            var outcome = outcomes[id];
            if (outcome && movie.InLibraryAs === (outcome.Status === 'placeholder' ? 'placeholder' : 'stream')) movie.ItemId = outcome.ItemId || movie.ItemId;
            else if (!busy[id]) delete outcomes[id];
            renderDetails(movie);
            if (movie.ActiveJobId && !busy[id]) add(movie, movie.ActiveJobId);
        } catch (error) {
            if (modal === root && !stopped && generation === myGeneration) {
                var body = root.querySelector('.t2j-dialog-body');
                body.innerHTML = '<p>' + esc(errorText(error.message)) + '</p>' + button(t('Спробувати ще раз', 'Retry'));
                body.querySelector('button').onclick = function () { open(id, opener); };
            }
        } finally { clearTimeout(timeout); }
    }
    async function add(movie, resumeJobId) {
        if (busy[movie.TmdbId]) return;
        var myGeneration = generation;
        busy[movie.TmdbId] = true;
        updateActions(movie);
        try {
            // Keep the mutation alive when its dialog closes. A late response may only update the same user.
            var job = resumeJobId ? await request('Jobs/' + encodeURIComponent(resumeJobId)) : await request('Movies/' + movie.TmdbId + '/Add', { method: 'POST' });
            while (job.Status === 'queued' || job.Status === 'running') {
                await new Promise(function (resolve) { setTimeout(resolve, 1500); });
                if (stopped || generation !== myGeneration) return;
                job = await request('Jobs/' + encodeURIComponent(job.JobId));
            }
            if (job.Status === 'failed') throw new Error(job.Code || 'discovery_failed');
            var result = job.Result;
            if (!result) throw new Error('discovery_failed');
            if (stopped || generation !== myGeneration) return;
            outcomes[movie.TmdbId] = result;
            movie.InLibraryAs = result.Status === 'placeholder' ? 'placeholder' : 'stream';
            movie.ItemId = result.ItemId || movie.ItemId;
            Object.keys(cache).forEach(function (key) { cache[key].movies.forEach(function (item) { if (item.TmdbId === movie.TmdbId) item.InLibraryAs = movie.InLibraryAs; }); });
            if (currentMovie && currentMovie.TmdbId === movie.TmdbId) {
                currentMovie.InLibraryAs = movie.InLibraryAs;
                currentMovie.ItemId = movie.ItemId;
            }
            if (section && cache[cacheKey()]) renderMovies(cache[cacheKey()].movies);
            host.toast(outcomeText(result), result.Status !== 'placeholder');
        } catch (error) {
            if (!stopped && generation === myGeneration) {
                host.toast(errorText(error.message), false);
                if (modal && currentMovie && currentMovie.TmdbId === movie.TmdbId) modal.querySelector('.t2j-discovery-status').textContent = errorText(error.message);
            }
        } finally {
            if (generation === myGeneration) {
                delete busy[movie.TmdbId];
                if (currentMovie && currentMovie.TmdbId === movie.TmdbId) updateActions(currentMovie);
            }
        }
    }
    function reset() {
        generation++; loadToken++;
        if (requestAbort) requestAbort.abort();
        close(true);
        if (section) section.remove();
        section = null; cache = {}; busy = {}; outcomes = {}; loading = false;
    }
    function position(home) {
        var container = home.querySelector('.homeSectionsContainer, .sections, .pageContent') || home;
        var folders = container.querySelector('[data-type="CollectionFolder"], [data-type="UserView"]');
        while (folders && folders.parentElement !== container) folders = folders.parentElement;
        var before = folders ? folders.nextElementSibling : container.firstElementChild;
        if (before !== section) container.insertBefore(section, before);
    }
    function check() {
        if (stopped) return;
        var currentUser = host.user();
        if (currentUser !== user) { reset(); user = currentUser; }
        if (!user || host.admin() !== true) { close(true); if (section) { section.remove(); section = null; } return; }
        var route = window.location.hash || '';
        if (route && !/^(?:#\/?(?:home(?:\.html)?|index(?:\.html)?)?(?:\?|$))/.test(route)) { close(true); return; }
        var home = document.querySelector('.homePage:not(.hide), #indexPage:not(.hide)');
        if (!home || home.closest('.hide')) return;
        if (!section || !document.body.contains(section)) {
            section = document.createElement('div');
            section.className = 'verticalSection emby-scroller-container t2j-discovery';
            section.innerHTML = '<div class="t2j-discovery-head"><h2 class="sectionTitle sectionTitle-cards padded-left">' + esc(t('Популярне зараз', 'Trending now')) + '</h2><div class="emby-tabs-slider t2j-discovery-period" role="group" aria-label="' + esc(t('Період популярності', 'Trending period')) + '">'
                + '<button type="button" is="emby-button" class="emby-tab-button emby-button" data-window="day">' + esc(t('Сьогодні', 'Today')) + '</button><button type="button" is="emby-button" class="emby-tab-button emby-button" data-window="week">' + esc(t('За тиждень', 'This week')) + '</button></div></div>'
                + '<div class="emby-scrollbuttons padded-right"><button type="button" class="emby-scrollbuttons-button paper-icon-button-light" data-direction="left" aria-label="' + esc(t('Назад', 'Previous')) + '">' + icon('chevron_left') + '</button><button type="button" class="emby-scrollbuttons-button paper-icon-button-light" data-direction="right" aria-label="' + esc(t('Далі', 'Next')) + '">' + icon('chevron_right') + '</button></div>'
                + '<div class="emby-scroller padded-top-focusscale padded-bottom-focusscale t2j-discovery-scroll"><div class="scrollSlider focuscontainer-x t2j-discovery-row"></div></div>';
            // Jellyfin calls pause()/resume() on every .itemsContainer in its home view.
            // Keep our independently managed row out of that component lifecycle.
            // Its flex layout is declared above; scrollSlider styling is not loaded on every client.
            position(home);
            var scroller = section.querySelector('.t2j-discovery-scroll');
            scroller.addEventListener('scroll', updateScroll, { passive: true });
            section.querySelectorAll('[data-direction]').forEach(function (btn) { btn.onclick = function () {
                var direction = (btn.getAttribute('data-direction') === 'left' ? -1 : 1) * (getComputedStyle(scroller).direction === 'rtl' ? -1 : 1);
                scroller.scrollBy({ left: direction * scroller.clientWidth * .8, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
            }; });
            section.querySelectorAll('[data-window]').forEach(function (btn) { btn.onclick = function () {
                if (windowName === btn.getAttribute('data-window')) return;
                windowName = btn.getAttribute('data-window');
                scroller.scrollLeft = 0;
                section.querySelectorAll('[data-window]').forEach(function (b) { var active = b === btn; b.classList.toggle('emby-tab-button-active', active); b.setAttribute('aria-pressed', active ? 'true' : 'false'); });
                var entry = cache[cacheKey()];
                if (entry && Date.now() - entry.at < 600000) { loadToken++; if (requestAbort) requestAbort.abort(); loading = false; renderMovies(entry.movies); }
                else load();
            }; btn.classList.toggle('emby-tab-button-active', btn.getAttribute('data-window') === windowName); btn.setAttribute('aria-pressed', btn.getAttribute('data-window') === windowName ? 'true' : 'false'); });
            var entry = cache[cacheKey()];
            if (entry && Date.now() - entry.at < 600000) renderMovies(entry.movies);
            else if (!loading) load();
        }
        else if (!loading && Date.now() - lastLoadAt >= 600000) load(true);
        // Jellyfin may insert the library folders after this module's first check.
        position(home);
        updateScroll();
    }
    return { check: check, close: close, dispose: function () { stopped = true; reset(); style.remove(); } };
};
