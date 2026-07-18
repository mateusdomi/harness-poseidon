import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  documentKindSchema,
  operationModeSchema,
  type AgentDefinition,
  type DocumentKind,
  type OperationMode,
  type WorkflowPhaseConfig,
  type WorkflowTemplate,
  type WorkflowVersion,
} from '@/api';
import { Badge, Button, Checkbox, Field, Input, Select, Textarea } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { usePublishWorkflowVersion } from '@/features/workflows/hooks/use-workflows';

export interface TemplateAdminProps {
  templates: WorkflowTemplate[];
  versions: WorkflowVersion[];
  agentDefinitions: AgentDefinition[];
}

/** Configuração por fase no formulário de nova versão (indexada pela posição). */
interface PhaseForm {
  gates: string;
  weight: string;
  documentKinds: DocumentKind[];
  agentIds: string[];
  transitions: string[];
}

const EMPTY_PHASE_FORM: PhaseForm = {
  gates: '',
  weight: '25',
  documentKinds: [],
  agentIds: [],
  transitions: [],
};

/**
 * Administração de templates: lista templates e versões publicadas
 * (imutáveis) e permite criar/publicar uma nova versão — fases ordenadas,
 * gates por fase, documentos esperados, pesos de progresso, agentes
 * permitidos, modo padrão e regras de transição. Publicar emite
 * `workflow.versionPublished`.
 */
