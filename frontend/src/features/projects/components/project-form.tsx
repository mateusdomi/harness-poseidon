import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';

import {
  PRIORITIES,
  PROJECT_STATES,
  REPOSITORY_PROVIDERS,
  type Brand,
  type Organization,
  type Project,
} from '@/api';
import {
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Checkbox,
  Field,
  Input,
  Select,
  Textarea,
} from '@/design-system';
import { zodResolver } from '@/lib/form';
import { BrandFields } from '@/features/shared/components/brand-fields';
import { VersionedBadge } from '@/features/shared/components/versioned-badge';
import { useProfiles } from '@/features/shared/hooks/use-profiles';
import { TechnologiesInput } from '@/features/projects/components/technologies-input';
import {
  PROJECT_FORM_TAB_SCHEMAS,
  PROJECT_FORM_TABS,
  defaultProjectValues,
  projectFormSchema,
  projectToFormValues,
  type ProjectFormTab,
  type ProjectFormValues,
} from '@/features/projects/components/project-form-schema';

export type { ProjectFormTab, ProjectFormValues } from '@/features/projects/components/project-form-schema';

export interface ProjectFormProps {
  organizations: Organization[];
  initial?: Project;
  submitting: boolean;
  onSubmit: (values: ProjectFormValues) => void;
  onCancel: () => void;
}

/**
 * Criação/edição de projeto em ABAS, com validação zod por aba.
 * Ao salvar, valida todas as abas e foca a primeira com erro.
 * Campos versionados (repositório, tecnologias, marca) têm badge próprio;
 * a marca mostra herança da organização vs sobrescrita.
 */
