/**
 * The search bar's settings apply to the next search; an open results page has
 * its own. The search bar uses this to point from one to the other.
 */
export const searchPanels = $state({
  /** Results pages currently shown. */
  resultsShown: 0,
  /** Incremented to ask the shown results page to open its settings. */
  openResultsSettings: 0,
});
