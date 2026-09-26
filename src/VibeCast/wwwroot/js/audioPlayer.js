// The episode id travels with each event (stamped by setSource) so a pause that
// fires late -- e.g. the element being removed when switching to a YouTube
// episode -- is saved against the episode it was actually playing.
function episodeIdOf(mediaEl) {
    return Number(mediaEl.dataset.episodeId) || 0;
}

export function init(mediaEl, dotNetRef) {
    // init runs on every episode load, but the element is reused between RSS
    // episodes; only wire the listeners once per element.
    if (mediaEl.dataset.vcInit) {
        return;
    }
    mediaEl.dataset.vcInit = '1';
    mediaEl.addEventListener('ended', () => dotNetRef.invokeMethodAsync('OnEnded', episodeIdOf(mediaEl)));
    mediaEl.addEventListener('play', () => dotNetRef.invokeMethodAsync('OnPlayStateChanged', true));
    mediaEl.addEventListener('pause', () => {
        dotNetRef.invokeMethodAsync('OnPlayStateChanged', false);
        // A pause at the very end is followed by 'ended', which resets the position.
        if (!mediaEl.ended) {
            dotNetRef.invokeMethodAsync('OnPaused', episodeIdOf(mediaEl), mediaEl.currentTime || 0);
        }
    });
}

// The browser rejects a pending play() promise when a new load is started
// before it resolves (rapid episode switches, re-render). That AbortError is
// expected and harmless, but if it propagates back across JSInterop it becomes
// an unhandled JSException that tears down the Blazor circuit. Swallow it here.
function safePlay(mediaEl) {
    return mediaEl.play().catch(err => {
        if (err && err.name === 'AbortError') {
            return;
        }
        throw err;
    });
}

export function setSource(mediaEl, src, startSeconds, episodeId) {
    mediaEl.dataset.episodeId = String(episodeId);
    mediaEl.src = src;
    mediaEl.currentTime = startSeconds || 0;
    return safePlay(mediaEl);
}

export function play(mediaEl) {
    return safePlay(mediaEl);
}

export function pause(mediaEl) {
    mediaEl.pause();
}

export function seek(mediaEl, seconds) {
    mediaEl.currentTime = seconds;
}

export function setRate(mediaEl, rate) {
    mediaEl.playbackRate = rate;
    mediaEl.preservesPitch = true;
}

export function getCurrentTime(mediaEl) {
    return mediaEl.currentTime || 0;
}

export function getDuration(mediaEl) {
    return isFinite(mediaEl.duration) ? mediaEl.duration : 0;
}

// Position worth resuming from: 0 once the episode has finished, so sitting on the
// end frame (or closing the player there) doesn't store "resume at the end".
export function getResumePosition(mediaEl) {
    return mediaEl.ended ? 0 : (mediaEl.currentTime || 0);
}
