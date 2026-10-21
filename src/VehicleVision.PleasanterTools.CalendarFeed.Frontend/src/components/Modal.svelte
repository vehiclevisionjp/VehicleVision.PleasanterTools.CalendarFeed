<script lang="ts">
  import type { Snippet } from 'svelte';
  import { t } from '../lib/i18n/state.svelte';

  let {
    title,
    wide = false,
    onclose,
    children,
  }: { title: string; wide?: boolean; onclose: () => void; children: Snippet } = $props();

  let dialog: HTMLDialogElement | undefined = $state();

  // 表示したら、モーダルとして開く（フォーカスの閉じ込め・Esc は <dialog> が行う）
  $effect(() => {
    if (dialog && !dialog.open) {dialog.showModal();}
  });
</script>

<dialog bind:this={dialog} class:wide aria-labelledby="modal-title" onclose={onclose}>
  <h2 id="modal-title">{title}</h2>
  {@render children()}
  <span class="visually-hidden">{t('close')}</span>
</dialog>
