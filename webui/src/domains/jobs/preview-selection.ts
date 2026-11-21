import { projectSelection } from '../../app/tree-selection';

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

export function projectPreviewSelection(
  total: number, subtreeCounts: ReadonlyMap<string, number>, defaultSelected: boolean, rules: readonly SubtreeRule[],
) {
  const included = new Set<string>();
  const excluded = new Set<string>();
  for (const rule of rules) (rule.selected ? included : excluded).add(rule.ref);
  return projectSelection(included, excluded, parentRef, ref => subtreeCounts.get(ref) ?? 0, defaultSelected, total);
}

function parentRef(ref: string): string | null {
  const separator = ref.lastIndexOf('/');
  return separator < 0 ? null : ref.slice(0, separator);
}
