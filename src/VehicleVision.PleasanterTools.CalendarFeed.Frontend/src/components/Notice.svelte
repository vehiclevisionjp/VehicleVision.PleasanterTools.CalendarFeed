<script lang="ts">
  import type { Snippet } from 'svelte';

  let { kind = 'error', children }: { kind?: 'error' | 'ok' | 'warn'; children: Snippet } = $props();
</script>

<div class="notice {kind}" role={kind === 'error' ? 'alert' : 'status'}>
  {@render children()}
</div>
