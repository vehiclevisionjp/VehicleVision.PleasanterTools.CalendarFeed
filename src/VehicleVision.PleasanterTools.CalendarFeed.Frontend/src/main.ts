// 文字の書体は同梱しない（DesignReference の決まり。端末の Noto 系 → OS の書体の順で使う）。アイコンの Material Icons だけ同梱する（CSP の font-src 'self' で配る）
import '@fontsource/material-icons';
import { mount } from 'svelte';
import App from './App.svelte';
import './styles/app.scss';

mount(App, { target: document.getElementById('app')! });
