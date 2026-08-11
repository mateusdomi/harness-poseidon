import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { documentKindSchema, type DocumentKind, type Ulid } from '@/api';
import { Button, Field, Input, Select } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useUploadDocument } from '@/features/documents/hooks/use-documents';

export interface UploadDocumentDialogProps {
  projectId: Ulid;
  onClose: () => void;
}

/**
 * Upload textual de fonte do projeto: arquivo + nome + categoria. O
 * progresso é local e o conteúdo do arquivo vira a versão 1 do documento.
 */
export function UploadDocumentDialog({ projectId, onClose }: UploadDocumentDialogProps) {
  const { t } = useTranslation();
  const upload = useUploadDocument();
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState('');
  const [kind, setKind] = useState<DocumentKind>('spec');
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(
    () => () => {
      if (timerRef.current) clearInterval(timerRef.current);
    },
    [],
  );

  async function submit() {
    if (!file || title.trim().length === 0) {
      setError(t('documents.upload.errors.required'));
      return;
    }
    setError(null);
    setProgress(0);

    const body = await file.text();
    timerRef.current = setInterval(() => {
      setProgress((current) => {
        if (current === null) return 0;
        const next = Math.min(current + 20, 100);
        if (next >= 100 && timerRef.current) {
          clearInterval(timerRef.current);
          timerRef.current = null;
          upload.mutate(
            {
              projectId,
              title: title.trim(),
              kind,
              body,
              classifications: ['source', 'requirements_source', t('documents.upload.externalTag')],
            },
            { onSuccess: onClose },
          );
        }
        return next;
      });
    }, 150);
  }

  const busy = progress !== null;

  return (
    <ModalDialog label={t('documents.upload.title')} onClose={onClose}>
      <h3 className="font-heading text-lg font-semibold">{t('documents.upload.title')}</h3>

      <Field
        htmlFor="upload-file"
        label={t('documents.upload.fileLabel')}
        required
        requiredLabel={t('common.requiredMark')}
      >
        <Input
          id="upload-file"
          type="file"
          accept=".md,.markdown,.txt"
          disabled={busy}
          onChange={(event) => {
            const selected = event.target.files?.[0] ?? null;
            setFile(selected);
            if (selected && title.trim().length === 0) {
              setTitle(selected.name.replace(/\.(md|markdown|txt)$/i, ''));
            }
            if (error) setError(null);
          }}
        />
      </Field>

      <Field
        htmlFor="upload-title"
        label={t('documents.upload.nameLabel')}
        required
        requiredLabel={t('common.requiredMark')}
      >
        <Input
          id="upload-title"
          value={title}
          disabled={busy}
          onChange={(event) => {
            setTitle(event.target.value);
            if (error) setError(null);
          }}
        />
      </Field>

      <Field htmlFor="upload-kind" label={t('documents.upload.kindLabel')}>
        <Select
          id="upload-kind"
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

      {busy && (
        <div
          role="progressbar"
          aria-valuenow={progress}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-label={t('documents.upload.progressLabel')}
          className="h-2 overflow-hidden rounded-full bg-surface-elevated"
        >
          <div
            className="h-full rounded-full bg-accent transition-[width]"
            style={{ width: `${progress}%` }}
          />
        </div>
      )}

      {error && (
        <p role="alert" className="text-xs text-error">
          {error}
        </p>
      )}

      <div className="flex flex-wrap gap-2">
        <Button type="button" disabled={busy} onClick={() => void submit()}>
          {t('documents.upload.submit')}
        </Button>
        <Button type="button" variant="outline" disabled={busy} onClick={onClose}>
          {t('common.actions.cancel')}
        </Button>
      </div>
    </ModalDialog>
  );
}
