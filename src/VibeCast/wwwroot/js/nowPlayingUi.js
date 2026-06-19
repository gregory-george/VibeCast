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
