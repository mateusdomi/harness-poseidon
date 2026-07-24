import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { documentKindSchema, type DocumentKind, type Ulid } from '@/api';
import { Button, Field, Input, Select, Textarea } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useCreateDocument } from '@/features/documents/hooks/use-documents';

export interface CreateDocumentDialogProps {
  projectId: Ulid;
  onClose: () => void;
  /** Abre o detalhe do documento recém-criado. */
  onCreated: (documentId: Ulid) => void;
}

/**
 * Criação de documento do zero: título + categoria + conteúdo markdown. O
 * conteúdo vira a versão 1 (estado inicial definido pelo backend). Ao criar,
 * abre o detalhe do novo documento para leitura/edição/aprovação.
 */
export function CreateDocumentDialog({ projectId, onClose, onCreated }: CreateDocumentDialogProps) {
  const { t } = useTranslation();
  const create = useCreateDocument();
  const [title, setTitle] = useState('');
  const [kind, setKind] = useState<DocumentKind>('note');
  const [body, setBody] = useState('');
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    if (title.trim().length === 0 || body.trim().length === 0) {
      setError(t('documents.create.errors.required'));
      return;
    }
    setError(null);
    const created = await create.mutateAsync({
      projectId,
      title: title.trim(),
      kind,
      body,
    });
    onCreated(created.id);
  }

  const busy = create.isPending;

  return (
    <ModalDialog label={t('documents.create.title')} onClose={onClose}>
      <h3 className="font-heading text-lg font-semibold">{t('documents.create.title')}</h3>

      <Field
        htmlFor="create-title"
        label={t('documents.create.titleLabel')}
        required
        requiredLabel={t('common.requiredMark')}
      >
        <Input
          id="create-title"
          value={title}
          disabled={busy}
          onChange={(event) => {
            setTitle(event.target.value);
            if (error) setError(null);
          }}
        />
      </Field>

      <Field htmlFor="create-kind" label={t('documents.create.kindLabel')}>
        <Select
          id="create-kind"
          value={kind}
          disabled={busy}
          onChange={(event) => setKind(event.target.value as DocumentKind)}
        >
          {documentKindSchema.options.map((option) => (
            <option key={option} value={option}>
              {t(`status.documentKind.${option}`)}
            </option>
          ))}
        </Select>
      </Field>

      <Field
        htmlFor="create-body"
        label={t('documents.create.bodyLabel')}
        required
        requiredLabel={t('common.requiredMark')}
      >
        <Textarea
          id="create-body"
          className="min-h-48 font-mono text-sm"
          value={body}
          disabled={busy}
          onChange={(event) => {
            setBody(event.target.value);
            if (error) setError(null);
          }}
        />
      </Field>
      <p className="text-xs text-foreground-muted">{t('documents.create.bodyHint')}</p>

      {error && (
        <p role="alert" className="text-xs text-error">
          {error}
        </p>
      )}

      <div className="flex flex-wrap gap-2">
        <Button type="button" disabled={busy} onClick={() => void submit()}>
          {t('documents.create.submit')}
        </Button>
        <Button type="button" variant="outline" disabled={busy} onClick={onClose}>
          {t('common.actions.cancel')}
        </Button>
      </div>
    </ModalDialog>
  );
}
