let escHandler = null;

export function addEscapeListener(dotNetRef) {
    removeEscapeListener();
    escHandler = (e) => {
        if (e.key === 'Escape') {
            dotNetRef.invokeMethodAsync('OnEscapePressed');
        }
    };
    window.addEventListener('keydown', escHandler);
}

export function removeEscapeListener() {
    if (escHandler) {
        window.removeEventListener('keydown', escHandler);
        escHandler = null;
    }
}

const MIN_VIDEO_HEIGHT = 80;
const MAX_VIDEO_HEIGHT = 500;

let resizeCleanup = null;

export function initVideoResize(wrapEl, handleEl, dotNetRef, initialHeightPx) {
    cleanupResizeListeners();
    applyHeight(wrapEl, initialHeightPx);

    let drag = null;

    const onPointerMove = (e) => {
        if (!drag) {
            return;
        }
        const height = clampHeight(drag.startHeight + (drag.startY - e.clientY));
        applyHeight(wrapEl, height);
        drag.height = height;
    };

    const onPointerUp = () => {
        if (!drag) {
            return;
        }
        const height = drag.height;
        drag = null;
        document.body.style.removeProperty('cursor');
        window.removeEventListener('pointermove', onPointerMove);
        window.removeEventListener('pointerup', onPointerUp);
        dotNetRef.invokeMethodAsync('OnVideoHeightChanged', height);
    };

    const onPointerDown = (e) => {
        e.preventDefault();
        drag = { startY: e.clientY, startHeight: wrapEl.getBoundingClientRect().height, height: 0 };
        drag.height = drag.startHeight;
        document.body.style.cursor = 'ns-resize';
        window.addEventListener('pointermove', onPointerMove);
        window.addEventListener('pointerup', onPointerUp);
    };

    handleEl.addEventListener('pointerdown', onPointerDown);
    resizeCleanup = () => {
        handleEl.removeEventListener('pointerdown', onPointerDown);
        window.removeEventListener('pointermove', onPointerMove);
        window.removeEventListener('pointerup', onPointerUp);
        document.body.style.removeProperty('cursor');
    };
}

export function endVideoResize(wrapEl) {
    cleanupResizeListeners();
    if (wrapEl) {
        wrapEl.style.removeProperty('height');
        wrapEl.style.removeProperty('width');
    }
}

function cleanupResizeListeners() {
    if (resizeCleanup) {
        resizeCleanup();
        resizeCleanup = null;
    }
}

function clampHeight(height) {
    return Math.round(Math.max(MIN_VIDEO_HEIGHT, Math.min(MAX_VIDEO_HEIGHT, height)));
}

function applyHeight(wrapEl, height) {
    wrapEl.style.height = `${clampHeight(height)}px`;
    wrapEl.style.width = 'auto';
}
