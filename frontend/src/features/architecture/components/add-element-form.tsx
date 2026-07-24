import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, Field, Input, Textarea } from '@/design-system';
import type { CreateElementInput } from '../api/types';

export interface AddElementFormProps {
  projectId: string;
  onSubmit: (input: CreateElementInput) => void;
  pending: boolean;
}

/**
 * Cria um novo elemento no modelo (POST /architecture/elements). Nasce como
 * proposta e passa a existir no grafo — visível na view "Modelo completo".
 */
export function AddElementForm({ projectId, onSubmit, pending }: AddElementFormProps) {
  const { t } = useTranslation();
  const [name, setName] = useState('');
  const [kind, setKind] = useState('');
  const [description, setDescription] = useState('');

  const canSubmit = name.trim().length > 0 && kind.trim().length > 0 && !pending;

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (!canSubmit) return;
    onSubmit({
      projectId,
      name: name.trim(),
      kind: kind.trim(),
      description: description.trim(),
      properties: {},
    });
    setName('');
    setKind('');
    setDescription('');
  }

  return (
    <form className="flex flex-col gap-3" onSubmit={handleSubmit} aria-label={t('architecture.add.label')}>
      <Field htmlFor="arch-add-name" label={t('architecture.add.name')}>
        <Input
          id="arch-add-name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder={t('architecture.add.namePlaceholder')}
        />
      </Field>
      <Field htmlFor="arch-add-kind" label={t('architecture.add.kind')}>
        <Input
          id="arch-add-kind"
          value={kind}
          onChange={(event) => setKind(event.target.value)}
          placeholder={t('architecture.add.kindPlaceholder')}
        />
      </Field>
      <Field htmlFor="arch-add-description" label={t('architecture.add.description')}>
        <Textarea
          id="arch-add-description"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          rows={2}
        />
      </Field>
      <Button type="submit" size="sm" disabled={!canSubmit}>
        {t('architecture.add.submit')}
      </Button>
    </form>
  );
}
