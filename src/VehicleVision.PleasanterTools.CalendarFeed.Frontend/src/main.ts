// 書体とアイコンは同梱する（端末の書体に依存させない。CSP の font-src 'self' で配る）
import '@fontsource-variable/noto-sans-jp';
import '@fontsource-variable/m-plus-1-code';
import '@fontsource/material-icons';
import { mount } from 'svelte';
import App from './App.svelte';
import './styles/app.scss';

mount(App, { target: document.getElementById('app')! });
