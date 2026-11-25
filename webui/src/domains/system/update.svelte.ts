// The release whose announcement this browser has dismissed. Dismissing hides
// the Status notice and the navigation badge until the next release; the
// version line on the Status tab keeps saying it.
const key = 'sockseek-update-dismissed';
let dismissed = $state(localStorage.getItem(key));

export const dismissedUpdate = {
  get version(): string | null { return dismissed; },
  dismiss(version: string): void {
    dismissed = version;
    localStorage.setItem(key, version);
  },
};
