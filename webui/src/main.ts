import { mount } from 'svelte';
import Authentication from './Authentication.svelte';
import './themes.css';
import './ui.css';
import './app.css';

const target = document.getElementById('app');
if (!target) throw new Error('Missing #app mount point');

mount(Authentication, { target });
