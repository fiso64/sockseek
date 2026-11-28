import { mount } from 'svelte';
import Authentication from './Authentication.svelte';
import '@fontsource-variable/inter/wght.css';
import './themes.css';
import './ui.css';
import './app.css';

// The browser chrome of an installed app matches the configured theme and scheme.
const themeColor = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
const syncThemeColor = () => { if (themeColor) themeColor.content = getComputedStyle(document.body).backgroundColor; };
syncThemeColor();
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', syncThemeColor);

const target = document.getElementById('app');
if (!target) throw new Error('Missing #app mount point');

mount(Authentication, { target });
