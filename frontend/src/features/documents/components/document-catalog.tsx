import { useTranslation } from 'react-i18next';
import { AlertTriangle, FileCheck } from 'lucide-react';

import type { Document } from '@/api';
import { Badge } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { documentStateVariant } from '@/lib/status';

export type DocumentV3Category = 'sources' | 'records' | 'delivery';

export interface DocumentCatalogProps {
  documents: Document[];
  onOpen: (documentId: string) => void;
  showTechnicalDetails?: boolean;
}

function normalizedTokens(doc: Document): string {
  return [doc.title, doc.kind, ...doc.classifications]
    .join(' ')
    .toLocaleLowerCase('pt-BR');
}

export function documentV3Category(doc: Document): DocumentV3Category {
  const tokens = normalizedTokens(doc);
  if (
    tokens.includes('buildmission') ||
    tokens.includes('validationmission') ||
    tokens.includes('context manifest') ||
    tokens.includes('manifesto de contexto') ||
    tokens.includes('execution') ||
    tokens.includes('execução') ||
    tokens.includes('validation report') ||
    tokens.includes('relatório de validação') ||
    tokens.includes('system-record') ||
    tokens.includes('technical-record') ||
    tokens.includes('mission')
  ) {
    return 'records';
  }

  if (
    tokens.includes('source') ||
    tokens.includes('fonte') ||
    tokens.includes('requirement') ||
    tokens.includes('requisito') ||
    tokens.includes('primary') ||
    tokens.includes('input') ||
    tokens.includes('referência') ||
    tokens.includes('reference') ||
    tokens.includes('protótipo') ||
    tokens.includes('prototype') ||
    doc.kind === 'prd' ||
    doc.kind === 'spec' ||
    doc.kind === 'design'
  ) {
    return 'sources';
  }

  return 'delivery';
}

/**
 * Catálogo de documentos: cards no mobile (<md) e tabela densa no desktop.
 * Flags de inconsistência e waiver aparecem junto ao estado.
 */
export function DocumentCatalog({
  documents,
  onOpen,
  showTechnicalDetails = false,
}: DocumentCatalogProps) {
  const { t, i18n } = useTranslation();

  const flags = (doc: Document) => (
    <>
      {doc.inconsistent && (
        <Badge variant="error">
          <AlertTriangle aria-hidden="true" className="size-3" />
          {t('documents.flags.inconsistent')}
        </Badge>
      )}
      {doc.waiver && (
        <Badge variant="info">
          <FileCheck aria-hidden="true" className="size-3" />
          {t('documents.flags.waiver')}
        </Badge>
      )}
    </>
  );

  return (
    <>
      {/* Cards (mobile) */}
      <ul className="flex flex-col gap-3 md:hidden" aria-label={t('documents.catalog.label')}>
        {documents.map((doc) => (
          <li key={doc.id}>
            <button
              type="button"
              onClick={() => onOpen(doc.id)}
              className="flex min-h-11 w-full flex-col items-start gap-2 rounded-xl border border-border bg-surface p-4 text-left transition-colors hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
              <span className="text-sm font-semibold">{doc.title}</span>
              <span className="flex flex-wrap items-center gap-2">
                <Badge variant="outline">{t(`status.documentKind.${doc.kind}`)}</Badge>
                <Badge variant="info">
                  {t(`documents.category.${documentV3Category(doc)}`)}
                </Badge>
                <Badge variant={documentStateVariant(doc.state)}>
                  {t(`status.documentState.${doc.state}`)}
                </Badge>
                {flags(doc)}
              </span>
              <span className="text-xs text-foreground-muted">
                {showTechnicalDetails
                  ? `${doc.phaseName ?? t('documents.catalog.noPhase')} · `
                  : null}
                {t('documents.detail.version', { version: doc.currentVersion })} ·{' '}
                {formatDateTime(doc.updatedAt, i18n.language)}
              </span>
            </button>
          </li>
        ))}
      </ul>

      {/* Tabela densa (desktop) */}
      <div className="hidden overflow-x-auto rounded-xl border border-border md:block">
        <table className="w-full text-sm" aria-label={t('documents.catalog.label')}>
          <thead>
            <tr className="border-b border-border bg-surface text-left text-xs text-foreground-muted">
              <th scope="col" className="px-3 py-2 font-medium">
                {t('documents.catalog.columns.title')}
              </th>
              <th scope="col" className="px-3 py-2 font-medium">
                {t('documents.catalog.columns.category')}
              </th>
              {showTechnicalDetails && (
                <th scope="col" className="px-3 py-2 font-medium">
                  {t('documents.catalog.columns.phase')}
                </th>
              )}
              <th scope="col" className="px-3 py-2 font-medium">
                {t('documents.catalog.columns.state')}
              </th>
              <th scope="col" className="px-3 py-2 font-medium">
                {t('documents.catalog.columns.version')}
              </th>
              <th scope="col" className="px-3 py-2 font-medium">
                {t('documents.catalog.columns.updatedAt')}
              </th>
            </tr>
          </thead>
          <tbody>
            {documents.map((doc) => (
              <tr
                key={doc.id}
                onClick={() => onOpen(doc.id)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    onOpen(doc.id);
                  }
                }}
                tabIndex={0}
                className="cursor-pointer border-b border-border last:border-0 hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
              >
                <td className="px-3 py-2 font-medium">
                  <span className="flex flex-wrap items-center gap-2">
                    {doc.title}
                    {flags(doc)}
                  </span>
                </td>
                <td className="px-3 py-2">
                  <span className="flex flex-wrap items-center gap-2">
                    <Badge variant="info">
                      {t(`documents.category.${documentV3Category(doc)}`)}
                    </Badge>
                    <span className="text-xs text-foreground-muted">
                      {t(`status.documentKind.${doc.kind}`)}
                    </span>
                  </span>
                </td>
                {showTechnicalDetails && (
                  <td className="px-3 py-2">{doc.phaseName ?? t('documents.catalog.noPhase')}</td>
                )}
                <td className="px-3 py-2">
                  <Badge variant={documentStateVariant(doc.state)}>
                    {t(`status.documentState.${doc.state}`)}
                  </Badge>
                </td>
                <td className="px-3 py-2">
                  {t('documents.detail.version', { version: doc.currentVersion })}
                </td>
                <td className="px-3 py-2 text-foreground-muted">
                  {formatDateTime(doc.updatedAt, i18n.language)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
