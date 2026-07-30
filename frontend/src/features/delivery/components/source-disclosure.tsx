import { useTranslation } from 'react-i18next';

export interface SourceDisclosureProps {
  source: string;
  updatedAt: string;
  calculation: string;
  confidence: string;
  missing?: readonly string[];
  technical?: string;
}

/**
 * Explica de onde veio um sinal operacional sem poluir a leitura principal.
 * O elemento nativo details preserva teclado, leitor de tela e progressive disclosure.
 */
export function SourceDisclosure({
  source,
  updatedAt,
  calculation,
  confidence,
  missing = [],
  technical,
}: SourceDisclosureProps) {
  const { t, i18n } = useTranslation();
  const parsed = new Date(updatedAt);
  const formatted = Number.isNaN(parsed.getTime())
    ? t('delivery.sources.unavailable')
    : new Intl.DateTimeFormat(i18n.language, {
        dateStyle: 'short',
        timeStyle: 'short',
      }).format(parsed);

  return (
    <details className="rounded-md border border-border px-3 py-2 text-xs text-foreground-muted">
      <summary className="cursor-pointer font-medium text-foreground">
        {t('delivery.sources.open')}
      </summary>
      <dl className="mt-2 grid gap-2 md:grid-cols-2">
        <div>
          <dt className="font-medium text-foreground">{t('delivery.sources.source')}</dt>
          <dd>{source}</dd>
        </div>
        <div>
          <dt className="font-medium text-foreground">{t('delivery.sources.updated')}</dt>
          <dd>{formatted}</dd>
        </div>
        <div>
          <dt className="font-medium text-foreground">{t('delivery.sources.calculation')}</dt>
          <dd>{calculation}</dd>
        </div>
        <div>
          <dt className="font-medium text-foreground">{t('delivery.sources.confidence')}</dt>
          <dd>{confidence}</dd>
        </div>
      </dl>
      {missing.length > 0 && (
        <div className="mt-2">
          <p className="font-medium text-foreground">{t('delivery.sources.missing')}</p>
          <ul className="list-disc pl-5">
            {missing.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </div>
      )}
      {technical && (
        <p className="mt-2 break-words">
          <span className="font-medium text-foreground">{t('delivery.sources.technical')}: </span>
          {technical}
        </p>
      )}
    </details>
  );
}
