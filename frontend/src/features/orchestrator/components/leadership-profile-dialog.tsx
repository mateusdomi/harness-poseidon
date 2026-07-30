import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Camera } from 'lucide-react';

import type { Account, AgentDefinition, Model } from '@/api';
import { Button, Field, Input, Select, Textarea } from '@/design-system';
import { useUpdateDefinition } from '@/features/orchestrator/hooks/use-definitions';
import {
  definitionToFormValues,
  valuesToInput,
} from '@/features/orchestrator/lib/definitions-form';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { ImageCropEditor } from '@/features/shared/components/image-crop-editor';
import { ManagedAgentAvatar } from '@/features/shared/components/managed-agent-avatar';
import {
  useLeadershipProfile,
  useUpdateLeadershipProfile,
  useUploadLeadershipPhoto,
} from '@/features/shared/hooks/use-leadership-profile';

function list(value: string): string[] {
  return value
    .split(/\r?\n|,/)
    .map((item) => item.trim())
    .filter(Boolean);
}

export function LeadershipProfileDialog({
  definition,
  models,
  accounts,
  onClose,
}: {
  definition: AgentDefinition;
  models: Model[];
  accounts: Account[];
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const profileQuery = useLeadershipProfile();
  const updateProfile = useUpdateLeadershipProfile();
  const uploadPhoto = useUploadLeadershipPhoto();
  const updateDefinition = useUpdateDefinition();
  const profile = profileQuery.data;
  const initialized = useRef(false);
  const [selectedPhoto, setSelectedPhoto] = useState<File | null>(null);
  const [photoSelectionError, setPhotoSelectionError] = useState<string | null>(null);
  const [photoSaved, setPhotoSaved] = useState(false);
  const [values, setValues] = useState({
    displayName: '',
    title: '',
    summary: '',
    specialties: '',
    careerSummary: '',
    languages: '',
    personality: '',
    hobbies: '',
    age: '27',
    communicationInstructions: '',
    preferredModelId: '',
    preferredAccountId: '',
  });

  useEffect(() => {
    if (!profile || initialized.current) return;
    initialized.current = true;
    setValues({
      displayName: profile.displayName,
      title: profile.title,
      summary: profile.summary,
      specialties: profile.specialties.join('\n'),
      careerSummary: profile.careerSummary,
      languages: profile.languages.join('\n'),
      personality: profile.personality,
      hobbies: profile.hobbies.join('\n'),
      age: String(profile.age),
      communicationInstructions: profile.communicationInstructions,
      preferredModelId: profile.preferredModelId ?? definition.defaultModelId ?? '',
      preferredAccountId: profile.preferredAccountId ?? definition.preferredAccountId ?? '',
    });
  }, [profile, definition]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!profile) return;
    const preferredModelId = values.preferredModelId || null;
    const preferredAccountId = values.preferredAccountId || null;
    // A rota técnica é persistida primeiro na definição versionada, que é a
    // fonte efetivamente consumida pelas novas execuções. O perfil público só
    // passa a anunciá-la depois que essa escrita auditável termina.
    if (
      preferredModelId !== (definition.defaultModelId ?? null) ||
      preferredAccountId !== (definition.preferredAccountId ?? null)
    ) {
      const current = valuesToInput(definitionToFormValues(definition));
      await updateDefinition.mutateAsync({
        id: definition.id,
        input: {
          ...current,
          defaultModelId: preferredModelId,
          preferredAccountId,
          expectedVersion: definition.version ?? 0,
        },
      });
    }
    await updateProfile.mutateAsync({
      displayName: values.displayName,
      title: values.title,
      summary: values.summary,
      specialties: list(values.specialties),
      careerSummary: values.careerSummary,
      languages: list(values.languages),
      personality: values.personality,
      hobbies: list(values.hobbies),
      age: Number(values.age),
      communicationInstructions: values.communicationInstructions,
      preferredModelId,
      preferredAccountId,
      expectedVersion: profile.version,
    });
    onClose();
  }

  const pending = updateProfile.isPending || uploadPhoto.isPending || updateDefinition.isPending;
  const error = updateProfile.error ?? uploadPhoto.error ?? updateDefinition.error;
  const photoError = uploadPhoto.error?.message ?? photoSelectionError;

  return (
    <ModalDialog label={t('orchestrator.profile.title')} onClose={onClose} className="max-w-3xl">
      {selectedPhoto && profile ? (
        <ImageCropEditor
          file={selectedPhoto}
          subjectName={profile.displayName}
          pending={uploadPhoto.isPending}
          error={photoError}
          onCancel={() => {
            uploadPhoto.reset();
            setSelectedPhoto(null);
          }}
          onConfirm={async (photo) => {
            await uploadPhoto.mutateAsync(photo);
            setSelectedPhoto(null);
            setPhotoSaved(true);
          }}
        />
      ) : (
        <form className="flex flex-col gap-5" onSubmit={(event) => void submit(event)}>
          <div>
            <h2 className="font-heading text-xl font-semibold">
              {t('orchestrator.profile.title')}
            </h2>
            <p className="mt-1 text-sm text-foreground-muted">
              {t('orchestrator.profile.boundary')}
            </p>
          </div>

          {profileQuery.isLoading ? (
            <p className="text-sm text-foreground-muted">{t('common.states.loading')}</p>
          ) : profile ? (
            <>
              <section className="flex flex-col gap-4 rounded-lg border border-border bg-surface-elevated p-4 md:flex-row md:items-center">
                <ManagedAgentAvatar
                  alias="chief-orchestrator"
                  fallbackName={profile.displayName}
                  roleLabel={profile.title}
                  size={96}
                  className="ring-2 ring-brand/40 ring-offset-2 ring-offset-surface-elevated"
                />
                <div className="flex min-w-0 flex-1 flex-col gap-2">
                  <div>
                    <p className="font-semibold">{t('orchestrator.profile.photo')}</p>
                    <p className="text-xs text-foreground-muted">{t('photoEditor.cropHelp')}</p>
                  </div>
                  <Button asChild variant="outline" className="w-fit">
                    <label htmlFor="leadership-photo" className="cursor-pointer">
                      <Camera aria-hidden="true" className="size-4" />
                      {t('photoEditor.change')}
                    </label>
                  </Button>
                  <input
                    id="leadership-photo"
                    className="sr-only"
                    type="file"
                    accept="image/jpeg,image/png,image/webp"
                    onChange={(event) => {
                      const photo = event.currentTarget.files?.[0];
                      event.currentTarget.value = '';
                      if (!photo) return;
                      if (
                        !['image/jpeg', 'image/png', 'image/webp'].includes(photo.type) ||
                        photo.size > 5 * 1024 * 1024
                      ) {
                        setPhotoSelectionError(t('photoEditor.invalid'));
                        return;
                      }
                      uploadPhoto.reset();
                      setPhotoSelectionError(null);
                      setPhotoSaved(false);
                      setSelectedPhoto(photo);
                    }}
                  />
                  {photoSelectionError ? (
                    <p role="alert" className="text-xs text-error">
                      {photoSelectionError}
                    </p>
                  ) : null}
                  {photoSaved ? (
                    <p role="status" className="text-xs font-medium text-success">
                      {t('photoEditor.saved')}
                    </p>
                  ) : null}
                </div>
              </section>
              <div className="grid gap-4 md:grid-cols-2">
                <Field htmlFor="leadership-name" label={t('orchestrator.profile.displayName')}>
                  <Input
                    id="leadership-name"
                    required
                    value={values.displayName}
                    onChange={(event) => setValues({ ...values, displayName: event.target.value })}
                  />
                </Field>
                <Field htmlFor="leadership-title" label={t('orchestrator.profile.jobTitle')}>
                  <Input
                    id="leadership-title"
                    required
                    value={values.title}
                    onChange={(event) => setValues({ ...values, title: event.target.value })}
                  />
                </Field>
                <Field htmlFor="leadership-age" label={t('orchestrator.profile.age')}>
                  <Input
                    id="leadership-age"
                    type="number"
                    min={18}
                    max={120}
                    value={values.age}
                    onChange={(event) => setValues({ ...values, age: event.target.value })}
                  />
                </Field>
              </div>
              <Field htmlFor="leadership-summary" label={t('orchestrator.profile.summary')}>
                <Textarea
                  id="leadership-summary"
                  rows={3}
                  value={values.summary}
                  onChange={(event) => setValues({ ...values, summary: event.target.value })}
                />
              </Field>
              <Field htmlFor="leadership-career" label={t('orchestrator.profile.career')}>
                <Textarea
                  id="leadership-career"
                  rows={3}
                  value={values.careerSummary}
                  onChange={(event) => setValues({ ...values, careerSummary: event.target.value })}
                />
              </Field>
              <div className="grid gap-4 md:grid-cols-2">
                {(
                  [
                    ['specialties', 'specialties'],
                    ['languages', 'languages'],
                    ['hobbies', 'hobbies'],
                  ] as const
                ).map(([field, label]) => (
                  <Field
                    key={field}
                    htmlFor={`leadership-${field}`}
                    label={t(`orchestrator.profile.${label}`)}
                    hint={t('orchestrator.profile.listHint')}
                  >
                    <Textarea
                      id={`leadership-${field}`}
                      rows={4}
                      value={values[field]}
                      onChange={(event) => setValues({ ...values, [field]: event.target.value })}
                    />
                  </Field>
                ))}
                <Field
                  htmlFor="leadership-personality"
                  label={t('orchestrator.profile.personality')}
                >
                  <Textarea
                    id="leadership-personality"
                    rows={4}
                    value={values.personality}
                    onChange={(event) => setValues({ ...values, personality: event.target.value })}
                  />
                </Field>
              </div>

              <section className="flex flex-col gap-4 rounded-lg border border-border p-4">
                <div>
                  <h3 className="font-semibold">{t('orchestrator.profile.communicationTitle')}</h3>
                  <p className="text-xs text-foreground-muted">
                    {t('orchestrator.profile.communicationHelp')}
                  </p>
                </div>
                <Field
                  htmlFor="leadership-communication"
                  label={t('orchestrator.profile.communicationInstructions')}
                >
                  <Textarea
                    id="leadership-communication"
                    rows={4}
                    value={values.communicationInstructions}
                    onChange={(event) =>
                      setValues({ ...values, communicationInstructions: event.target.value })
                    }
                  />
                </Field>
              </section>

              <section className="grid gap-4 rounded-lg border border-border p-4 md:grid-cols-2">
                <div className="md:col-span-2">
                  <h3 className="font-semibold">{t('orchestrator.profile.routingTitle')}</h3>
                  <p className="text-xs text-foreground-muted">
                    {t('orchestrator.profile.routingHelp')}
                  </p>
                </div>
                <Field htmlFor="leadership-model" label={t('orchestrator.profile.model')}>
                  <Select
                    id="leadership-model"
                    value={values.preferredModelId}
                    onChange={(event) =>
                      setValues({ ...values, preferredModelId: event.target.value })
                    }
                  >
                    <option value="">{t('orchestrator.profile.notConfigured')}</option>
                    {models
                      .filter((model) => model.enabled)
                      .map((model) => (
                        <option key={model.id} value={model.id}>
                          {model.displayName}
                        </option>
                      ))}
                  </Select>
                </Field>
                <Field htmlFor="leadership-account" label={t('orchestrator.profile.account')}>
                  <Select
                    id="leadership-account"
                    value={values.preferredAccountId}
                    onChange={(event) =>
                      setValues({ ...values, preferredAccountId: event.target.value })
                    }
                  >
                    <option value="">{t('orchestrator.profile.notConfigured')}</option>
                    {accounts.map((account) => (
                      <option key={account.id} value={account.id}>
                        {account.label}
                      </option>
                    ))}
                  </Select>
                </Field>
              </section>

              <details className="rounded-lg border border-border p-4">
                <summary className="cursor-pointer font-medium">
                  {t('orchestrator.profile.history', { count: profile.history.length })}
                </summary>
                {profile.history.length === 0 ? (
                  <p className="mt-2 text-sm text-foreground-muted">
                    {t('orchestrator.profile.historyEmpty')}
                  </p>
                ) : (
                  <ol className="mt-3 flex flex-col gap-2 text-sm">
                    {[...profile.history].reverse().map((revision) => (
                      <li key={revision.version}>
                        <span className="font-medium">
                          {t('orchestrator.profile.revision', { version: revision.version })}
                        </span>
                        {' · '}
                        {new Date(revision.changedAt).toLocaleString('pt-BR')}
                        <ul className="ml-5 list-disc text-foreground-muted">
                          {revision.changes.map((change) => (
                            <li key={change}>{change}</li>
                          ))}
                        </ul>
                      </li>
                    ))}
                  </ol>
                )}
              </details>
            </>
          ) : null}

          {error ? (
            <p role="alert" className="text-sm text-error">
              {error.message}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>
              {t('common.actions.cancel')}
            </Button>
            <Button type="submit" disabled={!profile || pending}>
              {pending ? t('common.states.loading') : t('common.actions.save')}
            </Button>
          </div>
        </form>
      )}
    </ModalDialog>
  );
}