export function ProjectForm({ organizations, initial, submitting, onSubmit, onCancel }: ProjectFormProps) {
  const { t } = useTranslation();
  const profilesQuery = useProfiles();
  const [activeTab, setActiveTab] = useState<ProjectFormTab>('identification');
  const [summaryError, setSummaryError] = useState(false);

  const {
    register,
    control,
    watch,
    getValues,
    setError,
    clearErrors,
    handleSubmit,
    formState: { errors },
  } = useForm<ProjectFormValues>({
    resolver: zodResolver(projectFormSchema),
    defaultValues: initial ? projectToFormValues(initial) : defaultProjectValues(organizations[0]?.id ?? ''),
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  const selectedOrganizationId = watch('organizationId');
  const selectedOrganization = organizations.find((org) => org.id === selectedOrganizationId);

  function validateTabs(values: ProjectFormValues): ProjectFormTab | null {
    for (const tab of PROJECT_FORM_TABS) {
      const result = PROJECT_FORM_TAB_SCHEMAS[tab].safeParse(values);
      if (!result.success) {
        for (const issue of result.error.issues) {
          const path = issue.path.join('.');
          setError(path as keyof ProjectFormValues, { type: issue.code, message: issue.message });
        }
        return tab;
      }
    }
    return null;
  }

  function handleValidSubmit(values: ProjectFormValues) {
    // A resolução do RHF já validou o schema completo; esta segunda passagem
    // garante o foco na primeira aba com erro em cenários de valores sujos.
    const failingTab = validateTabs(values);
    if (failingTab) {
      setActiveTab(failingTab);
      setSummaryError(true);
      return;
    }
    setSummaryError(false);
    onSubmit(values);
  }

  function handleInvalidSubmit() {
    const failingTab = validateTabs(getValues());
    setActiveTab(failingTab ?? 'identification');
    setSummaryError(true);
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>
          {initial ? t('projects.form.editTitle') : t('projects.form.createTitle')}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div role="tablist" aria-label={t('projects.form.tabsLabel')} className="mb-6 flex flex-wrap gap-1">
          {PROJECT_FORM_TABS.map((tab) => (
            <button
              key={tab}
              type="button"
              role="tab"
              id={`project-tab-${tab}`}
              aria-selected={activeTab === tab}
              aria-controls={`project-panel-${tab}`}
              onClick={() => setActiveTab(tab)}
              className={
                activeTab === tab
                  ? 'min-h-touch rounded-md border border-accent px-3 py-2 text-sm font-medium text-accent'
                  : 'min-h-touch rounded-md border border-border px-3 py-2 text-sm text-foreground-muted hover:text-foreground'
              }
            >
              {t(`projects.form.tabs.${tab}`)}
            </button>
          ))}
        </div>

        {summaryError ? (
          <p role="alert" className="mb-4 rounded-md border border-error bg-surface-elevated p-3 text-sm text-error">
            {t('projects.form.errorsSummary')}
          </p>
        ) : null}

        <form
          onSubmit={handleSubmit(handleValidSubmit, handleInvalidSubmit)}
          noValidate
          onChange={() => {
            if (summaryError) {
              setSummaryError(false);
              clearErrors();
            }
          }}
        >
          <div
            role="tabpanel"
            id="project-panel-identification"
            aria-labelledby="project-tab-identification"
            hidden={activeTab !== 'identification'}
            className="flex flex-col gap-4"
          >
            <Field
              htmlFor="project-organization"
              label={t('projects.form.identification.organization')}
              required
              requiredLabel={t('common.requiredMark')}
              error={errors.organizationId ? t(errors.organizationId.message!) : undefined}
            >
              <Select id="project-organization" {...register('organizationId')}>
                {organizations.map((org) => (
                  <option key={org.id} value={org.id}>
                    {org.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field
              htmlFor="project-name"
              label={t('projects.form.identification.name')}
              required
              requiredLabel={t('common.requiredMark')}
              error={errors.name ? t(errors.name.message!) : undefined}
            >
              <Input id="project-name" aria-invalid={Boolean(errors.name)} {...register('name')} />
            </Field>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field
                htmlFor="project-key"
                label={t('projects.form.identification.key')}
                required
                requiredLabel={t('common.requiredMark')}
                hint={t('projects.form.identification.keyHint')}
                error={errors.key ? t(errors.key.message!) : undefined}
              >
                <Input id="project-key" aria-invalid={Boolean(errors.key)} {...register('key')} />
              </Field>
              <Field htmlFor="project-criticality" label={t('projects.form.identification.criticality')}>
                <Select id="project-criticality" {...register('criticality')}>
                  {PRIORITIES.map((priority) => (
                    <option key={priority} value={priority}>
                      {t(`status.priority.${priority}`)}
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
            <Field
              htmlFor="project-description"
              label={t('projects.form.identification.description')}
              required
              requiredLabel={t('common.requiredMark')}
              error={errors.description ? t(errors.description.message!) : undefined}
            >
              <Textarea
                id="project-description"
                aria-invalid={Boolean(errors.description)}
                {...register('description')}
              />
            </Field>
            {initial ? (
              <Field htmlFor="project-state" label={t('projects.form.identification.state')}>
                <Select id="project-state" {...register('state')}>
                  {PROJECT_STATES.map((state) => (
                    <option key={state} value={state}>
                      {t(`status.projectState.${state}`)}
                    </option>
                  ))}
                </Select>
              </Field>
            ) : null}
          </div>

          <div
            role="tabpanel"
            id="project-panel-repository"
            aria-labelledby="project-tab-repository"
            hidden={activeTab !== 'repository'}
            className="flex flex-col gap-4"
          >
            <div className="flex items-center gap-2">
              <VersionedBadge />
            </div>
            <Field htmlFor="project-repo-provider" label={t('projects.form.repository.provider')}>
              <Select id="project-repo-provider" {...register('repositoryProvider')}>
                {REPOSITORY_PROVIDERS.map((provider) => (
                  <option key={provider} value={provider}>
                    {t(`status.repositoryProvider.${provider}`)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field
              htmlFor="project-repo-url"
              label={t('projects.form.repository.url')}
              hint={t('projects.form.repository.urlHint')}
              error={errors.repositoryUrl ? t(errors.repositoryUrl.message!) : undefined}
            >
              <Input
                id="project-repo-url"
                aria-invalid={Boolean(errors.repositoryUrl)}
                {...register('repositoryUrl')}
              />
            </Field>
            <Field
              htmlFor="project-repo-branch"
              label={t('projects.form.repository.branch')}
              required
              requiredLabel={t('common.requiredMark')}
              error={errors.defaultBranch ? t(errors.defaultBranch.message!) : undefined}
            >
              <Input
                id="project-repo-branch"
                aria-invalid={Boolean(errors.defaultBranch)}
                {...register('defaultBranch')}
              />
            </Field>
          </div>

          <div
            role="tabpanel"
            id="project-panel-technologies"
            aria-labelledby="project-tab-technologies"
            hidden={activeTab !== 'technologies'}
            className="flex flex-col gap-4"
          >
            <div className="flex items-center gap-2">
              <VersionedBadge />
            </div>
            <Controller
              control={control}
              name="technologies"
              render={({ field }) => (
                <TechnologiesInput
                  id="project-technologies"
                  value={field.value}
                  onChange={field.onChange}
                />
              )}
            />
          </div>

          <div
            role="tabpanel"
            id="project-panel-brand"
            aria-labelledby="project-tab-brand"
            hidden={activeTab !== 'brand'}
            className="flex flex-col gap-4"
          >
            <div className="flex items-center gap-2">
              <VersionedBadge />
            </div>
            <Controller
              control={control}
              name="brand"
              render={({ field }) => (
                <BrandFields
                  idPrefix="project-brand"
                  value={field.value as Brand}
                  onChange={field.onChange}
                  inheritedBrand={selectedOrganization?.brand}
                  source="organization"
                  errors={{
                    logoUrl: errors.brand?.logoUrl ? t(errors.brand.logoUrl.message!) : undefined,
                    primaryColor: errors.brand?.primaryColor
                      ? t(errors.brand.primaryColor.message!)
                      : undefined,
                    secondaryColor: errors.brand?.secondaryColor
                      ? t(errors.brand.secondaryColor.message!)
                      : undefined,
                  }}
                />
              )}
            />
          </div>

          <div
            role="tabpanel"
            id="project-panel-people"
            aria-labelledby="project-tab-people"
            hidden={activeTab !== 'people'}
            className="flex flex-col gap-4"
          >
            <fieldset className="flex flex-col gap-3">
              <legend className="text-sm font-medium">{t('projects.form.people.label')}</legend>
              {profilesQuery.isPending ? (
                <p className="text-sm text-foreground-muted">{t('common.states.loading')}</p>
              ) : (profilesQuery.data ?? []).length === 0 ? (
                <p className="text-sm text-foreground-muted">{t('projects.form.people.empty')}</p>
              ) : (
                <Controller
                  control={control}
                  name="memberProfileIds"
                  render={({ field }) => (
                    <ul className="flex flex-col gap-2">
                      {(profilesQuery.data ?? []).map((profile) => {
                        const checked = field.value.includes(profile.id);
                        return (
                          <li key={profile.id}>
                            <label
                              htmlFor={`project-member-${profile.id}`}
                              className="flex min-h-touch items-center gap-3 rounded-md border border-border bg-surface-elevated p-3"
                            >
                              <Checkbox
                                id={`project-member-${profile.id}`}
                                checked={checked}
                                onChange={(event) => {
                                  field.onChange(
                                    event.target.checked
                                      ? [...field.value, profile.id]
                                      : field.value.filter((id) => id !== profile.id),
                                  );
                                }}
                              />
                              <span className="flex flex-col">
                                <span className="text-sm font-medium">{profile.displayName}</span>
                                {profile.email ? (
                                  <span className="text-xs text-foreground-muted">{profile.email}</span>
                                ) : null}
                              </span>
                            </label>
                          </li>
                        );
                      })}
                    </ul>
                  )}
                />
              )}
              {errors.memberProfileIds ? (
                <p role="alert" className="text-xs text-error">
                  {t(errors.memberProfileIds.message!)}
                </p>
              ) : null}
            </fieldset>
          </div>

          <div className="mt-6 flex flex-wrap gap-3">
            <Button type="submit" disabled={submitting}>
              {submitting
                ? t('common.states.loading')
                : initial
                  ? t('common.actions.save')
                  : t('projects.form.create')}
            </Button>
            <Button type="button" variant="outline" onClick={onCancel}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
