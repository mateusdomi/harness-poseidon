import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';

import {
  agentRoleSchema,
  type Account,
  type AgentDefinition,
  type Model,
  type Provider,
  type Skill,
  type Tool,
} from '@/api';
import { Button, Checkbox, Field, Input, Select, Textarea } from '@/design-system';
import { zodResolver } from '@/lib/form';
import {
  accountOptionLabel,
  definitionFormSchema,
  definitionToFormValues,
  EMPTY_DEFINITION_VALUES,
  type DefinitionFormValues,
} from '@/features/orchestrator/lib/definitions-form';
import { ModalDialog } from '@/features/shared/components/modal-dialog';

function toggleInList(list: string[], item: string): string[] {
  return list.includes(item) ? list.filter((entry) => entry !== item) : [...list, item];
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
 * Formulário de criação/edição de definição de agente (modal): identidade,
 * comportamento (persona/missão/etc.) e execução (modelo, conta, esforço).
 * Deixa explícito que a definição é a configuração LÓGICA do agente e a
 * conta de provider é a credencial — conceitos diferentes.
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

  const {
    register,
    control,
    handleSubmit,
    formState: { errors },
  } = useForm<DefinitionFormValues>({
    resolver: zodResolver(definitionFormSchema),
    defaultValues: initial ? definitionToFormValues(initial) : EMPTY_DEFINITION_VALUES,
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  const enabledModels = models.filter((model) => model.enabled);

  return (
    <ModalDialog label={title} onClose={onClose} className="max-w-3xl">
      <h2 className="font-heading text-lg font-semibold">{title}</h2>
      <p className="text-sm text-foreground-muted">{t('orchestrator.definitions.help')}</p>

      <form onSubmit={handleSubmit(onSubmit)} noValidate className="flex flex-col gap-5">
        {apiError ? (
          <p role="alert" className="rounded-md border border-error bg-surface-elevated p-3 text-sm text-error">
            {apiError}
          </p>
        ) : null}

        <section className="flex flex-col gap-4">
          <h3 className="text-sm font-semibold">{t('orchestrator.definitions.form.sectionIdentity')}</h3>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              htmlFor="definition-name"
              label={t('orchestrator.definitions.fields.name')}
              required
              requiredLabel={t('common.requiredMark')}
              error={errors.name ? t(errors.name.message!) : undefined}
            >
              <Input id="definition-name" aria-invalid={Boolean(errors.name)} {...register('name')} />
            </Field>
            <Field
              htmlFor="definition-key"
              label={t('orchestrator.definitions.fields.key')}
              required
              requiredLabel={t('common.requiredMark')}
              hint={t('orchestrator.definitions.form.keyHint')}
              error={errors.key ? t(errors.key.message!) : undefined}
            >
              <Input id="definition-key" aria-invalid={Boolean(errors.key)} {...register('key')} />
            </Field>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field htmlFor="definition-role" label={t('orchestrator.definitions.fields.role')}>
              <Select id="definition-role" {...register('role')}>
                {agentRoleSchema.options.map((role) => (
                  <option key={role} value={role}>
                    {t(`orchestrator.definitions.role.${role}`)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field htmlFor="definition-specialty" label={t('orchestrator.definitions.fields.specialty')}>
              <Input id="definition-specialty" {...register('specialty')} />
            </Field>
          </div>
          <Field htmlFor="definition-description" label={t('orchestrator.definitions.fields.description')}>
            <Textarea id="definition-description" {...register('description')} />
          </Field>
          <Field htmlFor="definition-team" label={t('orchestrator.definitions.fields.team')}>
            <Input id="definition-team" {...register('team')} />
          </Field>
        </section>

        <section className="flex flex-col gap-4">
          <h3 className="text-sm font-semibold">{t('orchestrator.definitions.form.sectionBehavior')}</h3>
          <Field htmlFor="definition-persona" label={t('orchestrator.definitions.fields.persona')}>
            <Textarea id="definition-persona" {...register('persona')} />
          </Field>
          <Field htmlFor="definition-mission" label={t('orchestrator.definitions.fields.mission')}>
            <Textarea id="definition-mission" {...register('mission')} />
          </Field>
          <Field
            htmlFor="definition-responsibilities"
            label={t('orchestrator.definitions.fields.responsibilities')}
          >
            <Textarea id="definition-responsibilities" {...register('responsibilities')} />
          </Field>
          <Field htmlFor="definition-instructions" label={t('orchestrator.definitions.fields.instructions')}>
            <Textarea id="definition-instructions" {...register('instructions')} />
          </Field>
          <Field htmlFor="definition-restrictions" label={t('orchestrator.definitions.fields.restrictions')}>
            <Textarea id="definition-restrictions" {...register('restrictions')} />
          </Field>
          <Field
            htmlFor="definition-best-practices"
            label={t('orchestrator.definitions.fields.bestPractices')}
          >
            <Textarea id="definition-best-practices" {...register('bestPractices')} />
          </Field>
        </section>

        <section className="flex flex-col gap-4">
          <h3 className="text-sm font-semibold">{t('orchestrator.definitions.form.sectionExecution')}</h3>
          <Field
            htmlFor="definition-stacks"
            label={t('orchestrator.definitions.fields.stacks')}
            hint={t('orchestrator.definitions.form.stacksHint')}
          >
            <Input id="definition-stacks" {...register('stacksText')} />
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field htmlFor="definition-model" label={t('orchestrator.definitions.fields.defaultModel')}>
              <Select id="definition-model" {...register('defaultModelId')}>
                <option value="">{t('orchestrator.definitions.form.noModel')}</option>
                {enabledModels.map((model) => (
                  <option key={model.id} value={model.id}>
                    {model.displayName}
                  </option>
                ))}
              </Select>
            </Field>
            <Field htmlFor="definition-effort" label={t('orchestrator.definitions.fields.defaultEffort')}>
              <Select id="definition-effort" {...register('defaultEffort')}>
                <option value="">{t('orchestrator.definitions.form.notDefined')}</option>
                {(['low', 'medium', 'high'] as const).map((effort) => (
                  <option key={effort} value={effort}>
                    {t(`orchestrator.definitions.effort.${effort}`)}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <Field
            htmlFor="definition-account"
            label={t('orchestrator.definitions.fields.preferredAccount')}
            hint={t('orchestrator.definitions.form.accountHint')}
          >
            <Select id="definition-account" {...register('preferredAccountId')}>
              <option value="">{t('orchestrator.definitions.form.noAccount')}</option>
              {accounts.map((account) => (
                <option key={account.id} value={account.id}>
                  {accountOptionLabel(account, providers)}
                </option>
              ))}
            </Select>
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field htmlFor="definition-actor-critic" label={t('orchestrator.definitions.fields.actorCritic')}>
              <Select id="definition-actor-critic" {...register('actorCritic')}>
                <option value="">{t('orchestrator.definitions.form.notDefined')}</option>
                {(['actor', 'critic'] as const).map((value) => (
                  <option key={value} value={value}>
                    {t(`orchestrator.definitions.actorCritic.${value}`)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field htmlFor="definition-risk" label={t('orchestrator.definitions.fields.risk')}>
              <Select id="definition-risk" {...register('risk')}>
                <option value="">{t('orchestrator.definitions.form.notDefined')}</option>
                {(['low', 'medium', 'high'] as const).map((risk) => (
                  <option key={risk} value={risk}>
                    {t(`orchestrator.definitions.risk.${risk}`)}
                  </option>
                ))}
              </Select>
            </Field>
          </div>

          <fieldset className="flex flex-col gap-2">
            <legend className="text-sm font-medium">{t('orchestrator.definitions.fields.skills')}</legend>
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
          </fieldset>

          <fieldset className="flex flex-col gap-2">
            <legend className="text-sm font-medium">{t('orchestrator.definitions.fields.tools')}</legend>
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
          </fieldset>

          <fieldset className="flex flex-col gap-2">
            <legend className="text-sm font-medium">
              {t('orchestrator.definitions.fields.fallbackModels')}
            </legend>
            <Controller
              control={control}
              name="fallbackModelIds"
              render={({ field }) => (
                <ul className="flex flex-col gap-2">
                  {models.map((model) => (
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

        <div className="flex flex-wrap gap-3">
          <Button type="submit" disabled={submitting}>
            {submitting
              ? t('common.states.loading')
              : initial
                ? t('common.actions.save')
                : t('orchestrator.definitions.form.create')}
          </Button>
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
        </div>
      </form>
    </ModalDialog>
  );
}
