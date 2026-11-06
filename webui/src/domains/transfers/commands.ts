/** Bound per-ID HTTP fan-out without rejecting a valid selection's size. */
export async function runTransferCommands(ids: readonly string[], command: (id: string) => Promise<unknown>): Promise<{ succeeded: number; failed: number; firstError?: string }> {
  let next = 0, succeeded = 0, failed = 0;
  let firstError: string | undefined;
  async function worker(): Promise<void> {
    while (next < ids.length) {
      const id = ids[next++]!;
      try { await command(id); succeeded++; }
      catch (cause) {
        failed++;
        firstError ??= cause instanceof Error ? cause.message : String(cause);
      }
    }
  }
  await Promise.all(Array.from({ length: Math.min(4, ids.length) }, worker));
  return { succeeded, failed, firstError };
}
