// The daemon supplies the root relative to the originally requested bookmark.
// Keep it stable as pushState changes the document URL.
const base = typeof document === 'undefined' ? null : document.querySelector('base');
if (base) base.href = base.href;
const prefix = base ? new URL(base.href).pathname.replace(/\/$/, '') : '';

export function appPath(path: string): string {
  return prefix + (path.startsWith('/') ? path : '/' + path);
}

export function routePathname(): string {
  const path = window.location.pathname;
  return path.slice(prefix.length) || '/';
}
