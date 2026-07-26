import { useId, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';

import { ApiError, type ChannelKind, type Project } from '@/api';
import { Button, Field, Input, Select } from '@/design-system';
import { useCreateChannelLink } from '@/features/channels/hooks/use-channels';

/** Tipos de canal externo oferecidos na UI (o `terminal` é interno). */
const EXTERNAL_KINDS: ChannelKind[] = ['telegram', 'teams', 'whatsapp', 'email'];

/** Dica de identidade externa por tipo de canal (fallback: Telegram). */
const IDENTITY_HINT_KEY: Partial<Record<ChannelKind, string>> = {
  teams: 'channels.link.fields.identityHintTeams',
  whatsapp: 'channels.link.fields.identityHintWhatsApp',
  email: 'channels.link.fields.identityHintEmail',
};

/** Mapeia o `title` do problem+json do backend para a chave i18n do erro. */
const KNOWN_ERROR_TITLES = new Set([
  'invalid_channel_kind',
  'invalid_external_identity',
  'invalid_project_id',
  'project_not_found',
]);

export interface LinkChannelFormProps {
  projects: Project[];
  /** Chamado após vincular com sucesso (ex.: fechar o formulário). */
  onLinked?: () => void;
  onCancel?: () => void;
}

/**
 * Formulário de vínculo de canal externo a um projeto. Usa o endpoint real
 * `POST /api/v1/channels/links`; o token do bot NUNCA passa por aqui — só a
 * identidade externa (ex.: chat id do Telegram).
 */
export function LinkChannelForm({ projects, onLinked, onCancel }: LinkChannelFormProps) {
  const { t } = useTranslation();
  const fieldId = useId();
  const kindId = `${fieldId}-kind`;
  const identityId = `${fieldId}-identity`;
  const projectId = `${fieldId}-project`;

  const mutation = useCreateChannelLink();
  const [kind, setKind] = useState<ChannelKind>('telegram');
  const [identity, setIdentity] = useState('');
  // `null` = ainda não escolhido; adota o primeiro projeto quando a lista chega.
  const [project, setProject] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);

  if (projects.length === 0) {
    return <p className="text-sm text-foreground-muted">{t('channels.link.noProjects')}</p>;
  }

  const selectedProject = project ?? projects[0]?.id ?? '';

  const errorKey = (() => {
    if (!mutation.isError) return null;
    const error = mutation.error;
    if (error instanceof ApiError && KNOWN_ERROR_TITLES.has(error.problem.title)) {
      return `channels.link.errors.${error.problem.title}`;
    }
    return 'channels.link.errors.generic';
  })();

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    setSuccess(false);
    mutation.mutate(
      { kind, externalIdentity: identity.trim(), projectId: selectedProject },
      {
        onSuccess: () => {
          setSuccess(true);
          setIdentity('');
          onLinked?.();
        },
      },
    );
  };

  const identityHint = t(IDENTITY_HINT_KEY[kind] ?? 'channels.link.fields.identityHintTelegram');

  return (
    <form
      className="flex min-w-0 flex-col gap-4"
      onSubmit={handleSubmit}
      aria-label={t('channels.link.title')}
    >
      <Field htmlFor={kindId} label={t('channels.link.fields.kind')} required requiredLabel={t('channels.link.required')}>
        <Select
          id={kindId}
          value={kind}
          onChange={(event) => setKind(event.target.value as ChannelKind)}
        >
          {EXTERNAL_KINDS.map((value) => (
            <option key={value} value={value}>
              {t(`channels.kind.${value}`)}
            </option>
          ))}
        </Select>
      </Field>

      <Field
        htmlFor={identityId}
        label={t('channels.link.fields.identity')}
        hint={identityHint}
        required
        requiredLabel={t('channels.link.required')}
      >
        <Input
          id={identityId}
          value={identity}
          onChange={(event) => setIdentity(event.target.value)}
          placeholder={t('channels.link.fields.identityPlaceholder')}
          autoComplete="off"
          required
        />
      </Field>

      <Field htmlFor={projectId} label={t('channels.link.fields.project')} required requiredLabel={t('channels.link.required')}>
        <Select id={projectId} value={selectedProject} onChange={(event) => setProject(event.target.value)}>
          <option value="" disabled>
            {t('channels.link.fields.projectPlaceholder')}
          </option>
          {projects.map((entry) => (
            <option key={entry.id} value={entry.id}>
              {entry.name}
            </option>
          ))}
        </Select>
      </Field>

      {errorKey ? (
        <p role="alert" className="text-sm text-error">
          {t(errorKey)}
        </p>
      ) : null}
      {success ? (
        <p role="status" className="text-sm text-success">
          {t('channels.link.success')}
        </p>
      ) : null}

      <div className="flex items-center gap-2">
        <Button
          type="submit"
          disabled={mutation.isPending || identity.trim().length === 0 || selectedProject === ''}
        >
          {mutation.isPending ? t('channels.link.submitting') : t('channels.link.submit')}
        </Button>
        {onCancel ? (
          <Button type="button" variant="ghost" onClick={onCancel}>
            {t('channels.link.cancel')}
          </Button>
        ) : null}
      </div>
    </form>
  );
}
