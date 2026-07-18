import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FolderSearch } from 'lucide-react';

import type { Document } from '@/api';
import { Badge, Button, Field, Input, Select } from '@/design-system';
import { documentStateVariant } from '@/lib/status';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useClassifyDocument } from '@/features/documents/hooks/use-documents';

export interface OrphanDocumentsProps {
  /** Documentos sem vínculo de fase (`phaseName === null`). */
  orphans: Document[];
  /** Fases da versão ativa do workflow do projeto (sugestão de classificação). */
  phases: string[];
}

/**
 * Documentos órfãos (sem vínculo com fase do workflow): lista com ação
 * de classificar — escolhe a fase sugerida e ajusta rótulos, via comando
 * `classifyDocument` (metadados, não edição de conteúdo).
 */
export function OrphanDocuments({ orphans, phases }: OrphanDocumentsProps) {
  const { t } = useTranslation();
  const classify = useClassifyDocument();
  const [editing, setEditing] = useState<Document | null>(null);
  const [phaseName, setPhaseName] = useState('');
  const [classifications, setClassifications] = useState('');

  function openClassify(doc: Document) {
    setEditing(doc);
    setPhaseName(phases[0] ?? '');
    setClassifications(doc.classifications.join(', '));
  }

  function submit() {
    if (!editing || phaseName === '') return;
    classify.mutate(
      {
        documentId: editing.id,
        input: {
          phaseName,
          classifications: classifications
            .split(',')
            .map((entry) => entry.trim())
            .filter((entry) => entry.length > 0),
        },
      },
      { onSuccess: () => setEditing(null) },
    );
  }

  if (orphans.length === 0) return null;

  return (
    <section
      aria-labelledby="orphans-title"
      className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-4"
    >
      <div className="flex items-center gap-2">
        <FolderSearch aria-hidden="true" className="size-5 text-foreground-muted" />
        <h2 id="orphans-title" className="font-heading text-lg font-semibold">
          {t('documents.orphans.title')}
        </h2>
        <Badge variant="warning">{orphans.length}</Badge>
      </div>
      <p className="text-xs text-foreground-muted">{t('documents.orphans.body')}</p>

      <ul className="flex flex-col gap-2">
        {orphans.map((doc) => (
          <li
            key={doc.id}
            className="flex flex-wrap items-center gap-2 rounded-lg border border-border p-3"
          >
            <span className="text-sm font-medium">{doc.title}</span>
            <Badge variant={documentStateVariant(doc.state)}>
              {t(`status.documentState.${doc.state}`)}
            </Badge>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="ml-auto"
              onClick={() => openClassify(doc)}
            >
              {t('documents.orphans.classify')}
            </Button>
          </li>
        ))}
      </ul>

      {editing && (
        <ModalDialog
          label={t('documents.orphans.dialogTitle', { title: editing.title })}
          onClose={() => setEditing(null)}
        >
          <h3 className="font-heading text-lg font-semibold">
            {t('documents.orphans.dialogTitle', { title: editing.title })}
          </h3>

          <Field
            htmlFor="orphan-phase"
            label={t('documents.orphans.phaseLabel')}
            required
            requiredLabel={t('common.requiredMark')}
            hint={t('documents.orphans.phaseHint')}
          >
            <Select
              id="orphan-phase"
              value={phaseName}
              onChange={(event) => setPhaseName(event.target.value)}
            >
              {phases.map((phase) => (
                <option key={phase} value={phase}>
                  {phase}
                </option>
              ))}
            </Select>
          </Field>

          <Field
            htmlFor="orphan-classifications"
            label={t('documents.orphans.classificationsLabel')}
            hint={t('documents.orphans.classificationsHint')}
          >
            <Input
              id="orphan-classifications"
              value={classifications}
              onChange={(event) => setClassifications(event.target.value)}
            />
          </Field>

          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              disabled={classify.isPending || phaseName === ''}
              onClick={submit}
            >
              {t('common.actions.save')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setEditing(null)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}
    </section>
  );
}
