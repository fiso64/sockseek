/** Copy text for the user. The async clipboard needs a secure context, which a
 * daemon reached over plain HTTP on a LAN is not, so fall back to a selection. */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch { /* Denied or unavailable: try the selection below. */ }
  const field = document.createElement('textarea');
  field.value = text;
  field.setAttribute('readonly', '');
  field.style.cssText = 'position:fixed;inset:0 auto auto 0;opacity:0;';
  // Inside an open modal, so its focus trap keeps the selection.
  const focused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  ([...document.querySelectorAll('[role="dialog"][aria-modal="true"]')].at(-1) ?? document.body).append(field);
  try {
    field.focus();
    field.select();
    // A copy of an empty selection also "succeeds"; require the text selected.
    return field.selectionEnd - field.selectionStart === text.length && text.length > 0
      && document.activeElement === field && document.execCommand('copy');
  } catch { return false; }
  finally {
    field.remove();
    focused?.focus();
  }
}
