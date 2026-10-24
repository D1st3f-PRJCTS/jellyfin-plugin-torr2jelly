// Loaded before the search integration; receives its auth, routing and native-dialog helpers.
window._t2jCreateDiscovery = function (host) {
    'use strict';
    var stopped = false, user = null, generation = 0, windowName = 'week';
    var section = null, modal = null, returnFocus = null, detailsAbort = null;
    var cache = {}, loading = false, loadToken = 0, requestAbort = null;
    var busy = {}, outcomes = {}, currentMovie = null, lastLoadAt = 0;
    var esc = host.escape;
    function t(uk, en) { return host.lang().indexOf('uk') === 0 ? uk : en; }
    function button(text, cls) { return '<button type="button" is="emby-button" class="emby-button ' + (cls || 'raised') + '">' + esc(text) + '</button>'; }
    function image(url, cls, alt) {
        if (!/^https:\/\/image\.tmdb\.org\/t\/p\/[a-z0-9]+\/[A-Za-z0-9._-]+$/.test(url || '')) return '';
        return '<img loading="lazy" class="' + cls + '" src="' + esc(url) + '" alt="' + esc(alt || '') + '">';
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
    style.textContent = '.t2j-discovery-head{display:flex;align-items:center;gap:1em;flex-wrap:wrap}.t2j-discovery-row{display:flex;gap:1em;overflow-x:auto;padding:.5em 0 1em;scroll-snap-type:x proximity}.t2j-discovery-card{flex:0 0 9em;min-width:0;color:inherit;background:none;border:0;padding:0;text-align:start;cursor:pointer;scroll-snap-align:start;font:inherit}.t2j-discovery-card:focus-visible{outline:2px solid var(--jf-palette-primary-main,#00a4dc);outline-offset:3px}.t2j-discovery-poster{display:block;width:100%;aspect-ratio:2/3;object-fit:cover;background:var(--jf-palette-background-paper,#202020);border-radius:var(--jf-shape-borderRadius,4px)}.t2j-discovery-card-title{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;margin-top:.5em}.t2j-discovery-meta{display:block;font-size:90%;color:var(--jf-palette-text-secondary,rgba(255,255,255,.7))}.t2j-discovery-details-top{display:flex;gap:1.3em;align-items:start}.t2j-discovery-detail-poster{width:9em;flex:none;border-radius:var(--jf-shape-borderRadius,4px)}.t2j-discovery-info{flex:1;min-width:0}.t2j-discovery-info p{margin:.3em 0 .8em}.t2j-discovery-cast{display:flex;gap:1em;overflow-x:auto;padding:.5em 0}.t2j-discovery-actor{flex:0 0 5.5em;font-size:90%}.t2j-discovery-actor img{width:100%;aspect-ratio:2/3;object-fit:cover;border-radius:var(--jf-shape-borderRadius,4px)}.t2j-discovery-actions{display:flex;gap:.8em;flex-wrap:wrap;margin:1em 0}.t2j-discovery-overview{white-space:pre-line;line-height:1.5}.t2j-discovery-credit{font-size:80%;margin-top:1em}.t2j-discovery-status{margin:.7em 0;white-space:pre-line}@media(max-width:40em){.t2j-discovery-card{flex-basis:7.5em}.t2j-discovery-detail-poster{width:6em}}';
    document.head.appendChild(style);

    function cacheKey() { return user + ':' + host.lang() + ':' + windowName; }
    function stateLine(text, retry) {
        if (!section) return;
        var body = section.querySelector('.t2j-discovery-row');
        body.innerHTML = '<div role="status">' + esc(text) + (retry ? button(t('Спробувати ще раз', 'Retry'), 'button-flat t2j-discovery-retry') : '') + '</div>';
        var retryButton = body.querySelector('button');
        if (retryButton) retryButton.onclick = function () { delete cache[cacheKey()]; load(); };
    }
    function renderMovies(movies) {
        if (!section) return;
        if (!movies.length) { stateLine(t('Популярних фільмів поки немає.', 'No trending movies available.'), true); return; }
        var body = section.querySelector('.t2j-discovery-row');
        body.innerHTML = movies.map(function (movie) {
            var state = movie.InLibraryAs === 'stream' ? t('У бібліотеці', 'In library') : movie.InLibraryAs === 'placeholder' ? t('Заглушка', 'Placeholder') : '';
            return '<button type="button" class="t2j-discovery-card" data-tmdb="' + movie.TmdbId + '" aria-label="' + esc(movie.Title) + '">'
                + (image(movie.PosterUrl, 't2j-discovery-poster', '') || '<span class="t2j-discovery-poster"></span>')
                + '<span class="t2j-discovery-card-title">' + esc(movie.Title) + '</span><span class="t2j-discovery-meta">' + esc([movie.Year, state].filter(Boolean).join(' · ')) + '</span></button>';
        }).join('');
        body.querySelectorAll('[data-tmdb]').forEach(function (card) { card.onclick = function () { open(parseInt(card.getAttribute('data-tmdb'), 10), card); }; });
    }
    async function load() {
        var token = ++loadToken, myGeneration = generation, key = cacheKey(), selected = windowName;
        if (requestAbort) requestAbort.abort();
        requestAbort = new AbortController();
        var abort = requestAbort;
        var timeout = setTimeout(function () { abort.abort(); }, 25000);
        loading = true;
        lastLoadAt = Date.now();
        stateLine(t('Завантаження…', 'Loading…'), false);
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
        if (!skipFocus && returnFocus && document.body.contains(returnFocus)) returnFocus.focus();
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
        var score = function (n, votes) { return typeof n === 'number' ? n.toFixed(1) + '/10' + (votes ? ' · ' + Number(votes).toLocaleString() + ' ' + t('голосів', 'votes') : '') : t('немає даних', 'not available'); };
        body.innerHTML = '<div class="t2j-discovery-details-top">' + image(movie.PosterUrl, 't2j-discovery-detail-poster', movie.Title)
            + '<div class="t2j-discovery-info"><h2>' + esc(movie.Title) + '</h2><p class="secondaryText">' + esc(movie.OriginalTitle || '') + '</p>'
            + '<p>' + esc([movie.Year, movie.Runtime ? movie.Runtime + ' ' + t('хв', 'min') : '', (movie.Genres || []).join(', ')].filter(Boolean).join(' · ')) + '</p>'
            + '<p>IMDb ' + esc(score(movie.ImdbRating, movie.ImdbVotes)) + '</p><p>TMDB ' + esc(score(movie.TmdbRating, movie.TmdbVotes)) + '</p></div></div>'
            + '<p class="t2j-discovery-overview">' + esc(movie.Overview || t('Опис поки відсутній.', 'No overview available.')) + '</p>'
            + '<div class="t2j-discovery-actions">' + button('', 'raised button-submit t2j-discovery-add') + button(t('Знайти вручну', 'Find manually'), 'button-flat t2j-discovery-manual') + '</div>'
            + '<div class="t2j-discovery-status" role="status" aria-live="polite"></div>'
            + '<h3>' + esc(t('Актори', 'Cast')) + '</h3><div class="t2j-discovery-cast">' + (movie.Actors || []).map(function (actor) {
                return '<div class="t2j-discovery-actor">' + image(actor.ImageUrl, '', actor.Name) + '<div>' + esc(actor.Name) + '</div><div class="secondaryText">' + esc(actor.Character || '') + '</div></div>';
            }).join('') + '</div>'
            + '<div class="fieldDescription t2j-discovery-credit"><a class="button-link" href="https://www.themoviedb.org/" target="_blank" rel="noopener noreferrer">TMDB</a> · This product uses the TMDB API but is not endorsed or certified by TMDB.<br>'
            + '<a class="button-link" href="https://data.imdb.com/non-commercial-datasets/" target="_blank" rel="noopener noreferrer">IMDb non-commercial datasets</a></div>';
        body.querySelector('.t2j-discovery-manual').onclick = function () { manual(movie); };
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
        btn.textContent = adding ? t('Шукаємо та перевіряємо…', 'Searching and verifying…') : movie.InLibraryAs === 'stream' ? t('У бібліотеці', 'In library') : movie.InLibraryAs === 'placeholder' ? t('Шукати й додати', 'Search and add') : t('Додати', 'Add');
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
        root.innerHTML = '<div class="dialogBackdrop"></div><div role="dialog" aria-modal="true" aria-labelledby="t2j-discovery-title" class="focuscontainer dialog formDialog t2j-dialog">'
            + '<div class="formDialogHeader"><button type="button" is="paper-icon-button-light" class="paper-icon-button-light t2j-discovery-close" aria-label="' + esc(t('Закрити', 'Close')) + '"><span class="material-icons" aria-hidden="true">close</span></button><h3 class="formDialogHeaderTitle" id="t2j-discovery-title">' + esc(t('Про фільм', 'Movie details')) + '</h3></div><div class="t2j-dialog-body" role="status">' + esc(t('Завантаження…', 'Loading…')) + '</div></div>';
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
            section.className = 'verticalSection padded-left padded-right t2j-discovery';
            section.innerHTML = '<div class="t2j-discovery-head"><h2 class="sectionTitle sectionTitle-cards">' + esc(t('Популярне зараз', 'Trending now')) + '</h2><div class="emby-tabs-slider" role="group" aria-label="' + esc(t('Період популярності', 'Trending period')) + '">'
                + '<button type="button" is="emby-button" class="emby-tab-button" data-window="day">' + esc(t('Сьогодні', 'Today')) + '</button><button type="button" is="emby-button" class="emby-tab-button" data-window="week">' + esc(t('За тиждень', 'This week')) + '</button></div></div><div class="t2j-discovery-row"></div>';
            (home.querySelector('.homeSectionsContainer, .sections, .pageContent') || home).appendChild(section);
            section.querySelectorAll('[data-window]').forEach(function (btn) { btn.onclick = function () {
                if (windowName === btn.getAttribute('data-window')) return;
                windowName = btn.getAttribute('data-window');
                section.querySelectorAll('[data-window]').forEach(function (b) { var active = b === btn; b.classList.toggle('emby-tab-button-active', active); b.setAttribute('aria-pressed', active ? 'true' : 'false'); });
                var entry = cache[cacheKey()];
                if (entry && Date.now() - entry.at < 600000) { loadToken++; if (requestAbort) requestAbort.abort(); loading = false; renderMovies(entry.movies); }
                else load();
            }; btn.classList.toggle('emby-tab-button-active', btn.getAttribute('data-window') === windowName); btn.setAttribute('aria-pressed', btn.getAttribute('data-window') === windowName ? 'true' : 'false'); });
            var entry = cache[cacheKey()];
            if (entry && Date.now() - entry.at < 600000) renderMovies(entry.movies);
            else if (!loading) load();
        }
        else if (!loading && Date.now() - lastLoadAt >= 600000) load();
    }
    return { check: check, close: close, dispose: function () { stopped = true; reset(); style.remove(); } };
};
