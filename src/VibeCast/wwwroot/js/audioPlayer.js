export function init(mediaEl, dotNetRef) {
    mediaEl.addEventListener('ended', () => dotNetRef.invokeMethodAsync('OnEnded'));
    mediaEl.addEventListener('play', () => dotNetRef.invokeMethodAsync('OnPlayStateChanged', true));
    mediaEl.addEventListener('pause', () => dotNetRef.invokeMethodAsync('OnPlayStateChanged', false));
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

export function setSource(mediaEl, src, startSeconds) {
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
