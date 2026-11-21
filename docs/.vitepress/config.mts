import { posix } from 'node:path'
import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Sockseek',
  description: 'Manual for the Sockseek Soulseek client, daemon and CLI',
  base: './',
  srcDir: 'pages',
  vite: { publicDir: '../../webui/public' },
  transformHead({ pageData }) {
    const href = posix.relative(posix.dirname(pageData.relativePath), 'sockseek-favicon.svg')
    return [['link', { rel: 'icon', type: 'image/svg+xml', href }]]
  },
  themeConfig: {
    search: { provider: 'local' },
    socialLinks: [{ icon: 'github', link: 'https://github.com/fiso64/sockseek', ariaLabel: 'Sockseek on GitHub' }],
    sidebar: [
      { text: 'Using Sockseek', items: [
        { text: 'Getting started', link: '/' },
        { text: 'Local CLI downloads', link: '/using-sockseek/one-shot' },
        { text: 'Daemon and remote access', link: '/using-sockseek/daemon' },
        { text: 'Configuration', link: '/using-sockseek/configuration' },
      ] },
      { text: 'Installation', items: [
        { text: 'Release archive', link: '/installation/release-archive' },
        { text: 'NixOS', link: '/installation/nixos' },
        { text: 'Docker Compose', link: '/installation/docker' },
      ] },
      { text: 'Search and download', items: [
        { text: 'File conditions', link: '/search-and-download/file-conditions' },
        { text: 'Name format', link: '/search-and-download/name-format' },
      ] },
      { text: 'Automated & bulk downloads', items: [
        { text: 'Input types', link: '/automated-and-bulk-downloads/inputs' },
        { text: 'Download modes', link: '/automated-and-bulk-downloads/download-modes' },
        { text: 'On-complete actions', link: '/automated-and-bulk-downloads/on-complete' },
        { text: 'Examples', link: '/automated-and-bulk-downloads/examples' },
        { text: 'Troubleshooting & Tips', link: '/automated-and-bulk-downloads/tips' },
      ] },
      { text: 'Reference', items: [
        { text: 'Daemon and remote CLI', link: '/reference/daemon-options' },
        { text: 'Download options', link: '/reference/download-options' },
        { text: 'Variables', link: '/reference/variables' },
        { text: 'CLI shortcuts', link: '/reference/shortcuts' },
        { text: 'API', link: '/reference/api' },
      ] },
    ],
  },
})
