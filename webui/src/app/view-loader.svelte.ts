/** Cache code, not mounted state. Browsers can cache import failures until reload. */
export class ViewLoader<T> {
  component = $state.raw<T>();
  failed = $state(false);
  private started = false;

  constructor(private readonly importView: () => Promise<{ default: T }>) {}

  load(): void {
    if (this.started) return;
    this.started = true;
    void this.importView()
      .then(module => { this.component = module.default; })
      .catch(() => { this.failed = true; });
  }
}
