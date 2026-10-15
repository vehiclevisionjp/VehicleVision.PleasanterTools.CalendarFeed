<script lang="ts">
  import { t } from '../lib/i18n/state.svelte';

  let { value, label }: { value: string; label: string } = $props();
  let copied = $state(false);

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      copied = true;
      setTimeout(() => (copied = false), 2000);
    } catch {
      // クリップボードを使えない環境（非 HTTPS など）では、選択してコピーしてもらう
    }
  }
</script>

<div class="copy-row">
  <input type="text" readonly {value} aria-label={label} onfocus={(e) => e.currentTarget.select()} />
  <button type="button" class="btn" onclick={copy}><span class="material-icons" aria-hidden="true">{copied ? 'check' : 'content_copy'}</span>{copied ? t('copied') : t('copy')}</button>
</div>
