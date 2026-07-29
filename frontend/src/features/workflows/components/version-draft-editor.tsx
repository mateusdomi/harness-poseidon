import { useState } from 'react';
import { Controller, useFieldArray, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';
import { z } from 'zod';

import {
  documentKindSchema,
  operationModeSchema,
  validateWorkflowVersionContent,
  type AgentDefinition,
  type PublishWorkflowVersionInput,
  type Skill,
  type Tool,
  type WorkflowPhaseConfig,
  type WorkflowTemplate,
  type WorkflowValidationIssue,
  type WorkflowVersion,
} from '@/api';
import { Badge, Button, Checkbox, Field, Input, Select, Textarea } from '@/design-system';
import { zodResolver } from '@/lib/form';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  usePublishWorkflowDraft,
  useUpdateWorkflowDraftVersion,
} from '@/features/workflows/hooks/use-workflows';

/* ---- schema do formulário (mensagens = CHAVES i18n) ---- */

const phaseEditorSchema = z.object({
  name: z.string().trim().min(1, 'common.validation.required'),
  objective: z.string(),
  context: z.string(),
  /** Gates separados por vírgula. */
  gates: z.string(),
  weight: z
    .string()
    .refine(
      (value) => Number.isFinite(Number(value)) && Number(value) >= 0 && Number(value) <= 100,
      'workflows.templates.errors.invalidWeight',
    ),
  documentKinds: z.array(documentKindSchema),
  /** Um critério por linha. */
  acceptanceCriteria: z.string(),
  agentIds: z.array(z.string()),
  skillIds: z.array(z.string()),
  toolIds: z.array(z.string()),
  dependsOn: z.array(z.string()),
  /** Uma condição por linha. */
  entryConditions: z.string(),
  exitConditions: z.string(),
  transitions: z.array(z.string()),
});

const versionEditorSchema = z.object({
  phases: z.array(phaseEditorSchema).min(1, 'workflows.templates.validation.phasesRequired'),
  defaultMode: z.union([operationModeSchema, z.literal('')]),
  changelog: z.string(),
});

export type VersionEditorValues = z.infer<typeof versionEditorSchema>;

