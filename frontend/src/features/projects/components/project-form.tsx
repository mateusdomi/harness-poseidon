import { useEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';

import {
  PRIORITIES,
  PROJECT_STATES,
  REPOSITORY_PROVIDERS,
  type Brand,
  type Organization,
  type Project,
  type WorkflowTemplate,
  type WorkflowVersion,
} from '@/api';
import {
  Badge,
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
import { formatDateTime } from '@/lib/format';
import { keyify } from '@/lib/utils';
import { BrandFields } from '@/features/shared/components/brand-fields';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useProfiles } from '@/features/shared/hooks/use-profiles';
import { TechnologiesInput } from '@/features/projects/components/technologies-input';
import { recommendWorkflowTemplate } from '@/features/workflows/lib/recommend-template';
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

/** Campos operacionais/versionados — mudança em projeto INICIADO exige painel de impacto. */
const OPERATIONAL_FIELDS = [
  'repositoryProvider',
  'repositoryUrl',
  'defaultBranch',
  'technologies',
  'brand',
] as const;
type OperationalField = (typeof OPERATIONAL_FIELDS)[number];

/** Campos operacionais alterados em relação ao projeto salvo. */
function changedOperationalFields(
  project: Project,
  values: ProjectFormValues,
): OperationalField[] {
  const saved = projectToFormValues(project);
  return OPERATIONAL_FIELDS.filter((field) => {
    const before = saved[field];
    const after = values[field];
    if (typeof before === 'string' || typeof after === 'string') return before !== after;
    return JSON.stringify(before) !== JSON.stringify(after);
  });
}

export interface ProjectFormProps {
  organizations: Organization[];
  initial?: Project;
  /** Organização pré-selecionada na criação (deep link do golden path). */
  defaultOrganizationId?: string;
  /** Projeto iniciado (workflow com execução) — ativa o painel de impacto. */
  started?: boolean;
  workflowTemplates?: WorkflowTemplate[];
  workflowVersions?: WorkflowVersion[];
  currentWorkflowTemplateId?: string;
  submitting: boolean;
  onSubmit: (values: ProjectFormValues, logoFile?: File | null) => void;
  onCancel: () => void;
}

/**
 * Criação/edição de projeto em ABAS, com validação zod por aba.
 * Ao salvar, valida todas as abas e foca a primeira com erro.
 * Campos versionados (repositório, tecnologias, marca) têm badge próprio;
 * a marca mostra herança da organização vs sobrescrita.
 * FR-4: edição exibe a versão de config atual, alterações pendentes e o
 * histórico de versões; em projeto INICIADO, mudança em campo operacional
 * abre o painel de impacto com confirmação reforçada (checkbox).
 */
export function ProjectForm({
  organizations,
  initial,
  defaultOrganizationId,
  started = false,
  workflowTemplates = [],
  workflowVersions = [],
  currentWorkflowTemplateId,
  submitting,
  onSubmit,
  onCancel,
}: ProjectFormProps) {
  const { t, i18n } = useTranslation();
  const profilesQuery = useProfiles();
  const [activeTab, setActiveTab] = useState<ProjectFormTab>('organization');
  const [summaryError, setSummaryError] = useState(false);
  const [pendingLogoFile, setPendingLogoFile] = useState<File | null>(null);
  const [impact, setImpact] = useState<{
    values: ProjectFormValues;
    fields: OperationalField[];
    logoFile: File | null;
  } | null>(null);
  const [impactAccepted, setImpactAccepted] = useState(false);
  // Em edição a sigla já existe e é do usuário; em criação, geramos do nome
  // até que ele a edite manualmente (§7 — sem exigir decisão manual).
  const keyEditedRef = useRef(Boolean(initial));

  const {
    register,
    control,
    watch,
    setValue,
    trigger,
    getValues,
    setError,
    clearErrors,
    handleSubmit,
    formState: { errors, dirtyFields, isDirty },
  } = useForm<ProjectFormValues>({
    resolver: zodResolver(projectFormSchema),
    defaultValues: (() => {
      const values = initial
        ? projectToFormValues(initial)
        : defaultProjectValues(
          (defaultOrganizationId && organizations.some((o) => o.id === defaultOrganizationId)
            ? defaultOrganizationId
            : organizations[0]?.id) ?? '',
        );
      values.workflowTemplateId =
        currentWorkflowTemplateId ??
        recommendWorkflowTemplate(workflowTemplates, workflowVersions)?.template.id ??
        '';
      return values;
    })(),
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  const nameValue = watch('name');
  // Deriva a sigla do nome enquanto o usuário não a editou (só na criação).
  useEffect(() => {
    if (!keyEditedRef.current) {
      setValue('key', keyify(nameValue ?? ''), { shouldValidate: false });
    }
  }, [nameValue, setValue]);

  const keyReg = register('key');
  const selectedOrganizationId = watch('organizationId');
  const selectedOrganization = organizations.find((org) => org.id === selectedOrganizationId);
  const selectedWorkflowTemplateId = watch('workflowTemplateId');
  const workflowRecommendation = recommendWorkflowTemplate(workflowTemplates, workflowVersions);
  const pendingCount = Object.keys(dirtyFields).length;

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
    // Projeto iniciado + campo operacional alterado → painel de impacto.
    // Metadados seguros (título, descrição) seguem o fluxo normal.
    if (initial && started) {
      const fields = changedOperationalFields(initial, values);
      if (pendingLogoFile && !fields.includes('brand')) fields.push('brand');
      if (fields.length > 0) {
        setImpact({ values, fields, logoFile: pendingLogoFile });
        setImpactAccepted(false);
        return;
      }
    }
    onSubmit(values, pendingLogoFile);
  }

  function handleInvalidSubmit() {
    const failingTab = validateTabs(getValues());
    setActiveTab(failingTab ?? 'organization');
    setSummaryError(true);
  }

  function confirmImpact() {
    if (!impact) return;
    const { values, logoFile } = impact;
    setImpact(null);
    onSubmit(values, logoFile);
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>
          {initial ? t('projects.form.editTitle') : t('projects.form.createTitle')}
        </CardTitle>
        <p className="text-sm text-foreground-muted">
          {t('projects.config.versionedHint')}
        </p>
      </CardHeader>
      <CardContent>
        {initial && (
          <div className="mb-4 flex flex-wrap items-center gap-2">
            <Badge variant="outline">
              {t('projects.config.current', { version: initial.configVersion })}
            </Badge>
            {isDirty && (
              <Badge variant="warning" role="status">
                {t('projects.config.pending', { count: pendingCount })}
              </Badge>
            )}
          </div>
        )}
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
                  ? 'min-h-touch rounded-md border border-brand px-3 py-2 text-sm font-medium text-brand-strong'
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
            id="project-panel-organization"
            aria-labelledby="project-tab-organization"
            hidden={activeTab !== 'organization'}
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
            <p className="text-sm text-foreground-muted">
              {t('projects.form.organization.inheritanceHint')}
            </p>
          </div>

          <div
            role="tabpanel"
            id="project-panel-identity"
            aria-labelledby="project-tab-identity"
            hidden={activeTab !== 'identity'}
            className="flex flex-col gap-4"
          >
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
                <Input
                  id="project-key"
                  aria-invalid={Boolean(errors.key)}
                  {...keyReg}
                  onChange={(event) => {
                    keyEditedRef.current = true;
                    void keyReg.onChange(event);
                    void trigger('key');
                  }}
                />
              </Field>
            </div>
          </div>

          <div
            role="tabpanel"
            id="project-panel-objective"
            aria-labelledby="project-tab-objective"
            hidden={activeTab !== 'objective'}
            className="flex flex-col gap-4"
          >
            <Field
              htmlFor="project-description"
              label={t('projects.form.objective.description')}
              hint={t('projects.form.objective.descriptionHint')}
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
          </div>

          <div
            role="tabpanel"
            id="project-panel-criticality"
            aria-labelledby="project-tab-criticality"
            hidden={activeTab !== 'criticality'}
            className="flex flex-col gap-4"
          >
            <Field
              htmlFor="project-criticality"
              label={t('projects.form.identification.criticality')}
              hint={t('projects.form.identification.criticalityHint')}
            >
              <Select id="project-criticality" {...register('criticality')}>
                {PRIORITIES.map((priority) => (
                  <option key={priority} value={priority}>
                    {t(`status.priority.${priority}`)}
                  </option>
                ))}
              </Select>
            </Field>
          </div>

          <div
            role="tabpanel"
            id="project-panel-advanced"
            aria-labelledby="project-tab-advanced"
            hidden={activeTab !== 'advanced'}
            className="flex flex-col gap-4"
          >
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
            ) : (
              <p className="text-sm text-foreground-muted">
                {t('projects.form.advanced.defaults')}
              </p>
            )}
          </div>

          <div
            role="tabpanel"
            id="project-panel-repository"
            aria-labelledby="project-tab-repository"
            hidden={activeTab !== 'repository'}
            className="flex flex-col gap-4"
          >
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
            id="project-panel-workflow"
            aria-labelledby="project-tab-workflow"
            hidden={activeTab !== 'workflow'}
            className="flex flex-col gap-4"
          >
            {workflowTemplates.length > 0 ? (
              <>
                <Field
                  htmlFor="project-workflow-template"
                  label={t('projects.form.workflow.template')}
                  hint={
                    initial
                      ? t('projects.form.workflow.linkedHint')
                      : t('projects.form.workflow.templateHint')
                  }
                >
                  <Select
                    id="project-workflow-template"
                    disabled={Boolean(initial)}
                    {...register('workflowTemplateId')}
                  >
                    {workflowTemplates
                      .filter(
                        (template) =>
                          template.currentVersionId !== null && template.state !== 'archived',
                      )
                      .map((template) => (
                        <option key={template.id} value={template.id}>
                          {template.name}
                        </option>
                      ))}
                  </Select>
                </Field>
                {workflowRecommendation?.template.id === selectedWorkflowTemplateId ? (
                  <Badge variant="brand">{t('projects.form.workflow.recommended')}</Badge>
                ) : null}
              </>
            ) : (
              <p className="text-sm text-foreground-muted">
                {t('projects.form.workflow.unavailable')}
              </p>
            )}
          </div>

          <div
            role="tabpanel"
            id="project-panel-brand"
            aria-labelledby="project-tab-brand"
            hidden={activeTab !== 'brand'}
            className="flex flex-col gap-4"
          >
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
                  logoFile={pendingLogoFile}
                  onLogoFileChange={setPendingLogoFile}
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
              {profilesQuery.isLoading ? (
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

        {initial && (initial.configHistory?.length ?? 0) > 0 && (
          <section
            aria-labelledby="config-history-title"
            className="mt-6 flex flex-col gap-2 border-t border-border pt-4"
          >
            <h3 id="config-history-title" className="text-sm font-semibold">
              {t('projects.config.historyTitle')}
            </h3>
            <ul className="flex flex-col gap-1">
              {[...(initial.configHistory ?? [])].reverse().map((entry) => (
                <li
                  key={entry.version}
                  className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted"
                >
                  <Badge variant="outline">
                    {t('projects.config.version', { version: entry.version })}
                  </Badge>
                  <span>{formatDateTime(entry.changedAt, i18n.language)}</span>
                  <span>— {entry.summary}</span>
                </li>
              ))}
            </ul>
          </section>
        )}
      </CardContent>

      {impact && (
        <ModalDialog
          label={t('projects.impact.title')}
          onClose={() => setImpact(null)}
          className="max-w-lg"
        >
          <h3 className="font-heading text-lg font-semibold">{t('projects.impact.title')}</h3>
          <p className="text-sm text-foreground-muted">{t('projects.impact.body')}</p>
          <div className="flex flex-col gap-1">
            <span className="text-sm font-medium">{t('projects.impact.changedFields')}</span>
            <ul className="list-inside list-disc text-sm text-foreground-muted">
              {impact.fields.map((field) => (
                <li key={field}>{t(`projects.impact.fields.${field}`)}</li>
              ))}
            </ul>
          </div>
          <p className="text-xs text-foreground-muted">{t('projects.impact.versionNote')}</p>
          <label className="flex min-h-touch items-center gap-3 rounded-md border border-border bg-surface-elevated p-3 text-sm">
            <Checkbox
              checked={impactAccepted}
              onChange={(event) => setImpactAccepted(event.target.checked)}
            />
            {t('projects.impact.confirmCheckbox')}
          </label>
          <div className="flex flex-wrap gap-2">
            <Button type="button" disabled={!impactAccepted} onClick={confirmImpact}>
              {t('projects.impact.confirm')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setImpact(null)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}
    </Card>
  );
}
