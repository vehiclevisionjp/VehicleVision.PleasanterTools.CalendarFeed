import { mount } from 'svelte';
import App from './App.svelte';
import './styles/app.scss';

mount(App, { target: document.getElementById('app')! });
