import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';

import ptBR from './locales/pt-BR.json';
import en from './locales/en.json';

/**
 * Módulos de locale por feature (`locales/<lang>/<feature>.json`): cada
 * arquivo é dono de namespaces de topo próprios (ex.: "orchestrator"),
 * mesclados sobre o catálogo base. Evita conflito de edição no JSON único.
 */
function mergeFeatureModules(base: Record<string, unknown>, lang: string) {
  const modules = import.meta.glob<Record<string, unknown>>('./locales/*/*.json', {
    eager: true,
    import: 'default',
  });
  const merged = { ...base };
  for (const [path, module] of Object.entries(modules)) {
    if (!path.startsWith(`./locales/${lang}/`)) continue;
    for (const [namespace, entries] of Object.entries(module)) {
      if (namespace in merged) {
        throw new Error(`Namespace i18n duplicado "${namespace}" em ${path}.`);
      }
      merged[namespace] = entries;
    }
  }
  return merged;
}

export const SUPPORTED_LANGUAGES = ['pt-BR', 'en'] as const;
export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number];
export const DEFAULT_LANGUAGE: SupportedLanguage = 'pt-BR';

const LANGUAGE_STORAGE_KEY = 'poseidon-language';

export function getStoredLanguage(): SupportedLanguage {
  if (typeof localStorage === 'undefined') return DEFAULT_LANGUAGE;
  const stored = localStorage.getItem(LANGUAGE_STORAGE_KEY);
  return SUPPORTED_LANGUAGES.includes(stored as SupportedLanguage)
    ? (stored as SupportedLanguage)
    : DEFAULT_LANGUAGE;
}

export function persistLanguage(language: SupportedLanguage): void {
  if (typeof localStorage !== 'undefined') {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, language);
  }
  if (typeof document !== 'undefined') {
    document.documentElement.lang = language;
  }
}

/**
 * Chave ausente é defeito de produto: o i18next renderiza a própria chave e o
 * dono lê `status.chiefTurnState.delegating` na tela. Em desenvolvimento ela
 * estoura na hora; no build ela nem chega, porque o gate estático
 * (`src/i18n/__tests__/i18n-missing-keys.test.ts`) quebra antes.
 */
const failFastOnMissingKey = import.meta.env.DEV && import.meta.env.MODE !== 'test';

void i18n.use(initReactI18next).init({
  resources: {
    'pt-BR': { translation: mergeFeatureModules(ptBR, 'pt-BR') },
    en: { translation: mergeFeatureModules(en, 'en') },
  },
  lng: getStoredLanguage(),
  fallbackLng: DEFAULT_LANGUAGE,
  interpolation: {
    escapeValue: false, // React já faz escaping
  },
  returnNull: false,
  saveMissing: failFastOnMissingKey,
  missingKeyHandler: failFastOnMissingKey
    ? (_languages, _namespace, key) => {
        throw new Error(
          `Chave i18n ausente: "${key}". Adicione-a nos dois idiomas antes de usar.`,
        );
      }
    : undefined,
});

i18n.on('languageChanged', (language) => {
  persistLanguage(language as SupportedLanguage);
});

export default i18n;
