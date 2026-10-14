<script lang="ts">
  import { api } from '../lib/api';
  import { errorText } from '../lib/errors';
  import { emptyFeedForm, formFromFeed, toFeedInput } from '../lib/feedForm';
  import { language, t } from '../lib/i18n/state.svelte';
  import { LANGUAGES, LANGUAGE_NAMES } from '../lib/i18n/messages';
  import type { Feed } from '../lib/types';
  import Modal from './Modal.svelte';
  import Notice from './Notice.svelte';

  /** `feed` があれば編集（購読 URL と API キーは変わらない）、なければ作成。 */
  let {
    feed = null,
    canWrite,
    ondone,
    oncancel,
  }: {
    feed?: Feed | null;
    canWrite: boolean;
    ondone: (saved: Feed, url: string | null) => void;
    oncancel: () => void;
  } = $props();

  // svelte-ignore state_referenced_locally
  let form = $state(feed ? formFromFeed(feed) : emptyFeedForm(language()));
  let busy = $state(false);
  let error = $state('');

  const editing = $derived(feed !== null);

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    const input = toFeedInput(form);
    if (!input) {
      error = t('errorGeneric');
      return;
    }

    busy = true;
    error = '';
    if (feed) {
      const { apiKey: _ignored, ...update } = input;
      const saved = await api.updateFeed(feed.id, update);
      busy = false;
      if (!saved.ok) {
        error = errorText(saved);
        return;
      }

      ondone(saved.value, null);
      return;
    }

    const created = await api.createFeed(input);
    busy = false;
    if (!created.ok) {
      error = errorText(created);
      return;
    }

    ondone(created.value.feed, created.value.url);
  }
</script>

<Modal title={editing ? t('feedEditTitle') : t('feedNew')} wide onclose={oncancel}>
  {#if error}<Notice>{error}</Notice>{/if}
  <form onsubmit={submit}>
    <div class="grid">
      <div class="field">
        <label for="f-name">{t('feedName')}<span class="mark">{t('requiredMark')}</span></label>
        <input id="f-name" type="text" required maxlength="200" bind:value={form.name} />
        <div class="hint">{t('feedNameHint')}</div>
      </div>
      <div class="field">
        <label for="f-site">{t('feedSiteId')}<span class="mark">{t('requiredMark')}</span></label>
        <input id="f-site" type="number" min="1" required bind:value={form.siteId} />
      </div>
      <div class="field">
        <label for="f-lang">{t('feedLanguage')}</label>
        <select id="f-lang" bind:value={form.language}>
          {#each LANGUAGES as code (code)}<option value={code}>{LANGUAGE_NAMES[code]}</option>{/each}
        </select>
        <div class="hint">{t('feedLanguageHint')}</div>
      </div>
      <div class="field">
        <label for="f-tz">{t('feedTimeZone')}<span class="mark">{t('optionalMark')}</span></label>
        <input id="f-tz" type="text" placeholder="Asia/Tokyo" bind:value={form.apiKeyUserTimeZoneId} />
        <div class="hint">{t('feedTimeZoneHint')}</div>
      </div>
    </div>

    {#if !editing}
      <div class="field">
        <label for="f-key">{t('feedApiKey')}<span class="mark">{t('optionalMark')}</span></label>
        <input id="f-key" type="password" autocomplete="off" bind:value={form.apiKey} />
        <div class="hint">{t('feedApiKeyHint')}</div>
      </div>
    {/if}

    <fieldset>
      <legend>{t('feedMapping')}</legend>
      <div class="grid">
        <div class="field">
          <label for="m-sum">{t('mapSummaryColumn')}</label>
          <input id="m-sum" type="text" bind:value={form.summaryColumn} />
        </div>
        <div class="field">
          <label for="m-tpl">{t('mapSummaryTemplate')}<span class="mark">{t('optionalMark')}</span></label>
          <input id="m-tpl" type="text" bind:value={form.summaryTemplate} />
          <div class="hint">{t('mapSummaryTemplateHint')}</div>
        </div>
        <div class="field">
          <label for="m-start">{t('mapStart')}</label>
          <input id="m-start" type="text" bind:value={form.startColumn} />
        </div>
        <div class="field">
          <label for="m-startf">{t('mapStartFormat')}<span class="mark">{t('optionalMark')}</span></label>
          <input id="m-startf" type="text" placeholder="yyyy/MM/dd HH:mm" bind:value={form.startFormat} />
        </div>
        <div class="field">
          <label for="m-end">{t('mapEnd')}<span class="mark">{t('optionalMark')}</span></label>
          <input id="m-end" type="text" bind:value={form.endColumn} />
        </div>
        <div class="field">
          <label for="m-endf">{t('mapEndFormat')}<span class="mark">{t('optionalMark')}</span></label>
          <input id="m-endf" type="text" placeholder="yyyy/MM/dd HH:mm" bind:value={form.endFormat} />
        </div>
        <div class="field">
          <label for="m-desc">{t('mapDescription')}<span class="mark">{t('optionalMark')}</span></label>
          <input id="m-desc" type="text" bind:value={form.descriptionColumn} />
        </div>
        <div class="field">
          <label for="m-loc">{t('mapLocation')}<span class="mark">{t('optionalMark')}</span></label>
          <input id="m-loc" type="text" bind:value={form.locationColumn} />
        </div>
        <div class="field">
          <label for="m-all">{t('mapAllDay')}</label>
          <select id="m-all" bind:value={form.allDay}>
            <option value="Auto">{t('mapAllDayAuto')}</option>
            <option value="Never">{t('mapAllDayNever')}</option>
            <option value="Always">{t('mapAllDayAlways')}</option>
          </select>
        </div>
        <div class="field">
          <label for="m-dur">{t('mapDuration')}</label>
          <input id="m-dur" type="number" min="1" bind:value={form.defaultDurationMinutes} />
        </div>
      </div>
      <p class="hint muted small">{t('mapFormatHint')}</p>
    </fieldset>

    <div class="field">
      <label for="f-filters">{t('feedFilters')}<span class="mark">{t('optionalMark')}</span></label>
      <textarea id="f-filters" spellcheck="false" bind:value={form.filters}></textarea>
      <div class="hint">{t('feedFiltersHint')}</div>
    </div>

    <div class="dialog-actions">
      <button type="button" class="btn" onclick={oncancel}>{t('cancel')}</button>
      <button type="submit" class="btn primary" disabled={busy || !canWrite}>{editing ? t('save') : t('create')}</button>
    </div>
  </form>
</Modal>
