import { h } from 'vue'
import DefaultTheme from 'vitepress/theme'
import BetaNotice from './BetaNotice.vue'
import StaleLink from './StaleLink.vue'

// The default theme, with a notice above it while the website documents a beta
// and an explanation for links that no longer lead where they did.
export default {
  extends: DefaultTheme,
  Layout: () => h(DefaultTheme.Layout, null, {
    'layout-top': () => h(BetaNotice),
    'not-found': () => h(StaleLink, { missing: 'page' }),
    'doc-before': () => h(StaleLink, { missing: 'section' }),
  }),
}