export function TemplateAdmin({ templates, versions, agentDefinitions }: TemplateAdminProps) {
  const { t, i18n } = useTranslation();
  const publish = usePublishWorkflowVersion();
  const [editingTemplate, setEditingTemplate] = useState<WorkflowTemplate | null>(null);
  const [phasesText, setPhasesText] = useState('');
  const [changelog, setChangelog] = useState('');
  const [defaultMode, setDefaultMode] = useState<OperationMode | ''>('');
  const [phaseForms, setPhaseForms] = useState<Record<number, PhaseForm>>({});
  const [error, setError] = useState<string | null>(null);

  const phaseNames = useMemo(
    () =>
      phasesText
        .split('\n')
        .map((line) => line.trim())
        .filter((line) => line.length > 0),
    [phasesText],
  );

  function openDialog(template: WorkflowTemplate) {
    const current = versions.find((v) => v.id === template.currentVersionId);
    setEditingTemplate(template);
    setPhasesText(current ? current.phases.join('\n') : '');
    setChangelog('');
    setDefaultMode(current?.defaultOperationMode ?? '');
    setPhaseForms(
      Object.fromEntries(
        (current?.phases ?? []).map((name, index) => [
          index,
          {
            gates: (current?.gatesByPhase[name] ?? []).join(', '),
            weight: String(current?.phaseConfigs?.[name]?.progressWeight ?? 25),
            documentKinds: current?.phaseConfigs?.[name]?.documentKinds ?? [],
            agentIds: current?.phaseConfigs?.[name]?.allowedAgentDefinitionIds ?? [],
            transitions: current?.transitions?.[name] ?? [],
          },
        ]),
      ),
    );
    setError(null);
  }

  function phaseForm(index: number): PhaseForm {
    return phaseForms[index] ?? EMPTY_PHASE_FORM;
  }

  function updatePhaseForm(index: number, patch: Partial<PhaseForm>) {
    setPhaseForms((current) => ({
      ...current,
      [index]: { ...phaseForm(index), ...patch },
    }));
  }

  function toggleInList<T>(list: T[], item: T): T[] {
    return list.includes(item) ? list.filter((entry) => entry !== item) : [...list, item];
  }

  function submit() {
    if (phaseNames.length === 0) {
      setError(t('workflows.templates.errors.phasesRequired'));
      return;
    }
    const gatesByPhase: Record<string, string[]> = {};
    const phaseConfigs: Record<string, WorkflowPhaseConfig> = {};
    const transitions: Record<string, string[]> = {};
    for (const [index, name] of phaseNames.entries()) {
      const form = phaseForm(index);
      const gates = form.gates
        .split(',')
        .map((gate) => gate.trim())
        .filter((gate) => gate.length > 0);
      if (gates.length > 0) gatesByPhase[name] = gates;
      const weight = Number(form.weight);
      if (!Number.isFinite(weight) || weight < 0 || weight > 100) {
        setError(t('workflows.templates.errors.invalidWeight', { phase: name }));
        return;
      }
      phaseConfigs[name] = {
        documentKinds: form.documentKinds,
        progressWeight: weight,
        allowedAgentDefinitionIds: form.agentIds,
      };
      if (form.transitions.length > 0) transitions[name] = form.transitions;
    }

    publish.mutate(
      {
        templateId: editingTemplate!.id,
        input: {
          phases: phaseNames,
          gatesByPhase,
          phaseConfigs,
          defaultOperationMode: defaultMode === '' ? null : defaultMode,
          transitions,
          changelog: changelog.trim() || undefined,
        },
      },
      { onSuccess: () => setEditingTemplate(null) },
    );
  }

  return (
    <section aria-labelledby="templates-title" className="flex flex-col gap-3">
      <h2 id="templates-title" className="font-heading text-lg font-semibold">
        {t('workflows.templates.title')}
      </h2>

      <ul className="flex flex-col gap-3">
        {templates.map((template) => {
          const templateVersions = versions
            .filter((version) => version.templateId === template.id)
            .sort((a, b) => b.version - a.version);
          return (
            <li
              key={template.id}
              className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-4"
            >
              <div className="flex flex-wrap items-center gap-2">
                <h3 className="text-sm font-semibold">{template.name}</h3>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="ml-auto"
                  onClick={() => openDialog(template)}
                >
                  {t('workflows.templates.newVersion')}
                </Button>
              </div>
              <p className="text-xs text-foreground-muted">{template.description}</p>
              <ul className="flex flex-col gap-2">
                {templateVersions.map((version) => (
                  <li
                    key={version.id}
                    className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted"
                  >
                    <Badge variant={version.id === template.currentVersionId ? 'brand' : 'outline'}>
                      {t('workflows.templates.version', { version: version.version })}
                    </Badge>
                    {version.id === template.currentVersionId && (
                      <span className="font-medium text-foreground">
                        {t('workflows.templates.current')}
                      </span>
                    )}
                    <span>{formatDateTime(version.publishedAt, i18n.language)}</span>
                    <span>
                      {t('workflows.templates.phaseCount', { count: version.phases.length })}
                    </span>
                    {version.changelog && <span>— {version.changelog}</span>}
                  </li>
                ))}
              </ul>
            </li>
          );
        })}
      </ul>

      {editingTemplate && (
        <ModalDialog
          label={t('workflows.templates.dialogTitle', { name: editingTemplate.name })}
          onClose={() => setEditingTemplate(null)}
          className="max-w-2xl"
        >
          <h3 className="font-heading text-lg font-semibold">
            {t('workflows.templates.dialogTitle', { name: editingTemplate.name })}
          </h3>
          <p className="text-xs text-foreground-muted">{t('workflows.templates.dialogHint')}</p>

          <Field
            htmlFor="version-phases"
            label={t('workflows.templates.phasesLabel')}
            required
            requiredLabel={t('common.requiredMark')}
            hint={t('workflows.templates.phasesHint')}
          >
            <Textarea
              id="version-phases"
              rows={4}
              value={phasesText}
              onChange={(event) => {
                setPhasesText(event.target.value);
                if (error) setError(null);
              }}
            />
          </Field>

          <Field htmlFor="version-changelog" label={t('workflows.templates.changelogLabel')}>
            <Input
              id="version-changelog"
              value={changelog}
              onChange={(event) => setChangelog(event.target.value)}
            />
          </Field>

          <Field htmlFor="version-default-mode" label={t('workflows.templates.defaultModeLabel')}>
            <Select
              id="version-default-mode"
              value={defaultMode}
              onChange={(event) => setDefaultMode(event.target.value as OperationMode | '')}
            >
              <option value="">{t('workflows.templates.noDefaultMode')}</option>
              {operationModeSchema.options.map((option) => (
                <option key={option} value={option}>
                  {t(`status.operationMode.${option}`)}
                </option>
              ))}
            </Select>
          </Field>

          {phaseNames.map((name, index) => {
            const form = phaseForm(index);
            return (
              <fieldset
                key={`${index}-${name}`}
                className="flex flex-col gap-3 rounded-lg border border-border p-3"
              >
                <legend className="px-1 text-sm font-semibold">{name}</legend>

                <Field
                  htmlFor={`phase-gates-${index}`}
                  label={t('workflows.templates.gatesLabel')}
                  hint={t('workflows.templates.gatesHint')}
                >
                  <Input
                    id={`phase-gates-${index}`}
                    value={form.gates}
                    onChange={(event) => updatePhaseForm(index, { gates: event.target.value })}
                  />
                </Field>

                <Field
                  htmlFor={`phase-weight-${index}`}
                  label={t('workflows.templates.weightLabel')}
                >
                  <Input
                    id={`phase-weight-${index}`}
                    type="number"
                    min={0}
                    max={100}
                    value={form.weight}
                    onChange={(event) => updatePhaseForm(index, { weight: event.target.value })}
                  />
                </Field>

                <div className="flex flex-col gap-1">
                  <span className="text-sm font-medium">
                    {t('workflows.templates.documentKindsLabel')}
                  </span>
                  <div className="flex flex-wrap gap-x-4 gap-y-1">
                    {documentKindSchema.options.map((kind) => (
                      <label key={kind} className="flex min-h-11 items-center gap-2 text-xs">
                        <Checkbox
                          checked={form.documentKinds.includes(kind)}
                          onChange={() =>
                            updatePhaseForm(index, {
                              documentKinds: toggleInList(form.documentKinds, kind),
                            })
                          }
                        />
                        {t(`status.documentKind.${kind}`)}
                      </label>
                    ))}
                  </div>
                </div>

                <div className="flex flex-col gap-1">
                  <span className="text-sm font-medium">
                    {t('workflows.templates.agentsLabel')}
                  </span>
                  <div className="flex flex-wrap gap-x-4 gap-y-1">
                    {agentDefinitions.map((definition) => (
                      <label key={definition.id} className="flex min-h-11 items-center gap-2 text-xs">
                        <Checkbox
                          checked={form.agentIds.includes(definition.id)}
                          onChange={() =>
                            updatePhaseForm(index, {
                              agentIds: toggleInList(form.agentIds, definition.id),
                            })
                          }
                        />
                        {definition.name}
                      </label>
                    ))}
                  </div>
                </div>

                <div className="flex flex-col gap-1">
                  <span className="text-sm font-medium">
                    {t('workflows.templates.transitionsLabel')}
                  </span>
                  <div className="flex flex-wrap gap-x-4 gap-y-1">
                    {phaseNames
                      .filter((other) => other !== name)
                      .map((other) => (
                        <label key={other} className="flex min-h-11 items-center gap-2 text-xs">
                          <Checkbox
                            checked={form.transitions.includes(other)}
                            onChange={() =>
                              updatePhaseForm(index, {
                                transitions: toggleInList(form.transitions, other),
                              })
                            }
                          />
                          {other}
                        </label>
                      ))}
                  </div>
                </div>
              </fieldset>
            );
          })}

          {error && (
            <p role="alert" className="text-xs text-error">
              {error}
            </p>
          )}

          <div className="flex flex-wrap gap-2">
            <Button type="button" disabled={publish.isPending} onClick={submit}>
              {t('workflows.templates.publish')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setEditingTemplate(null)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}
    </section>
  );
}
