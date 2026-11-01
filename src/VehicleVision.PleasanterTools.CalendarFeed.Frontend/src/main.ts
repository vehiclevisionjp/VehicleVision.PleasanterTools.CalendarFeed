// 書体とアイコンは同梱する（端末の書体に依存させない。CSP の font-src 'self' で配る）。
// 書体は DesignReference 第 2.2 版の app-fonts.css（Noto Sans CJK JP・Myrica M。sync-app-fonts.mjs で複写。出どころは src/fonts/app/SOURCE.json）
import './styles/design-reference/app-fonts.css';
import '@fontsource/material-icons';
import { mount } from 'svelte';
import App from './App.svelte';
import './styles/app.scss';

mount(App, { target: document.getElementById('app')! });
