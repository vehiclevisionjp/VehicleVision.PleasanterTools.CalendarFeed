<script lang="ts">
  import { t } from '../lib/i18n/state.svelte';
  import Modal from './Modal.svelte';

  let {
    title,
    message,
    danger = false,
    busy = false,
    onconfirm,
    oncancel,
  }: { title: string; message: string; danger?: boolean; busy?: boolean; onconfirm: () => void; oncancel: () => void } = $props();
</script>

<Modal {title} onclose={oncancel}>
  <p>{message}</p>
  <div class="dialog-actions">
    <button type="button" class="btn" onclick={oncancel}>{t('cancel')}</button>
    <button type="button" class="btn {danger ? 'danger' : 'primary'}" disabled={busy} onclick={onconfirm}>{t('confirm')}</button>
  </div>
</Modal>
