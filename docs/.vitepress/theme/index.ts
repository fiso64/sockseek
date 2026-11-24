import { h } from 'vue'
import DefaultTheme from 'vitepress/theme'
import BetaNotice from './BetaNotice.vue'

// The default theme, with a notice above it while the website documents a beta.
export default {
  extends: DefaultTheme,
  Layout: () => h(DefaultTheme.Layout, null, { 'layout-top': () => h(BetaNotice) }),
}
