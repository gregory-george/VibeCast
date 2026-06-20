export function setTheme(theme) {
    if (theme === 'System') {
        document.documentElement.removeAttribute('data-theme');
    } else {
        document.documentElement.setAttribute('data-theme', theme.toLowerCase());
    }
}
