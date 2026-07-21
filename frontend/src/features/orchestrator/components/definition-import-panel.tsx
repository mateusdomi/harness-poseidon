import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Download, Upload } from 'lucide-react';

import { Button } from '@/design-system';
import {
  definitionTemplateJson,
  parseDefinitionImport,
  type ImportIssue,
} from '@/features/orchestrator/lib/definition-import';
import type { DefinitionFormValues } from '@/features/orchestrator/lib/definitions-form';

export interface DefinitionImportPanelProps {
  /** Aplica os valores importados como rascunho no formulário. */
  onApply: (values: DefinitionFormValues) => void;
}

/**
 * Template e importação de definição (§16.8): baixar um modelo JSON, importar
 * um arquivo, ver a prévia e aplicar como RASCUNHO no formulário (nada é
 * criado no backend até o usuário concluir os passos). Erros são reportados
 * por campo; arquivos com segredo são rejeitados.
 */
export function DefinitionImportPanel({ onApply }: DefinitionImportPanelProps) {
  const { t } = useTranslation();
  const inputRef = useRef<HTMLInputElement>(null);
  const [issues, setIssues] = useState<ImportIssue[]>([]);
  const [preview, setPreview] = useState<DefinitionFormValues | null>(null);

  function downloadTemplate() {
    const blob = new Blob([definitionTemplateJson()], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'poseidon-agent-definition.json';
    anchor.click();
    URL.revokeObjectURL(url);
  }

  async function handleFile(file: File) {
    const raw = await file.text();
    const result = parseDefinitionImport(raw);
    setIssues(result.issues);
    setPreview(result.ok ? result.values : null);
  }

  return (
    <section
      aria-labelledby="definition-import-title"
      className="flex flex-col gap-3 rounded-lg border border-border bg-surface-elevated p-3"
    >
      <h3 id="definition-import-title" className="text-sm font-semibold">
        {t('orchestrator.definitions.import.title')}
      </h3>
      <p className="text-xs text-foreground-muted">
        {t('orchestrator.definitions.import.body')}
      </p>

      <div className="flex flex-wrap gap-2">
        <Button type="button" size="sm" variant="outline" onClick={downloadTemplate}>
          <Download aria-hidden="true" className="size-4" />
          {t('orchestrator.definitions.import.download')}
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={() => inputRef.current?.click()}>
          <Upload aria-hidden="true" className="size-4" />
          {t('orchestrator.definitions.import.upload')}
        </Button>
        <input
          ref={inputRef}
          type="file"
          accept="application/json,.json"
          className="sr-only"
          aria-label={t('orchestrator.definitions.import.upload')}
          onChange={(event) => {
            const file = event.target.files?.[0];
            if (file) void handleFile(file);
            // Permite reimportar o mesmo arquivo depois de corrigido.
            event.target.value = '';
          }}
        />
      </div>

      {issues.length > 0 ? (
        <div role="alert" className="flex flex-col gap-1 rounded-md border border-error p-2">
          <p className="text-xs font-medium text-error">
            {t('orchestrator.definitions.import.errors.title')}
          </p>
          <ul className="flex flex-col gap-0.5">
            {issues.map((issue, index) => (
              <li key={`${issue.field ?? 'root'}-${index}`} className="text-xs text-error">
                {issue.field ? <code className="font-mono">{issue.field}</code> : null}{' '}
                {t(issue.messageKey, { field: issue.detail ?? '' })}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {preview ? (
        <div className="flex flex-col gap-2 rounded-md border border-border p-2">
          <p className="text-xs font-medium">
            {t('orchestrator.definitions.import.previewTitle')}
          </p>
          <dl className="flex flex-col gap-0.5 text-xs">
            <div className="flex gap-2">
              <dt className="text-foreground-muted">
                {t('orchestrator.definitions.fields.name')}
              </dt>
              <dd>{preview.name || '—'}</dd>
            </div>
            <div className="flex gap-2">
              <dt className="text-foreground-muted">
                {t('orchestrator.definitions.fields.role')}
              </dt>
              <dd>{t(`orchestrator.definitions.role.${preview.role}`)}</dd>
            </div>
            <div className="flex gap-2">
              <dt className="text-foreground-muted">
                {t('orchestrator.definitions.fields.specialty')}
              </dt>
              <dd>{preview.specialty || '—'}</dd>
            </div>
          </dl>
          <Button
            type="button"
            size="sm"
            onClick={() => {
              onApply(preview);
              setPreview(null);
            }}
          >
            {t('orchestrator.definitions.import.apply')}
          </Button>
        </div>
      ) : null}
    </section>
  );
}
