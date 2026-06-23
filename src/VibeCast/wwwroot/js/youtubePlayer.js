let player = null;

function ensureApiLoaded() {
    return new Promise(resolve => {
        if (window.YT && window.YT.Player) {
            resolve();
            return;
        }
        const existing = window.onYouTubeIframeAPIReady;
        window.onYouTubeIframeAPIReady = () => {
            if (existing) {
                existing();
            }
            resolve();
        };
        if (!document.getElementById('youtube-iframe-api')) {
            const tag = document.createElement('script');
            tag.id = 'youtube-iframe-api';
            tag.src = 'https://www.youtube.com/iframe_api';
            document.body.appendChild(tag);
        }
    });
}

export async function init(elementId, videoId, startSeconds, dotNetRef, captionsEnabled) {
    await ensureApiLoaded();
    if (player) {
        player.destroy();
        player = null;
    }

    return new Promise((resolve, reject) => {
        try {
            player = new YT.Player(elementId, {
                videoId: videoId,
                height: '100%',
                width: '100%',
                playerVars: { autoplay: 1, start: Math.floor(startSeconds || 0), cc_load_policy: captionsEnabled ? 1 : 0 },
                events: {
                    onReady: () => resolve(),
                    onError: (e) => console.error('[vibecast-yt] player error', e.data),
                    onStateChange: (e) => dotNetRef.invokeMethodAsync('OnYouTubeStateChange', e.data),
                },
            });
        } catch (e) {
            reject(e);
        }
    });
}

export function setCaptionsEnabled(enabled) {
    if (!player) {
        return;
    }
    if (enabled) {
        player.loadModule('captions');
    } else {
        player.unloadModule('captions');
    }
}

export function pause() {
    if (player) {
        player.pauseVideo();
    }
}

export function getCurrentTime() {
    return player ? player.getCurrentTime() : 0;
}

export function setPlaybackRate(rate) {
    if (player) {
        player.setPlaybackRate(rate);
    }
}

export function getAvailablePlaybackRates() {
    return player ? player.getAvailablePlaybackRates() : [1];
}

export function getDuration() {
    return player ? player.getDuration() : 0;
}

export function seekTo(seconds) {
    if (player) {
        player.seekTo(seconds, true);
    }
}
