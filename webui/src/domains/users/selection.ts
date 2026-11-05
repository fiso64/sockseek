/** Exact client-owned browse identities: d:<directory ID> or f:<file ID>. */
export function inheritedSelection(
  key: string,
  included: ReadonlySet<string>,
  excluded: ReadonlySet<string>,
  parentOf: (key: string) => string | null,
): boolean {
  const visited = new Set<string>([key]);
  let parent = parentOf(key);
  while (parent !== null && !visited.has(parent)) {
    visited.add(parent);
    // Exclusion wins at one identity; a nearer inclusion restores a branch.
    if (excluded.has(parent)) return false;
    if (included.has(parent)) return true;
    parent = parentOf(parent);
  }
  return false;
}

/** Count rule transitions, not expanded files. Nested exclusions are not
 * subtracted twice, and explicit re-inclusions add only their own subtree.
 */
export function selectionCount(
  included: ReadonlySet<string>,
  excluded: ReadonlySet<string>,
  parentOf: (key: string) => string | null,
  countOf: (key: string) => number,
): number {
  return projectSelection(included, excluded, parentOf, countOf).total;
}

export function projectSelection(
  included: ReadonlySet<string>,
  excluded: ReadonlySet<string>,
  parentOf: (key: string) => string | null,
  countOf: (key: string) => number,
): { total: number; descendantDeltas: ReadonlyMap<string, number> } {
  let count = 0;
  const descendantDeltas = new Map<string, number>();
  for (const key of new Set([...included, ...excluded])) {
    const inherited = inheritedSelection(key, included, excluded, parentOf);
    const selected = included.has(key) && !excluded.has(key);
    if (selected === inherited) continue;
    const delta = (selected ? 1 : -1) * countOf(key);
    count += delta;
    const visited = new Set<string>([key]);
    let parent = parentOf(key);
    while (parent !== null && !visited.has(parent)) {
      visited.add(parent);
      descendantDeltas.set(parent, (descendantDeltas.get(parent) ?? 0) + delta);
      parent = parentOf(parent);
    }
  }
  return { total: count, descendantDeltas };
}
