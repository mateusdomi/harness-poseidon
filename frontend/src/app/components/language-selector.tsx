import { useTranslation } from 'react-i18next';

import { SUPPORTED_LANGUAGES, type SupportedLanguage } from '@/i18n';

export function LanguageSelector() {
  const { t, i18n } = useTranslation();

  return (
    <label className="flex items-center gap-2 text-sm text-foreground-muted">
      <span className="sr-only">{t('shell.language.label')}</span>
      <select
        aria-label={t('shell.language.label')}
        value={i18n.language}
        onChange={(event) => void i18n.changeLanguage(event.target.value as SupportedLanguage)}
        className="h-11 rounded-md border border-border-strong bg-surface px-2 text-sm text-foreground focus-visible:outline-none"
      >
        {SUPPORTED_LANGUAGES.map((language) => (
          <option key={language} value={language}>
            {t(`shell.language.${language}`)}
          </option>
        ))}
      </select>
    </label>
  );
}
