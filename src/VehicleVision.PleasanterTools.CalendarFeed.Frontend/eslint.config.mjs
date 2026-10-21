// {} の省略を禁止する（if / else / for / while / do の本文。1 行で済む本文も同じ）。
// 他の規則は入れない（この設定は、省略の禁止だけのためにある）。
import svelte from 'eslint-plugin-svelte';
import tseslint from 'typescript-eslint';

const rules = { curly: ['error', 'all'] };

export default [
  { ignores: ['dist/**', 'node_modules/**'] },
  {
    files: ['**/*.ts'],
    languageOptions: { parser: tseslint.parser },
    rules,
  },
  ...svelte.configs['flat/base'],
  {
    files: ['**/*.svelte', '**/*.svelte.ts'],
    languageOptions: { parserOptions: { parser: tseslint.parser } },
    rules,
  },
];