function linesToList(value: string): string[] {
  return value
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function listToLines(value: string[] | undefined): string {
  return (value ?? []).join('\n');
}

function commaToList(value: string): string[] {
  return value
    .split(',')
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

/** Converte o rascunho do contrato em valores do formulário. */
function draftToEditorValues(draft: WorkflowVersion): VersionEditorValues {
  return {
    phases: draft.phases.map((name) => {
      const config = draft.phaseConfigs?.[name];
      return {
        name,
        objective: config?.objective ?? '',
        context: config?.context ?? '',
        gates: (draft.gatesByPhase[name] ?? []).join(', '),
        weight: String(config?.progressWeight ?? 25),
        documentKinds: config?.documentKinds ?? [],
        acceptanceCriteria: listToLines(config?.acceptanceCriteria),
        agentIds: config?.allowedAgentDefinitionIds ?? [],
        skillIds: config?.allowedSkillIds ?? [],
        toolIds: config?.allowedToolIds ?? [],
        dependsOn: config?.dependsOn ?? [],
        entryConditions: listToLines(config?.entryConditions),
        exitConditions: listToLines(config?.exitConditions),
        transitions: draft.transitions?.[name] ?? [],
      };
    }),
    defaultMode: draft.defaultOperationMode ?? '',
    changelog: draft.changelog ?? '',
  };
}

/** Converte os valores do formulário no payload de publicação/rascunho. */
function editorValuesToInput(values: VersionEditorValues): PublishWorkflowVersionInput {
  const gatesByPhase: Record<string, string[]> = {};
  const phaseConfigs: Record<string, WorkflowPhaseConfig> = {};
  const transitions: Record<string, string[]> = {};
  const phases = values.phases.map((phase) => phase.name.trim());

  for (const phase of values.phases) {
    const name = phase.name.trim();
    const gates = commaToList(phase.gates);
    if (gates.length > 0) gatesByPhase[name] = gates;
    phaseConfigs[name] = {
      documentKinds: phase.documentKinds,
      progressWeight: Number(phase.weight),
      allowedAgentDefinitionIds: phase.agentIds,
      ...(phase.objective.trim() ? { objective: phase.objective.trim() } : {}),
      ...(phase.context.trim() ? { context: phase.context.trim() } : {}),
      acceptanceCriteria: linesToList(phase.acceptanceCriteria),
      dependsOn: phase.dependsOn,
      entryConditions: linesToList(phase.entryConditions),
      exitConditions: linesToList(phase.exitConditions),
      allowedSkillIds: phase.skillIds,
      allowedToolIds: phase.toolIds,
    };
    if (phase.transitions.length > 0) transitions[name] = phase.transitions;
  }

  return {
    phases,
    gatesByPhase,
    phaseConfigs,
    defaultOperationMode: values.defaultMode === '' ? null : values.defaultMode,
    transitions,
    changelog: values.changelog.trim() || undefined,
  };
}

export interface VersionDraftEditorProps {
  template: WorkflowTemplate;
  draft: WorkflowVersion;
  agentDefinitions: AgentDefinition[];
  skills: Skill[];
  tools: Tool[];
  /** Versão em uso pela execução ativa do projeto (banner de impacto). */
  activeRunVersion: number | null;
  onClose: () => void;
}

const EMPTY_PHASE: VersionEditorValues['phases'][number] = {
  name: '',
  objective: '',
  context: '',
  gates: '',
  weight: '25',
  documentKinds: [],
  acceptanceCriteria: '',
  agentIds: [],
  skillIds: [],
  toolIds: [],
  dependsOn: [],
  entryConditions: '',
  exitConditions: '',
  transitions: [],
};

/**
 * Editor de versão em RASCUNHO (react-hook-form + zod): fases ordenadas
 * (mover cima/baixo, adicionar/remover), por fase — nome, objetivo,
 * contexto, gates, peso, documentos, critérios de aceite, agentes, skills,
 * ferramentas, dependências, condições de entrada/saída e transições.
 * "Salvar rascunho" persiste sem congelar; "Publicar" roda a validação do
 * Harness (local) e bloqueia com erros i18n se inválida.
 */
export function VersionDraftEditor({
  template,
  draft,
  agentDefinitions,
  skills,
  tools,
  activeRunVersion,
  onClose,
}: VersionDraftEditorProps) {
  const { t } = useTranslation();
  const updateDraft = useUpdateWorkflowDraftVersion();
  const publishDraft = usePublishWorkflowDraft();
  const [validationIssues, setValidationIssues] = useState<WorkflowValidationIssue[]>([]);
  const [saved, setSaved] = useState(false);
  const [apiError, setApiError] = useState<string | null>(null);

  const {
    register,
    control,
    watch,
    handleSubmit,
    formState: { errors },
  } = useForm<VersionEditorValues>({
    resolver: zodResolver(versionEditorSchema),
    defaultValues: draftToEditorValues(draft),
    mode: 'onSubmit',
  });

  const { fields, append, remove, move } = useFieldArray({ control, name: 'phases' });
  const phaseNames = watch('phases').map((phase) => phase.name.trim());
  const busy = updateDraft.isPending || publishDraft.isPending;

  const otherPhaseNames = [...new Set(phaseNames.filter((name) => name !== ''))];

  function toggleInList(list: string[], item: string): string[] {
    return list.includes(item) ? list.filter((entry) => entry !== item) : [...list, item];
  }

  function saveDraft(values: VersionEditorValues, onSuccess?: () => void) {
    setApiError(null);
    updateDraft.mutate(
      { versionId: draft.id, input: editorValuesToInput(values) },
      {
        onSuccess: () => {
          setSaved(true);
          onSuccess?.();
        },
        onError: (error) => setApiError(error.message),
      },
    );
  }

  function handleSave(values: VersionEditorValues) {
    setValidationIssues([]);
    saveDraft(values);
  }

  function handlePublish(values: VersionEditorValues) {
    const input = editorValuesToInput(values);
    const issues = validateWorkflowVersionContent(input);
    setValidationIssues(issues);
    if (issues.length > 0) return;
    // Publica o CONTEÚDO DO FORMULÁRIO: salva o rascunho e publica em seguida
    // (o mock revalida — publicação inválida é bloqueada com 422).
    saveDraft(values, () =>
      publishDraft.mutate(
        { versionId: draft.id, input: { changelog: input.changelog } },
        {
          onSuccess: onClose,
          onError: (error) => setApiError(error.message),
        },
      ),
    );
  }

  return (
    <ModalDialog
      label={t('workflows.editor.title', { name: template.name, version: draft.version })}
      onClose={onClose}
      className="max-w-3xl"
    >
      <h3 className="font-heading text-lg font-semibold">
        {t('workflows.editor.title', { name: template.name, version: draft.version })}
      </h3>
      <p className="text-xs text-foreground-muted">{t('workflows.editor.hint')}</p>

      {activeRunVersion !== null && (
        <p className="rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
          {t('workflows.templates.impactBanner', { version: activeRunVersion })}
        </p>
      )}

      <form
        className="flex flex-col gap-4"
        onSubmit={handleSubmit(handleSave)}
        noValidate
        onChange={() => setSaved(false)}
      >
        {fields.map((field, index) => {
          const currentName = phaseNames[index] || String(index);
          const selectablePhases = otherPhaseNames.filter((name) => name !== currentName);
          return (
            <fieldset
              key={field.id}
              className="flex flex-col gap-3 rounded-lg border border-border p-3"
            >
              <legend className="px-1 text-sm font-semibold">
                {t('workflows.editor.phaseLegend', { index: index + 1 })}
              </legend>

              <div className="flex flex-wrap items-end gap-2">
                <div className="min-w-48 flex-1">
                  <Field
                    htmlFor={`phase-name-${index}`}
                    label={t('workflows.editor.nameLabel')}
                    required
                    requiredLabel={t('common.requiredMark')}
                    error={
                      errors.phases?.[index]?.name
                        ? t(errors.phases[index]!.name!.message!)
                        : undefined
                    }
                  >
                    <Input id={`phase-name-${index}`} {...register(`phases.${index}.name`)} />
                  </Field>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  aria-label={t('workflows.editor.moveUp')}
                  disabled={index === 0}
                  onClick={() => move(index, index - 1)}
                >
                  <ArrowUp aria-hidden="true" className="size-4" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  aria-label={t('workflows.editor.moveDown')}
                  disabled={index === fields.length - 1}
                  onClick={() => move(index, index + 1)}
                >
                  <ArrowDown aria-hidden="true" className="size-4" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  aria-label={t('workflows.editor.removePhase')}
                  disabled={fields.length === 1}
                  onClick={() => remove(index)}
                >
                  <Trash2 aria-hidden="true" className="size-4" />
                </Button>
              </div>

              <Field
                htmlFor={`phase-objective-${index}`}
                label={t('workflows.editor.objectiveLabel')}
              >
                <Input id={`phase-objective-${index}`} {...register(`phases.${index}.objective`)} />
              </Field>

              <Field htmlFor={`phase-context-${index}`} label={t('workflows.editor.contextLabel')}>
                <Textarea
                  id={`phase-context-${index}`}
                  rows={2}
                  {...register(`phases.${index}.context`)}
                />
              </Field>

              <div className="grid gap-4 md:grid-cols-2">
                <Field
                  htmlFor={`phase-gates-${index}`}
                  label={t('workflows.templates.gatesLabel')}
                  hint={t('workflows.templates.gatesHint')}
                >
                  <Input id={`phase-gates-${index}`} {...register(`phases.${index}.gates`)} />
                </Field>
                <Field
                  htmlFor={`phase-weight-${index}`}
                  label={t('workflows.templates.weightLabel')}
                  error={
                    errors.phases?.[index]?.weight
                      ? t(errors.phases[index]!.weight!.message!, {
                          phase: phaseNames[index] || String(index + 1),
                        })
                      : undefined
                  }
                >
                  <Input
                    id={`phase-weight-${index}`}
                    type="number"
                    min={0}
                    max={100}
                    {...register(`phases.${index}.weight`)}
                  />
                </Field>
              </div>

              <Controller
                control={control}
                name={`phases.${index}.documentKinds`}
                render={({ field: kindField }) => (
                  <div className="flex flex-col gap-1">
                    <span className="text-sm font-medium">
                      {t('workflows.templates.documentKindsLabel')}
                    </span>
                    <div className="flex flex-wrap gap-x-4 gap-y-1">
                      {documentKindSchema.options.map((kind) => (
                        <label key={kind} className="flex min-h-11 items-center gap-2 text-xs">
                          <Checkbox
                            checked={kindField.value.includes(kind)}
                            onChange={() => kindField.onChange(toggleInList(kindField.value, kind))}
                          />
                          {t(`status.documentKind.${kind}`)}
                        </label>
                      ))}
                    </div>
                  </div>
                )}
              />

              <Field
                htmlFor={`phase-criteria-${index}`}
                label={t('workflows.editor.acceptanceCriteriaLabel')}
                hint={t('workflows.editor.linesHint')}
              >
                <Textarea
                  id={`phase-criteria-${index}`}
                  rows={2}
                  {...register(`phases.${index}.acceptanceCriteria`)}
                />
              </Field>

              <Controller
                control={control}
                name={`phases.${index}.agentIds`}
                render={({ field: agentField }) => (
                  <div className="flex flex-col gap-1">
                    <span className="text-sm font-medium">
                      {t('workflows.templates.agentsLabel')}
                    </span>
                    <div className="flex flex-wrap gap-x-4 gap-y-1">
                      {agentDefinitions.map((definition) => (
                        <label
                          key={definition.id}
                          className="flex min-h-11 items-center gap-2 text-xs"
                        >
                          <Checkbox
                            checked={agentField.value.includes(definition.id)}
                            onChange={() =>
                              agentField.onChange(toggleInList(agentField.value, definition.id))
                            }
                          />
                          {definition.name}
                        </label>
                      ))}
                    </div>
                  </div>
                )}
              />

              <Controller
                control={control}
                name={`phases.${index}.skillIds`}
                render={({ field: skillField }) => (
                  <div className="flex flex-col gap-1">
                    <span className="text-sm font-medium">{t('workflows.editor.skillsLabel')}</span>
                    <div className="flex flex-wrap gap-x-4 gap-y-1">
                      {skills.map((skill) => (
                        <label key={skill.id} className="flex min-h-11 items-center gap-2 text-xs">
                          <Checkbox
                            checked={skillField.value.includes(skill.id)}
                            onChange={() =>
                              skillField.onChange(toggleInList(skillField.value, skill.id))
                            }
                          />
                          {skill.name}
                        </label>
                      ))}
                    </div>
                  </div>
                )}
              />

              <Controller
                control={control}
                name={`phases.${index}.toolIds`}
                render={({ field: toolField }) => (
                  <div className="flex flex-col gap-1">
                    <span className="text-sm font-medium">{t('workflows.editor.toolsLabel')}</span>
                    <div className="flex flex-wrap gap-x-4 gap-y-1">
                      {tools.map((tool) => (
                        <label key={tool.id} className="flex min-h-11 items-center gap-2 text-xs">
                          <Checkbox
                            checked={toolField.value.includes(tool.id)}
                            onChange={() =>
                              toolField.onChange(toggleInList(toolField.value, tool.id))
                            }
                          />
                          {tool.name}
                        </label>
                      ))}
                    </div>
                  </div>
                )}
              />

              {selectablePhases.length > 0 && (
                <>
                  <Controller
                    control={control}
                    name={`phases.${index}.dependsOn`}
                    render={({ field: dependsField }) => (
                      <div className="flex flex-col gap-1">
                        <span className="text-sm font-medium">
                          {t('workflows.editor.dependsOnLabel')}
                        </span>
                        <div className="flex flex-wrap gap-x-4 gap-y-1">
                          {selectablePhases.map((other) => (
                            <label key={other} className="flex min-h-11 items-center gap-2 text-xs">
                              <Checkbox
                                checked={dependsField.value.includes(other)}
                                onChange={() =>
                                  dependsField.onChange(toggleInList(dependsField.value, other))
                                }
                              />
                              {other}
                            </label>
                          ))}
                        </div>
                      </div>
                    )}
                  />
                  <Controller
                    control={control}
                    name={`phases.${index}.transitions`}
                    render={({ field: transitionField }) => (
                      <div className="flex flex-col gap-1">
                        <span className="text-sm font-medium">
                          {t('workflows.templates.transitionsLabel')}
                        </span>
                        <div className="flex flex-wrap gap-x-4 gap-y-1">
                          {selectablePhases.map((other) => (
                            <label key={other} className="flex min-h-11 items-center gap-2 text-xs">
                              <Checkbox
                                checked={transitionField.value.includes(other)}
                                onChange={() =>
                                  transitionField.onChange(
                                    toggleInList(transitionField.value, other),
                                  )
                                }
                              />
                              {other}
                            </label>
                          ))}
                        </div>
                      </div>
                    )}
                  />
                </>
              )}

              <div className="grid gap-4 md:grid-cols-2">
                <Field
                  htmlFor={`phase-entry-${index}`}
                  label={t('workflows.editor.entryConditionsLabel')}
                  hint={t('workflows.editor.linesHint')}
                >
                  <Textarea
                    id={`phase-entry-${index}`}
                    rows={2}
                    {...register(`phases.${index}.entryConditions`)}
                  />
                </Field>
                <Field
                  htmlFor={`phase-exit-${index}`}
                  label={t('workflows.editor.exitConditionsLabel')}
                  hint={t('workflows.editor.linesHint')}
                >
                  <Textarea
                    id={`phase-exit-${index}`}
                    rows={2}
                    {...register(`phases.${index}.exitConditions`)}
                  />
                </Field>
              </div>
            </fieldset>
          );
        })}

        <div>
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => append({ ...EMPTY_PHASE })}
          >
            <Plus aria-hidden="true" className="size-4" />
            {t('workflows.editor.addPhase')}
          </Button>
        </div>

        <Field htmlFor="version-default-mode" label={t('workflows.templates.defaultModeLabel')}>
          <Select id="version-default-mode" {...register('defaultMode')}>
            <option value="">{t('workflows.templates.noDefaultMode')}</option>
            {operationModeSchema.options.map((option) => (
              <option key={option} value={option}>
                {t(`status.operationMode.${option}`)}
              </option>
            ))}
          </Select>
        </Field>

        <Field htmlFor="version-changelog" label={t('workflows.templates.changelogLabel')}>
          <Input id="version-changelog" {...register('changelog')} />
        </Field>

        {validationIssues.length > 0 && (
          <div role="alert" className="rounded-md border border-error p-3">
            <p className="text-sm font-medium text-error">
              {t('workflows.templates.validationTitle')}
            </p>
            <ul className="mt-1 list-inside list-disc text-xs text-error">
              {validationIssues.map((issue, index) => (
                <li key={`${issue.key}-${index}`}>{t(issue.key, issue.params)}</li>
              ))}
            </ul>
          </div>
        )}

        {apiError && (
          <p role="alert" className="text-xs text-error">
            {apiError}
          </p>
        )}

        <div className="flex flex-wrap items-center gap-2">
          <Button type="submit" variant="outline" disabled={busy}>
            {t('workflows.editor.saveDraft')}
          </Button>
          <Button type="button" disabled={busy} onClick={handleSubmit(handlePublish)}>
            {t('workflows.templates.publish')}
          </Button>
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          {saved && (
            <Badge variant="success" role="status">
              {t('workflows.editor.saved')}
            </Badge>
          )}
        </div>
      </form>
    </ModalDialog>
  );
}
