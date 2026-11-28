<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRoute, withBase } from 'vitepress'

// The WebUI of a daemon that does not serve its own manual links here with
// ?from=app. This website follows the newest version, so a link from an older
// daemon can name a page or section that has since moved: say so, rather than
// leaving the reader at a bare 404 or at the top of a page.
const props = defineProps<{ missing: 'page' | 'section' }>()
const route = useRoute()
const fromApp = ref(false)
const shown = ref(props.missing === 'page')
function check(): void {
  fromApp.value = new URLSearchParams(location.search).get('from') === 'app'
  if (props.missing === 'section')
    shown.value = fromApp.value && location.hash.length > 1 && !document.getElementById(decodeURIComponent(location.hash.slice(1)))
}
onMounted(check)
watch(() => [route.path, route.hash], check)

// The default theme has no way to open its search box other than its own button.
const search = (): void => document.querySelector<HTMLElement>('.VPNavBarSearchButton')?.click()
</script>

<template>
  <div v-if="shown" class="stale-link" :class="missing" :role="missing === 'section' ? 'note' : undefined">
    <template v-if="missing === 'page'">
      <p class="code">404</p>
      <h1>Page not found</h1>
    </template>
    <p v-if="fromApp" data-testid="stale-link-hint">
      Your Sockseek may be older than this manual, which describes the newest version.
      The {{ missing }} this link pointed to has been moved or renamed since.
    </p>
    <p class="actions">
      <button type="button" @click="search">Search the manual</button>
      <a v-if="missing === 'page'" :href="withBase('/')">Go to the start page</a>
    </p>
  </div>
</template>

<style>
.stale-link { font-size: 14px; line-height: 1.6; }
.stale-link.page { padding: 4rem 1.5rem 6rem; text-align: center; }
.stale-link.page > p { max-width: 28rem; margin: 1rem auto 0; color: var(--vp-c-text-2); }
.stale-link.page .code { margin: 0 auto; color: var(--vp-c-text-1); font-size: 4rem; font-weight: 600; line-height: 1; }
.stale-link.page h1 { padding-top: 0.75rem; font-size: 1.25rem; font-weight: 700; }
.stale-link.section { margin-bottom: 24px; padding: 12px 16px; border-radius: 8px; background: var(--vp-c-warning-soft); color: var(--vp-c-text-1); }
.stale-link .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
.stale-link.page .actions { justify-content: center; margin-top: 1.5rem; }
.stale-link .actions > * {
  border: 1px solid var(--vp-c-brand-1);
  border-radius: 1rem;
  padding: 0.1875rem 1rem;
  color: var(--vp-c-brand-1);
  font-weight: 500;
}
.stale-link .actions > *:hover { border-color: var(--vp-c-brand-2); color: var(--vp-c-brand-2); }
</style>
