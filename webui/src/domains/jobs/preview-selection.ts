export type SubtreeRule = { ref: string; selected: boolean };

function contains(ancestor: string, ref: string): boolean {
  return ref === ancestor || ref.startsWith(`${ancestor}/`);
}

export function effectiveSelection(ref: string, defaultSelected: boolean, rules: readonly SubtreeRule[]): boolean {
  let selected = defaultSelected;
  let longest = -1;
  for (const rule of rules) {
    if (rule.ref.length > longest && contains(rule.ref, ref)) {
      selected = rule.selected;
      longest = rule.ref.length;
    }
  }
  return selected;
}

export function toggleSubtree(
  ref: string,
  selected: boolean,
  defaultSelected: boolean,
  rules: readonly SubtreeRule[],
): SubtreeRule[] {
  const outside = rules.filter((rule) => !contains(ref, rule.ref));
  return selected === effectiveSelection(ref, defaultSelected, outside)
    ? outside
    : [...outside, { ref, selected }];
}

/** Counts include a selectable node itself as well as selectable descendants. */
export function selectedSubtreeCount(
  ref: string,
  subtreeCounts: ReadonlyMap<string, number>,
  defaultSelected: boolean,
  rules: readonly SubtreeRule[],
): number {
  const count = subtreeCounts.get(ref) ?? 0;
  let selected = effectiveSelection(ref, defaultSelected, rules) ? count : 0;
  const nested = rules.filter((rule) => rule.ref !== ref && contains(ref, rule.ref))
    .sort((a, b) => a.ref.length - b.ref.length);
  for (const rule of nested) {
    const parentSelection = effectiveSelection(rule.ref, defaultSelected, rules.filter((candidate) => candidate.ref !== rule.ref));
    if (rule.selected !== parentSelection)
      selected += (rule.selected ? 1 : -1) * (subtreeCounts.get(rule.ref) ?? 0);
  }
  return selected;
}

export function selectedPreviewCount(
  total: number,
  subtreeCounts: ReadonlyMap<string, number>,
  defaultSelected: boolean,
  rules: readonly SubtreeRule[],
): number {
  let selected = defaultSelected ? total : 0;
  const ordered = [...rules].sort((a, b) => a.ref.length - b.ref.length);
  for (const rule of ordered) {
    const inherited = effectiveSelection(rule.ref, defaultSelected, ordered.filter((candidate) => candidate.ref !== rule.ref));
    if (rule.selected !== inherited)
      selected += (rule.selected ? 1 : -1) * (subtreeCounts.get(rule.ref) ?? 0);
  }
  return selected;
}
