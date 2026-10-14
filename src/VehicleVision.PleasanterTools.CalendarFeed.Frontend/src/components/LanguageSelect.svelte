<script lang="ts">
  import { chooseLanguage, language, t } from '../lib/i18n/state.svelte';
  import { LANGUAGES, LANGUAGE_NAMES, type Language } from '../lib/i18n/messages';
</script>

<select
  class="lang"
  aria-label={t('language')}
  value={language()}
  onchange={(e) => chooseLanguage(e.currentTarget.value as Language)}
>
  {#each LANGUAGES as code (code)}
    <option value={code}>{LANGUAGE_NAMES[code]}</option>
  {/each}
</select>
