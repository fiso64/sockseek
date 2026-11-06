import { mount } from 'svelte';
import Authentication from './Authentication.svelte';
import './app.css';

const target = document.getElementById('app');
if (!target) throw new Error('Missing #app mount point');

mount(Authentication, { target });
