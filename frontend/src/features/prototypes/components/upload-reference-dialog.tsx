import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Ulid } from '@/api';
import { Button, Input, Textarea } from '@/design-system';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useCreateVisualReference } from '@/features/prototypes/hooks/use-prototypes';
import { buildReferenceTags } from '@/features/prototypes/lib/prototypes-derive';

export interface UploadReferenceDialogProps {
  projectId: Ulid;
  onClose: () => void;
}

/**
 * Upload de referência visual (imagem ou ZIP). ZIP é SOMENTE referência:
 * o aviso explícito fica visível no dialog e o arquivo nunca é executado.
 */
export function UploadReferenceDialog({ projectId, onClose }: UploadReferenceDialogProps) {
  const { t } = useTranslation();
  const createReference = useCreateVisualReference();
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState('');
  const [tags, setTags] = useState('');
  const [briefing, setBriefing] = useState('');
  const [flowMoment, setFlowMoment] = useState('');
  const [error, setError] = useState<string | null>(null);

  const isZip = file?.name.toLowerCase().endsWith('.zip') ?? false;

  async function save() {
    if (!file) {
      setError(t('prototypes.upload.fileRequired'));
      return;
    }
    if (title.trim() === '') {
      setError(t('prototypes.upload.titleRequired'));
      return;
    }
    try {
      await createReference.mutateAsync({
        projectId,
        title: title.trim(),
        // Mock: a imagem usa object URL; ZIP guarda só o nome (nunca executado).
        imageUrl: isZip ? `zip:${file.name}` : URL.createObjectURL(file),
        source: 'upload',
        tags: buildReferenceTags({ tags, briefing, flowMoment }),
      });
      onClose();
    } catch {
      setError(t('prototypes.upload.error'));
    }
  }

  return (
    <ModalDialog label={t('prototypes.upload.title')} onClose={onClose}>
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <div className="flex flex-col gap-1">
          <label htmlFor="reference-file" className="text-sm font-medium">
            {t('prototypes.upload.file')}
          </label>
          <Input
            id="reference-file"
            type="file"
            accept="image/*,.zip"
            onChange={(event) => {
              setFile(event.target.files?.[0] ?? null);
              setError(null);
            }}
          />
          <p className="text-xs text-foreground-muted">{t('prototypes.upload.zipNotice')}</p>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="reference-title" className="text-sm font-medium">
            {t('prototypes.upload.name')}
          </label>
          <Input
            id="reference-title"
            value={title}
            onChange={(event) => {
              setTitle(event.target.value);
              setError(null);
            }}
          />
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="reference-tags" className="text-sm font-medium">
            {t('prototypes.upload.tags')}
          </label>
          <Input
            id="reference-tags"
            value={tags}
            placeholder={t('prototypes.upload.tagsPlaceholder')}
            onChange={(event) => setTags(event.target.value)}
          />
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="reference-briefing" className="text-sm font-medium">
            {t('prototypes.upload.briefing')}
          </label>
          <Textarea
            id="reference-briefing"
            value={briefing}
            onChange={(event) => setBriefing(event.target.value)}
          />
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="reference-flow" className="text-sm font-medium">
            {t('prototypes.upload.flowMoment')}
          </label>
          <Input
            id="reference-flow"
            value={flowMoment}
            placeholder={t('prototypes.upload.flowMomentPlaceholder')}
            onChange={(event) => setFlowMoment(event.target.value)}
          />
        </div>
        {error && (
          <p role="alert" className="text-sm text-error">
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button type="submit" disabled={createReference.isPending}>
            {t('prototypes.upload.submit')}
          </Button>
        </div>
      </form>
    </ModalDialog>
  );
}
