import { useEffect, useMemo, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { ChevronDown, ChevronRight } from 'lucide-react';

import {
  agentRoleSchema,
  type Account,
  type AgentDefinition,
  type Model,
  type Provider,
  type Skill,
  type Tool,
} from '@/api';
import { Button, Checkbox, Field, Input, Select, Textarea, Tooltip } from '@/design-system';
import { zodResolver } from '@/lib/form';
import {
  accountOptionLabel,
  definitionFormSchema,
  definitionKeyFromName,
  definitionToFormValues,
  effortOptionsForModel,
  hasEffortMappings,
  DEFINITION_STEP_FIELDS,
  DEFINITION_STEPS,
  EMPTY_DEFINITION_VALUES,
  type DefinitionFormValues,
  type DefinitionStep,
} from '@/features/orchestrator/lib/definitions-form';
import { DefinitionImportPanel } from '@/features/orchestrator/components/definition-import-panel';
import { ModalDialog } from '@/features/shared/components/modal-dialog';

function toggleInList(list: string[], item: string): string[] {
  return list.includes(item) ? list.filter((entry) => entry !== item) : [...list, item];
}

/** Rótulo + descrição + exemplo de um campo guiado (§16 "cada campo"). */
function GuidedField({
  id,
  fieldKey,
  required = false,
  error,
  children,
}: {
  id: string;
  /** Sufixo das chaves i18n em `orchestrator.definitions.guide.<fieldKey>`. */
  fieldKey: string;
  required?: boolean;
  error?: string;
  children: React.ReactNode;
}) {
  const { t } = useTranslation();
  const base = `orchestrator.definitions.guide.${fieldKey}`;
  const description = t(`${base}.description`, { defaultValue: '' });
  const example = t(`${base}.example`, { defaultValue: '' });
  const impact = t(`${base}.impact`, { defaultValue: '' });

  return (
    <Field
      htmlFor={id}
      label={
        <span className="flex flex-wrap items-center gap-1.5">
          {t(`orchestrator.definitions.fields.${fieldKey}`)}
          {impact ? (
            <Tooltip label={impact}>
              <span
                aria-label={impact}
                tabIndex={0}
                className="flex size-4 items-center justify-center rounded-full border border-border-strong text-[10px] text-foreground-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
              >
                ?
              </span>
            </Tooltip>
          ) : null}
        </span>
      }
      required={required}
      requiredLabel={t('common.requiredMark')}
      hint={
        description || example ? (
          <span className="flex flex-col gap-0.5">
            {description ? <span>{description}</span> : null}
            {example ? (
              <span className="italic">
                {t('orchestrator.definitions.guide.exampleLabel')}: {example}
              </span>
            ) : null}
          </span>
        ) : undefined
      }
      error={error}
    >
      {children}
    </Field>
  );
}

export interface DefinitionFormDialogProps {
  /** Definição em edição; ausente = criação. */
  initial?: AgentDefinition;
  skills: Skill[];
  tools: Tool[];
  models: Model[];
  accounts: Account[];
  providers: Provider[];
  submitting: boolean;
  /** Erro da mutation (ex.: 409 ao editar definição arquivada). */
  apiError: string | null;
  onSubmit: (values: DefinitionFormValues) => void;
  onClose: () => void;
}

/**
 * Formulário GUIADO de definição de agente (§16), em 6 passos: identidade,
 * papel/especialidade, comportamento, capacidades, execução e revisão.
 * Cada campo carrega descrição, exemplo e impacto; a chave técnica é derivada
 * do nome e fica em seção avançada; o esforço reflete os `effortMappings`
 * reais do modelo escolhido. Catálogos (modelos, skills, ferramentas, contas)
 * vêm de APIs reais — quando vazios, oferecemos a CTA de gestão.
 */
export function DefinitionFormDialog({
  initial,
  skills,
  tools,
  models,
  accounts,
  providers,
  submitting,
  apiError,
  onSubmit,
  onClose,
}: DefinitionFormDialogProps) {
  const { t } = useTranslation();
  const title = initial
    ? t('orchestrator.definitions.form.editTitle', { name: initial.name })
    : t('orchestrator.definitions.form.createTitle');

  const [stepIndex, setStepIndex] = useState(0);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  // Em edição a chave já foi usada; em criação, derivamos do nome até editar.
  const keyEditedRef = useRef(Boolean(initial));

  const {
    register,
    control,
    handleSubmit,
    watch,
    setValue,
    reset,
    trigger,
    formState: { errors },
  } = useForm<DefinitionFormValues>({
    resolver: zodResolver(definitionFormSchema),
    defaultValues: initial ? definitionToFormValues(initial) : EMPTY_DEFINITION_VALUES,
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  const values = watch();
  const step: DefinitionStep = DEFINITION_STEPS[stepIndex];
  const isLastStep = stepIndex === DEFINITION_STEPS.length - 1;

  // Chave técnica derivada do nome enquanto o usuário não a editar (§16.2).
  useEffect(() => {
    if (!keyEditedRef.current) {
      setValue('key', definitionKeyFromName(values.name ?? ''), { shouldValidate: false });
    }
  }, [values.name, setValue]);

  const enabledModels = useMemo(() => models.filter((model) => model.enabled), [models]);
  const selectedModel = useMemo(
    () => models.find((model) => model.id === values.defaultModelId) ?? null,
    [models, values.defaultModelId],
  );
  const effortOptions = useMemo(() => effortOptionsForModel(selectedModel), [selectedModel]);
  const effortKnown = hasEffortMappings(selectedModel);

  // Esforço incompatível com o modelo escolhido nunca fica silenciosamente
  // aplicado: limpamos a seleção e a UI explica (§16.7).
  useEffect(() => {
    if (values.defaultEffort === '') return;
    const option = effortOptions.find((entry) => entry.value === values.defaultEffort);
    if (option && !option.supported) {
      setValue('defaultEffort', '', { shouldValidate: false });
    }
  }, [values.defaultEffort, effortOptions, setValue]);

  const keyReg = register('key');
  const keyError = errors.key ? t(errors.key.message!) : undefined;

  async function goNext() {
    const fields = DEFINITION_STEP_FIELDS[step];
    const valid = fields.length === 0 ? true : await trigger(fields);
    if (!valid) {
      // Erro na chave (seção avançada) precisa ficar visível.
      if (errors.key) setAdvancedOpen(true);
      return;
    }
    setStepIndex((index) => Math.min(index + 1, DEFINITION_STEPS.length - 1));
  }

  const catalogEmpty = {
    models: enabledModels.length === 0,
    skills: skills.length === 0,
    tools: tools.length === 0,
    accounts: accounts.length === 0,
  };

  return (
    <ModalDialog label={title} onClose={onClose} className="max-w-3xl">
      <h2 className="font-heading text-lg font-semibold">{title}</h2>
      <p className="text-sm text-foreground-muted">{t('orchestrator.definitions.help')}</p>

      {/* Template / importação — só na criação (§16.8). */}
      {!initial ? (
        <DefinitionImportPanel
          onApply={(imported) => {
            reset(imported);
            keyEditedRef.current = imported.key !== '';
            setStepIndex(0);
          }}
        />
      ) : null}

      {/* Progresso dos passos */}
      <ol className="flex flex-wrap gap-1.5" aria-label={t('orchestrator.definitions.steps.label')}>
        {DEFINITION_STEPS.map((entry, index) => (
          <li key={entry}>
            <button
              type="button"
              // Só permitimos voltar a passos já visitados.
              disabled={index > stepIndex}
              onClick={() => setStepIndex(index)}
              aria-current={index === stepIndex ? 'step' : undefined}
              className={
                index === stepIndex
                  ? 'min-h-touch rounded-md border border-brand px-2.5 py-1.5 text-xs font-medium text-brand-strong sm:min-h-0'
                  : index < stepIndex
                    ? 'min-h-touch rounded-md border border-border px-2.5 py-1.5 text-xs text-foreground-muted hover:text-foreground sm:min-h-0'
                    : 'min-h-touch rounded-md border border-border px-2.5 py-1.5 text-xs text-foreground-muted opacity-60 sm:min-h-0'
              }
            >
              {index + 1}. {t(`orchestrator.definitions.steps.${entry}`)}
            </button>
          </li>
        ))}
      </ol>

      <form onSubmit={handleSubmit(onSubmit)} noValidate className="flex flex-col gap-5">
        {apiError ? (
          <p role="alert" className="rounded-md border border-error bg-surface-elevated p-3 text-sm text-error">
            {apiError}
          </p>
        ) : null}

        {/* 1. Identidade */}
        {step === 'identity' ? (
          <section className="flex flex-col gap-4">
            <GuidedField
              id="definition-name"
              fieldKey="name"
              required
              error={errors.name ? t(errors.name.message!) : undefined}
            >
              <Input id="definition-name" aria-invalid={Boolean(errors.name)} {...register('name')} />
            </GuidedField>

            <GuidedField
              id="definition-description"
              fieldKey="description"
              error={errors.description ? t(errors.description.message!) : undefined}
            >
              <Textarea id="definition-description" rows={3} {...register('description')} />
            </GuidedField>

            {/* Chave técnica em seção avançada (§16.2) */}
            <div className="rounded-lg border border-border">
              <button
                type="button"
                onClick={() => setAdvancedOpen((open) => !open)}
                aria-expanded={advancedOpen || Boolean(keyError)}
                className="flex min-h-touch w-full items-center gap-2 px-4 py-2 text-sm font-medium text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
              >
                {advancedOpen || keyError ? (
                  <ChevronDown aria-hidden="true" className="size-4" />
                ) : (
                  <ChevronRight aria-hidden="true" className="size-4" />
                )}
                {t('orchestrator.definitions.form.advanced')}
              </button>
              {advancedOpen || keyError ? (
                <div className="flex flex-col gap-1.5 border-t border-border p-4">
                  <GuidedField id="definition-key" fieldKey="key" required error={keyError}>
                    <Input
                      id="definition-key"
                      aria-invalid={Boolean(errors.key)}
                      // A chave é imutável depois de criada (contrato do backend).
                      disabled={Boolean(initial)}
                      {...keyReg}
                      onChange={(event) => {
                        keyEditedRef.current = true;
                        void keyReg.onChange(event);
                        void trigger('key');
                      }}
                    />
                  </GuidedField>
                  <p className="text-xs text-foreground-muted">
                    {initial
                      ? t('orchestrator.definitions.form.keyLocked')
                      : t('orchestrator.definitions.form.keyPreview', {
                          key: values.key || '—',
                        })}
                  </p>
                </div>
              ) : null}
            </div>
          </section>
        ) : null}

        {/* 2. Papel e especialidade */}
        {step === 'role' ? (
          <section className="flex flex-col gap-4">
            <GuidedField id="definition-role" fieldKey="role">
              <Select id="definition-role" {...register('role')}>
                {agentRoleSchema.options.map((role) => (
                  <option key={role} value={role}>
                    {t(`orchestrator.definitions.role.${role}`)}
                  </option>
                ))}
              </Select>
            </GuidedField>
            {/* Diferença entre chefe e especialista, explicada (§16.3). */}
            <dl className="flex flex-col gap-2 rounded-md border border-border bg-surface-elevated p-3 text-xs">
              <div className="flex flex-col gap-0.5">
                <dt className="font-medium">{t('orchestrator.definitions.role.chief')}</dt>
                <dd className="text-foreground-muted">
                  {t('orchestrator.definitions.roleHelp.chief')}
                </dd>
              </div>
              <div className="flex flex-col gap-0.5">
                <dt className="font-medium">{t('orchestrator.definitions.role.specialist')}</dt>
                <dd className="text-foreground-muted">
                  {t('orchestrator.definitions.roleHelp.specialist')}
                </dd>
              </div>
            </dl>

            <GuidedField id="definition-specialty" fieldKey="specialty">
              <Input
                id="definition-specialty"
                list="definition-specialty-options"
                {...register('specialty')}
              />
            </GuidedField>
            {/* Catálogo derivado das definições reais já cadastradas. */}
            <datalist id="definition-specialty-options">
              {SPECIALTY_SUGGESTIONS.map((entry) => (
                <option key={entry} value={entry} />
              ))}
            </datalist>

            <GuidedField id="definition-team" fieldKey="team">
              <Input id="definition-team" {...register('team')} />
            </GuidedField>
          </section>
        ) : null}

        {/* 3. Comportamento */}
        {step === 'behavior' ? (
          <section className="flex flex-col gap-4">
            {(
              [
                'persona',
                'mission',
                'responsibilities',
                'instructions',
                'restrictions',
                'bestPractices',
              ] as const
            ).map((fieldKey) => (
              <GuidedField key={fieldKey} id={`definition-${fieldKey}`} fieldKey={fieldKey}>
                <Textarea
                  id={`definition-${fieldKey}`}
                  rows={3}
                  placeholder={t(`orchestrator.definitions.guide.${fieldKey}.placeholder`, {
                    defaultValue: '',
                  })}
                  {...register(fieldKey)}
                />
              </GuidedField>
            ))}
          </section>
        ) : null}

        {/* 4. Capacidades */}
        {step === 'capabilities' ? (
          <section className="flex flex-col gap-4">
            <GuidedField id="definition-stacks" fieldKey="stacks">
              <Input id="definition-stacks" {...register('stacksText')} />
            </GuidedField>

            <fieldset className="flex flex-col gap-2">
              <legend className="text-sm font-medium">
                {t('orchestrator.definitions.fields.skills')}
              </legend>
              <p className="text-xs text-foreground-muted">
                {t('orchestrator.definitions.guide.skills.description')}
              </p>
              {catalogEmpty.skills ? (
                <EmptyCatalog messageKey="skills" to="/tools" />
              ) : (
                <Controller
                  control={control}
                  name="skillIds"
                  render={({ field }) => (
                    <ul className="flex flex-col gap-2">
                      {skills.map((skill) => (
                        <li key={skill.id}>
                          <label
                            htmlFor={`definition-skill-${skill.id}`}
                            className="flex min-h-touch items-center gap-3 rounded-md border border-border bg-surface-elevated p-3 text-sm"
                          >
                            <Checkbox
                              id={`definition-skill-${skill.id}`}
                              checked={field.value.includes(skill.id)}
                              onChange={() => field.onChange(toggleInList(field.value, skill.id))}
                            />
                            {skill.name}
                          </label>
                        </li>
                      ))}
                    </ul>
                  )}
                />
              )}
            </fieldset>

            <fieldset className="flex flex-col gap-2">
              <legend className="text-sm font-medium">
                {t('orchestrator.definitions.fields.tools')}
              </legend>
              <p className="text-xs text-foreground-muted">
                {t('orchestrator.definitions.guide.tools.description')}
              </p>
              {catalogEmpty.tools ? (
                <EmptyCatalog messageKey="tools" to="/tools" />
              ) : (
                <Controller
                  control={control}
                  name="toolIds"
                  render={({ field }) => (
                    <ul className="flex flex-col gap-2">
                      {tools.map((tool) => (
                        <li key={tool.id}>
                          <label
                            htmlFor={`definition-tool-${tool.id}`}
                            className="flex min-h-touch items-center gap-3 rounded-md border border-border bg-surface-elevated p-3 text-sm"
                          >
                            <Checkbox
                              id={`definition-tool-${tool.id}`}
                              checked={field.value.includes(tool.id)}
                              onChange={() => field.onChange(toggleInList(field.value, tool.id))}
                            />
                            {tool.name}
                          </label>
                        </li>
                      ))}
                    </ul>
                  )}
                />
              )}
            </fieldset>
          </section>
        ) : null}

        {/* 5. Execução */}
        {step === 'execution' ? (
          <section className="flex flex-col gap-4">
            <GuidedField id="definition-model" fieldKey="defaultModel">
              {catalogEmpty.models ? (
                <EmptyCatalog messageKey="models" to="/providers" />
              ) : (
                <Select id="definition-model" {...register('defaultModelId')}>
                  <option value="">{t('orchestrator.definitions.form.noModel')}</option>
                  {enabledModels.map((model) => (
                    <option key={model.id} value={model.id}>
                      {model.displayName}
                    </option>
                  ))}
                </Select>
              )}
            </GuidedField>

            <GuidedField id="definition-effort" fieldKey="defaultEffort">
              <div className="flex flex-col gap-1.5">
                <Select id="definition-effort" {...register('defaultEffort')}>
                  <option value="">
                    {t('orchestrator.definitions.form.effortAuto')}
                  </option>
                  {effortOptions.map((option) => (
                    <option key={option.value} value={option.value} disabled={!option.supported}>
                      {t(`orchestrator.definitions.effort.${option.value}`)}
                      {option.supported
                        ? option.providerValue
                          ? ` — ${option.providerValue}`
                          : ''
                        : ` (${t('orchestrator.definitions.form.effortUnsupported')})`}
                    </option>
                  ))}
                </Select>
                <p className="text-xs text-foreground-muted">
                  {!selectedModel
                    ? t('orchestrator.definitions.form.effortNoModel')
                    : effortKnown
                      ? t('orchestrator.definitions.form.effortMapped')
                      : t('orchestrator.definitions.form.effortUnknown')}
                </p>
              </div>
            </GuidedField>

            <GuidedField id="definition-account" fieldKey="preferredAccount">
              {catalogEmpty.accounts ? (
                <EmptyCatalog messageKey="accounts" to="/providers" />
              ) : (
                <Select id="definition-account" {...register('preferredAccountId')}>
                  <option value="">{t('orchestrator.definitions.form.noAccount')}</option>
                  {accounts.map((account) => (
                    <option key={account.id} value={account.id}>
                      {accountOptionLabel(account, providers)}
                    </option>
                  ))}
                </Select>
              )}
            </GuidedField>

            <div className="grid gap-4 sm:grid-cols-2">
              <GuidedField id="definition-actor-critic" fieldKey="actorCritic">
                <Select id="definition-actor-critic" {...register('actorCritic')}>
                  <option value="">{t('orchestrator.definitions.form.notDefined')}</option>
                  {(['actor', 'critic'] as const).map((value) => (
                    <option key={value} value={value}>
                      {t(`orchestrator.definitions.actorCritic.${value}`)}
                    </option>
                  ))}
                </Select>
              </GuidedField>
              <GuidedField id="definition-risk" fieldKey="risk">
                <Select id="definition-risk" {...register('risk')}>
                  <option value="">{t('orchestrator.definitions.form.notDefined')}</option>
                  {(['low', 'medium', 'high'] as const).map((risk) => (
                    <option key={risk} value={risk}>
                      {t(`orchestrator.definitions.risk.${risk}`)}
                    </option>
                  ))}
                </Select>
              </GuidedField>
            </div>

            <fieldset className="flex flex-col gap-2">
              <legend className="text-sm font-medium">
                {t('orchestrator.definitions.fields.fallbackModels')}
              </legend>
              <p className="text-xs text-foreground-muted">
                {t('orchestrator.definitions.guide.fallbackModels.description')}
              </p>
              <Controller
                control={control}
                name="fallbackModelIds"
                render={({ field }) => (
                  <ul className="flex flex-col gap-2">
                    {enabledModels.map((model) => (
                      <li key={model.id}>
                        <label
                          htmlFor={`definition-fallback-${model.id}`}
                          className="flex min-h-touch items-center gap-3 rounded-md border border-border bg-surface-elevated p-3 text-sm"
                        >
                          <Checkbox
                            id={`definition-fallback-${model.id}`}
                            checked={field.value.includes(model.id)}
                            onChange={() => field.onChange(toggleInList(field.value, model.id))}
                          />
                          {model.displayName}
                        </label>
                      </li>
                    ))}
                  </ul>
                )}
              />
            </fieldset>
          </section>
        ) : null}

        {/* 6. Revisão */}
        {step === 'review' ? (
          <section className="flex flex-col gap-3">
            <p className="text-sm text-foreground-muted">
              {t('orchestrator.definitions.steps.reviewHelp')}
            </p>
            <dl className="flex flex-col gap-2 rounded-md border border-border bg-surface-elevated p-3 text-sm">
              <ReviewRow label={t('orchestrator.definitions.fields.name')} value={values.name} />
              <ReviewRow label={t('orchestrator.definitions.fields.key')} value={values.key} />
              <ReviewRow
                label={t('orchestrator.definitions.fields.role')}
                value={t(`orchestrator.definitions.role.${values.role}`)}
              />
              <ReviewRow
                label={t('orchestrator.definitions.fields.specialty')}
                value={values.specialty}
              />
              <ReviewRow label={t('orchestrator.definitions.fields.team')} value={values.team} />
              <ReviewRow
                label={t('orchestrator.definitions.fields.defaultModel')}
                value={selectedModel?.displayName ?? ''}
              />
              <ReviewRow
                label={t('orchestrator.definitions.fields.defaultEffort')}
                value={
                  values.defaultEffort
                    ? t(`orchestrator.definitions.effort.${values.defaultEffort}`)
                    : t('orchestrator.definitions.form.effortAuto')
                }
              />
              <ReviewRow
                label={t('orchestrator.definitions.fields.skills')}
                value={String(values.skillIds.length)}
              />
              <ReviewRow
                label={t('orchestrator.definitions.fields.tools')}
                value={String(values.toolIds.length)}
              />
            </dl>
          </section>
        ) : null}

        <div className="flex flex-wrap gap-3">
          {stepIndex > 0 ? (
            <Button type="button" variant="outline" onClick={() => setStepIndex((i) => i - 1)}>
              {t('common.actions.previous')}
            </Button>
          ) : null}
          {!isLastStep ? (
            <Button type="button" onClick={() => void goNext()}>
              {t('common.actions.next')}
            </Button>
          ) : (
            <Button type="submit" disabled={submitting}>
              {submitting
                ? t('common.states.loading')
                : initial
                  ? t('common.actions.save')
                  : t('orchestrator.definitions.form.create')}
            </Button>
          )}
          <Button type="button" variant="ghost" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
        </div>
      </form>
    </ModalDialog>
  );
}

/** Sugestões de especialidade — vocabulário observado, não enum de contrato. */
const SPECIALTY_SUGGESTIONS = [
  'Arquitetura',
  'Backend',
  'Frontend',
  'Dados',
  'Qualidade',
  'Revisão de código',
  'Segurança',
  'Infraestrutura',
  'Produto',
];

function ReviewRow({ label, value }: { label: string; value: string }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap items-center justify-between gap-2">
      <dt className="text-foreground-muted">{label}</dt>
      <dd className="font-medium">
        {value.trim() === '' ? t('orchestrator.definitions.form.notDefined') : value}
      </dd>
    </div>
  );
}

/** Catálogo real vazio → explica e leva à tela de gestão correta (§16.6). */
function EmptyCatalog({ messageKey, to }: { messageKey: string; to: string }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-start gap-2 rounded-md border border-dashed border-border p-3">
      <p className="text-sm text-foreground-muted">
        {t(`orchestrator.definitions.catalogEmpty.${messageKey}.body`)}
      </p>
      <Button asChild size="sm" variant="outline">
        <Link to={to}>{t(`orchestrator.definitions.catalogEmpty.${messageKey}.cta`)}</Link>
      </Button>
    </div>
  );
}
