import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';

import ptBR from './locales/pt-BR.json';
import en from './locales/en.json';

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

void i18n.use(initReactI18next).init({
  resources: {
    'pt-BR': { translation: ptBR },
    en: { translation: en },
  },
  lng: getStoredLanguage(),
  fallbackLng: DEFAULT_LANGUAGE,
  interpolation: {
    escapeValue: false, // React já faz escaping
  },
  returnNull: false,
});

i18n.on('languageChanged', (language) => {
  persistLanguage(language as SupportedLanguage);
});

export default i18n;
