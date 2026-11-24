<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useData } from 'vitepress'

const { theme } = useData()
// Shown until dismissed, then hidden for this browser. It is in the built page
// so that someone arriving from a search engine sees it without waiting.
const dismissedKey = 'sockseek-docs-beta-notice-dismissed'
const dismissed = ref(false)
onMounted(() => { dismissed.value = localStorage.getItem(dismissedKey) === '1' })
function dismiss(): void {
  dismissed.value = true
  localStorage.setItem(dismissedKey, '1')
}
</script>

<template>
  <div v-if="theme.betaNotice && !dismissed" class="beta-notice" role="note">
    <span>
      {{ theme.betaNotice.text }}
      <a :href="theme.betaNotice.link">{{ theme.betaNotice.linkText }}</a>
    </span>
    <button type="button" aria-label="Dismiss this notice" title="Dismiss" @click="dismiss">×</button>
  </div>
</template>

<style>
/* The default theme offsets its fixed navigation by this height. */
:root:has(.beta-notice) { --vp-layout-top-height: 36px; }
.beta-notice {
  position: fixed;
  inset: 0 0 auto 0;
  z-index: var(--vp-z-index-layout-top);
  height: var(--vp-layout-top-height);
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 0 12px;
  /* The tint is translucent; paint it over the page colour so text scrolling
     underneath does not show through. */
  background: linear-gradient(var(--vp-c-warning-soft), var(--vp-c-warning-soft)), var(--vp-c-bg);
  color: var(--vp-c-text-1);
  font-size: 13px;
  line-height: 1.3;
  text-align: center;
}
.beta-notice a { color: var(--vp-c-brand-1); font-weight: 500; text-decoration: underline; }
.beta-notice button { flex: none; width: 24px; height: 24px; border-radius: 4px; color: var(--vp-c-text-2); font-size: 18px; line-height: 1; }
.beta-notice button:hover { background: var(--vp-c-default-soft); color: var(--vp-c-text-1); }
@media (max-width: 520px) {
  :root:has(.beta-notice) { --vp-layout-top-height: 52px; }
}
</style>
