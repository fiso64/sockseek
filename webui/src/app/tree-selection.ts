/** The nearest explicit ancestor determines the selection inherited by a node. */
export function inheritedSelection(
  key: string,
  included: ReadonlySet<string>,
  excluded: ReadonlySet<string>,
  parentOf: (key: string) => string | null,
  selectedByDefault = false,
): boolean {
  if (!included.size && !excluded.size) return selectedByDefault;
  const visited = new Set<string>([key]);
  let parent = parentOf(key);
  while (parent !== null && !visited.has(parent)) {
    visited.add(parent);
    // Exclusion wins at one identity; a nearer inclusion restores a branch.
    if (excluded.has(parent)) return false;
    if (included.has(parent)) return true;
    parent = parentOf(parent);
  }
  return selectedByDefault;
}

/** Count rule transitions, not expanded files. Nested exclusions are not
 * subtracted twice, and explicit re-inclusions add only their own subtree.
 */
export function projectSelection(
  included: ReadonlySet<string>,
  excluded: ReadonlySet<string>,
  parentOf: (key: string) => string | null,
  countOf: (key: string) => number,
  selectedByDefault = false,
  totalCount = 0,
) {
  let count = selectedByDefault ? totalCount : 0;
  const descendantDeltas = new Map<string, number>();
  for (const key of new Set([...included, ...excluded])) {
    const inherited = inheritedSelection(key, included, excluded, parentOf, selectedByDefault);
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
  function subtreeCount(key: string): number {
    const selected = !excluded.has(key) && (included.has(key)
      || inheritedSelection(key, included, excluded, parentOf, selectedByDefault));
    return (selected ? countOf(key) : 0) + (descendantDeltas.get(key) ?? 0);
  }
  return { total: count, subtreeCount };
}
