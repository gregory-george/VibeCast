export function init(mediaEl, dotNetRef) {
    mediaEl.addEventListener('ended', () => dotNetRef.invokeMethodAsync('OnEnded'));
    mediaEl.addEventListener('play', () => dotNetRef.invokeMethodAsync('OnPlayStateChanged', true));
    mediaEl.addEventListener('pause', () => dotNetRef.invokeMethodAsync('OnPlayStateChanged', false));
}

export function setSource(mediaEl, src, startSeconds) {
    mediaEl.src = src;
    mediaEl.currentTime = startSeconds || 0;
    return mediaEl.play();
}

export function play(mediaEl) {
    return mediaEl.play();
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
