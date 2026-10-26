<script lang="ts">
  import { t } from '../lib/i18n/state.svelte';

  let { id, value = $bindable('') }: { id: string; value?: string } = $props();
</script>

<div class="field">
  <label for={id}>{t('password')}</label>
  <input {id} type="password" autocomplete="current-password" required bind:value />
</div>
