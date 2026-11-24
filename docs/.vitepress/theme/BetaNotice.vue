<script setup lang="ts">
import { useData } from 'vitepress'

const { theme } = useData()
</script>

<template>
  <div v-if="theme.betaNotice" class="beta-notice" role="note">
    {{ theme.betaNotice.text }}
    <a :href="theme.betaNotice.link">{{ theme.betaNotice.linkText }}</a>
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
  gap: 6px;
  padding: 0 12px;
  background: var(--vp-c-warning-soft);
  color: var(--vp-c-text-1);
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
}
.beta-notice a { color: var(--vp-c-brand-1); font-weight: 500; text-decoration: underline; }
@media (max-width: 520px) {
  :root:has(.beta-notice) { --vp-layout-top-height: 52px; }
  .beta-notice { display: block; padding-top: 9px; white-space: normal; text-align: center; line-height: 1.3; }
}
</style>
