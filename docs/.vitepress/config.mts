import { posix } from 'node:path'
import { defineConfig } from 'vitepress'

// The manual is built two ways. Bundled with the daemon it is served from
// wherever the daemon is mounted, so its links are relative. Published as a
// website (SOCKSEEK_DOCS_SITE_URL, e.g. https://example.github.io/sockseek/) it
// has one address, which search engines need for canonical links and a sitemap.
const site = process.env.SOCKSEEK_DOCS_SITE_URL ? new URL(process.env.SOCKSEEK_DOCS_SITE_URL) : null
// Shown on the website only, until 4.0 is the released version: remove with the beta.
const betaNotice = site && {
  text: 'These docs describe the 4.0 beta. Using 3.x?',
  linkText: 'Read the 3.x manual',
  link: 'https://github.com/fiso64/sockseek/blob/v3.1.0/README.md',
}

export default defineConfig({
  title: 'Sockseek',
  description: 'Manual for the Sockseek Soulseek client, daemon and CLI',
  base: site ? site.pathname : './',
  ...(site ? { sitemap: { hostname: site.href }, cleanUrls: true } : {}),
  srcDir: 'pages',
  vite: { publicDir: '../../webui/public' },
  transformHead({ pageData }) {
    const href = posix.relative(posix.dirname(pageData.relativePath), 'sockseek-favicon.svg')
    return [['link', { rel: 'icon', type: 'image/svg+xml', href }]]
  },
  themeConfig: {
    betaNotice,
    search: { provider: 'local' },
    socialLinks: [{ icon: 'github', link: 'https://github.com/fiso64/sockseek', ariaLabel: 'Sockseek on GitHub' }],
    sidebar: [
      { text: 'Using Sockseek', items: [
        { text: 'Getting started', link: '/' },
        { text: 'Local CLI downloads', link: '/using-sockseek/one-shot' },
        { text: 'Daemon and remote access', link: '/using-sockseek/daemon' },
        { text: 'Configuration', link: '/using-sockseek/configuration' },
        { text: 'Coming from 3.x', link: '/using-sockseek/coming-from-v3' },
      ] },
      { text: 'Installation', items: [
        { text: 'Release archive', link: '/installation/release-archive' },
        { text: 'Debian & Ubuntu', link: '/installation/debian' },
        { text: 'NixOS', link: '/installation/nixos' },
        { text: 'Docker Compose', link: '/installation/docker' },
      ] },
      { text: 'Downloading', items: [
        { text: 'Input types', link: '/downloading/inputs' },
        { text: 'Download modes', link: '/downloading/download-modes' },
        { text: 'File conditions', link: '/downloading/file-conditions' },
        { text: 'Name format', link: '/downloading/name-format' },
        { text: 'On-complete actions', link: '/downloading/on-complete' },
        { text: 'Troubleshooting & tips', link: '/downloading/tips' },
      ] },
      { text: 'Reference', items: [
        { text: 'Options', link: '/reference/options' },
        { text: 'Daemon and remote CLI', link: '/reference/daemon-options' },
        { text: 'Variables', link: '/reference/variables' },
        { text: 'CLI shortcuts', link: '/reference/shortcuts' },
        { text: 'API', link: '/reference/api' },
      ] },
    ],
  },
})
