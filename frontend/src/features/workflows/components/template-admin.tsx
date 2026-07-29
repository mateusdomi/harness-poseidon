import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  validateWorkflowVersionContent,
  type AgentDefinition,
  type Skill,
  type Tool,
  type Ulid,
  type Workflow,
  type WorkflowTemplate,
  type WorkflowValidationIssue,
  type WorkflowVersion,
} from '@/api';
import { Badge, Button, Checkbox, Field, Input } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import { workflowContentStateVariant } from '@/lib/status';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { VersionCompare } from '@/features/workflows/components/version-compare';
import { VersionDraftEditor } from '@/features/workflows/components/version-draft-editor';
import {
  useArchiveWorkflowTemplate,
  useArchiveWorkflowVersion,
  useCreateWorkflowDraftVersion,
  useCreateWorkflowTemplate,
  useDeleteWorkflowDraftVersion,
  useDeleteWorkflowTemplate,
  useDuplicateWorkflowTemplate,
  useDuplicateWorkflowVersion,
  useLinkWorkflowTemplate,
  usePublishWorkflowDraft,
} from '@/features/workflows/hooks/use-workflows';

export interface TemplateAdminProps {
  templates: WorkflowTemplate[];
  versions: WorkflowVersion[];
  agentDefinitions: AgentDefinition[];
  skills: Skill[];
  tools: Tool[];
  /** Templates vinculados a algum workflow (nunca excluíveis — tombstone). */
  usedTemplateIds: Set<Ulid>;
  /** Versões em uso (workflow ativo ou run) — nunca excluíveis. */
  usedVersionIds: Set<Ulid>;
  /** Projeto ativo — destino do vínculo de template. */
  activeProjectId: Ulid | null;
  /** Workflow já vinculado ao projeto ativo (bloqueia novo vínculo). */
  activeProjectWorkflow: Workflow | null;
  /** Versão em uso pela execução ativa do projeto (banner de impacto). */
  activeRunVersion: WorkflowVersion | null;
}

/**
 * Gestão completa de templates de workflow (FR-4): criar template do zero,
 * rascunhos editáveis (editor de fases), publicação com validação do
 * Harness (congela — imutável), duplicar template/versão, arquivar
 * (tombstone — nunca exclusão física de versão utilizada), excluir apenas
 * rascunho nunca utilizado (com confirmação), vincular template ao projeto
 * ativo e comparar versões (diff estrutural).
 */
export function TemplateAdmin({
  templates,
  versions,
  agentDefinitions,
  skills,
  tools,
  usedTemplateIds,
  usedVersionIds,
  activeProjectId,
  activeProjectWorkflow,
  activeRunVersion,
}: TemplateAdminProps) {
  const { t, i18n } = useTranslation();

  const createTemplate = useCreateWorkflowTemplate();
  const createDraft = useCreateWorkflowDraftVersion();
  const publishDraft = usePublishWorkflowDraft();
  const archiveTemplate = useArchiveWorkflowTemplate();
  const archiveVersion = useArchiveWorkflowVersion();
  const deleteTemplate = useDeleteWorkflowTemplate();
  const deleteVersion = useDeleteWorkflowDraftVersion();
  const duplicateTemplate = useDuplicateWorkflowTemplate();
  const duplicateVersion = useDuplicateWorkflowVersion();
  const linkTemplate = useLinkWorkflowTemplate();

  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [editing, setEditing] = useState<{
    template: WorkflowTemplate;
    draft: WorkflowVersion;
  } | null>(null);
  const [deletingTemplate, setDeletingTemplate] = useState<WorkflowTemplate | null>(null);
  const [deletingVersion, setDeletingVersion] = useState<WorkflowVersion | null>(null);
  const [compareIds, setCompareIds] = useState<Ulid[]>([]);
  const [comparing, setComparing] = useState<{ from: WorkflowVersion; to: WorkflowVersion } | null>(
    null,
  );
  const [publishIssues, setPublishIssues] = useState<{
    versionId: Ulid;
    issues: WorkflowValidationIssue[];
  } | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  function versionsOf(template: WorkflowTemplate): WorkflowVersion[] {
    return versions
      .filter((version) => version.templateId === template.id)
      .sort((a, b) => b.version - a.version);
  }

  /** Template excluível: rascunho, nunca vinculado, sem versões publicadas. */
  function canDeleteTemplate(template: WorkflowTemplate): boolean {
    if (usedTemplateIds.has(template.id)) return false;
    return versionsOf(template).every((version) => version.state === 'draft');
  }

  function canDeleteVersion(version: WorkflowVersion): boolean {
    return version.state === 'draft' && !usedVersionIds.has(version.id);
  }

  function canLink(template: WorkflowTemplate): boolean {
    return (
      activeProjectId !== null &&
      activeProjectWorkflow === null &&
      template.currentVersionId !== null &&
      template.state !== 'archived'
    );
  }

  async function openNewDraft(template: WorkflowTemplate) {
    setActionError(null);
    try {
      const draft = await createDraft.mutateAsync({ templateId: template.id });
      setEditing({ template, draft });
    } catch (error) {
      setActionError(error instanceof Error ? error.message : String(error));
    }
  }

  function publishFromCard(version: WorkflowVersion) {
    const issues = validateWorkflowVersionContent(version);
    if (issues.length > 0) {
      setPublishIssues({ versionId: version.id, issues });
      return;
    }
    setPublishIssues(null);
    setActionError(null);
    publishDraft.mutate(
      { versionId: version.id, input: {} },
      {
        onError: (error) => setActionError(error.message),
      },
    );
  }

  function toggleCompare(id: Ulid) {
    setCompareIds((current) =>
      current.includes(id) ? current.filter((entry) => entry !== id) : [...current, id].slice(-2),
    );
  }

  function openCompare() {
    if (compareIds.length !== 2) return;
    const [a, b] = compareIds
      .map((id) => versions.find((version) => version.id === id))
      .filter((version): version is WorkflowVersion => version !== undefined)
      .sort((x, y) => x.version - y.version);
    if (a && b) setComparing({ from: a, to: b });
  }

  function submitCreate() {
    if (newName.trim() === '') return;
    createTemplate.mutate(
      { name: newName.trim(), description: newDescription.trim() || undefined },
      {
        onSuccess: () => {
          setCreating(false);
          setNewName('');
          setNewDescription('');
        },
        onError: (error) => setActionError(error.message),
      },
    );
  }

  return (
    <section
      aria-labelledby="templates-title"
      className="min-w-0 rounded-xl border border-border bg-surface p-4 md:p-6"
    >
      <div className="flex flex-col gap-4">
        <div className="flex flex-wrap items-start gap-3">
          <div className="min-w-0 flex-1">
            <h2 id="templates-title" className="font-heading text-lg font-semibold">
              {t('workflows.templates.title')}
            </h2>
            <p className="mt-1 max-w-3xl text-sm text-foreground-muted">
              {t('workflows.templates.description')}
            </p>
          </div>
          <Button
            type="button"
            size="sm"
            onClick={() => {
              setCreating(true);
              setActionError(null);
            }}
          >
            {t('workflows.templates.create')}
          </Button>
        </div>

        {actionError && (
          <p role="alert" className="text-xs text-error">
            {actionError}
          </p>
        )}

        <div className="flex flex-wrap items-center gap-2 rounded-lg bg-surface-elevated p-3">
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={compareIds.length !== 2}
            onClick={openCompare}
          >
            {t('workflows.templates.compare')}
          </Button>
          {compareIds.length !== 2 && (
            <span className="text-xs text-foreground-muted">
              {t('workflows.templates.compareHint')}
            </span>
          )}
        </div>

        <ul className="flex min-w-0 flex-col gap-4">
          {templates.map((template) => {
            const templateVersions = versionsOf(template);
            const deletable = canDeleteTemplate(template);
            return (
              <li
                key={template.id}
                className="flex min-w-0 flex-col gap-4 rounded-xl border border-border bg-background p-4"
              >
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="text-sm font-semibold">{template.name}</h3>
                  <Badge variant={workflowContentStateVariant(template.state)}>
                    {t(`status.workflowContentState.${template.state}`)}
                  </Badge>
                  {template.archivedAt && (
                    <span className="text-xs text-foreground-muted">
                      {t('workflows.templates.archivedAt', {
                        date: formatDateTime(template.archivedAt, i18n.language),
                      })}
                    </span>
                  )}
                </div>
                <p className="text-xs text-foreground-muted">{template.description}</p>

                <div
                  className="flex flex-wrap gap-2"
                  aria-label={t('workflows.templates.templateActions', { name: template.name })}
                >
                  {template.state !== 'archived' && (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      disabled={createDraft.isPending}
                      onClick={() => void openNewDraft(template)}
                    >
                      {t('workflows.templates.newDraft')}
                    </Button>
                  )}
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={duplicateTemplate.isPending}
                    onClick={() => duplicateTemplate.mutate(template.id)}
                  >
                    {t('workflows.templates.duplicate')}
                  </Button>
                  {template.state !== 'archived' && (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      disabled={archiveTemplate.isPending}
                      onClick={() => archiveTemplate.mutate(template.id)}
                    >
                      {t('workflows.templates.archive')}
                    </Button>
                  )}
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={!deletable}
                    title={deletable ? undefined : t('workflows.templates.deleteBlocked')}
                    onClick={() => setDeletingTemplate(template)}
                  >
                    {t('workflows.templates.delete')}
                  </Button>
                  {activeProjectId !== null && (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      disabled={!canLink(template) || linkTemplate.isPending}
                      title={
                        canLink(template)
                          ? undefined
                          : activeProjectWorkflow !== null
                            ? t('workflows.templates.linkBlockedInUse')
                            : t('workflows.templates.linkBlockedNoPublished')
                      }
                      onClick={() =>
                        linkTemplate.mutate(
                          { projectId: activeProjectId, templateId: template.id },
                          { onError: (error) => setActionError(error.message) },
                        )
                      }
                    >
                      {t('workflows.templates.link')}
                    </Button>
                  )}
                </div>

                <ul className="flex min-w-0 flex-col gap-3">
                  {templateVersions.map((version) => {
                    const versionDeletable = canDeleteVersion(version);
                    const isCurrent = version.id === template.currentVersionId;
                    return (
                      <li
                        key={version.id}
                        className="flex min-w-0 flex-col gap-3 rounded-lg border border-border bg-surface p-3"
                      >
                        <div className="flex min-w-0 flex-col gap-3 md:flex-row md:items-start md:justify-between">
                          <div className="flex min-w-0 flex-wrap items-center gap-2 text-xs text-foreground-muted">
                            <Checkbox
                              aria-label={t('workflows.templates.compareSelect', {
                                version: version.version,
                              })}
                              checked={compareIds.includes(version.id)}
                              onChange={() => toggleCompare(version.id)}
                            />
                            <Badge variant={isCurrent ? 'brand' : 'outline'}>
                              {t('workflows.templates.version', { version: version.version })}
                            </Badge>
                            <Badge variant={workflowContentStateVariant(version.state)}>
                              {t(`status.workflowContentState.${version.state}`)}
                            </Badge>
                            {isCurrent && (
                              <span className="font-medium text-foreground">
                                {t('workflows.templates.current')}
                              </span>
                            )}
                            <span>
                              {version.publishedAt
                                ? formatDateTime(version.publishedAt, i18n.language)
                                : t('workflows.templates.notPublished')}
                            </span>
                            <span>
                              {t('workflows.templates.phaseCount', {
                                count: version.phases.length,
                              })}
                            </span>
                            {version.changelog && (
                              <span className="min-w-0 break-words">— {version.changelog}</span>
                            )}
                          </div>
                          <div
                            className="flex shrink-0 flex-wrap gap-2"
                            aria-label={t('workflows.templates.versionActions', {
                              version: version.version,
                            })}
                          >
                            {version.state === 'draft' && (
                              <>
                                <Button
                                  type="button"
                                  variant="outline"
                                  size="sm"
                                  onClick={() => setEditing({ template, draft: version })}
                                >
                                  {t('workflows.templates.editDraft')}
                                </Button>
                                <Button
                                  type="button"
                                  variant="outline"
                                  size="sm"
                                  disabled={publishDraft.isPending}
                                  onClick={() => publishFromCard(version)}
                                >
                                  {t('workflows.templates.publish')}
                                </Button>
                              </>
                            )}
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              disabled={duplicateVersion.isPending}
                              onClick={() => duplicateVersion.mutate(version.id)}
                            >
                              {t('workflows.templates.duplicate')}
                            </Button>
                            {version.state !== 'archived' && !isCurrent && (
                              <Button
                                type="button"
                                variant="outline"
                                size="sm"
                                disabled={archiveVersion.isPending}
                                onClick={() => archiveVersion.mutate(version.id)}
                              >
                                {t('workflows.templates.archive')}
                              </Button>
                            )}
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              disabled={!versionDeletable}
                              title={
                                versionDeletable
                                  ? undefined
                                  : version.state === 'draft'
                                    ? t('workflows.templates.deleteVersionInUse')
                                    : t('workflows.templates.deletePublishedBlocked')
                              }
                              onClick={() => setDeletingVersion(version)}
                            >
                              {t('workflows.templates.delete')}
                            </Button>
                          </div>
                        </div>

                        {version.phases.length > 0 && (
                          <ol
                            aria-label={t('workflows.templates.phasesListLabel')}
                            className="flex flex-wrap items-center gap-1"
                          >
                            {version.phases.map((phase, index) => (
                              <li key={`${phase}-${index}`}>
                                <Badge variant="outline">{phase}</Badge>
                              </li>
                            ))}
                          </ol>
                        )}

                        {publishIssues?.versionId === version.id && (
                          <div role="alert" className="rounded-md border border-error p-3">
                            <p className="text-xs font-medium text-error">
                              {t('workflows.templates.validationTitle')}
                            </p>
                            <ul className="mt-1 list-inside list-disc text-xs text-error">
                              {publishIssues.issues.map((issue, index) => (
                                <li key={`${issue.key}-${index}`}>{t(issue.key, issue.params)}</li>
                              ))}
                            </ul>
                          </div>
                        )}
                      </li>
                    );
                  })}
                </ul>
              </li>
            );
          })}
        </ul>
      </div>

      {creating && (
        <ModalDialog
          label={t('workflows.templates.createDialog.title')}
          onClose={() => setCreating(false)}
        >
          <h3 className="font-heading text-lg font-semibold">
            {t('workflows.templates.createDialog.title')}
          </h3>
          <Field
            htmlFor="template-name"
            label={t('workflows.templates.createDialog.nameLabel')}
            required
            requiredLabel={t('common.requiredMark')}
          >
            <Input
              id="template-name"
              value={newName}
              onChange={(event) => setNewName(event.target.value)}
            />
          </Field>
          <Field
            htmlFor="template-description"
            label={t('workflows.templates.createDialog.descriptionLabel')}
          >
            <Input
              id="template-description"
              value={newDescription}
              onChange={(event) => setNewDescription(event.target.value)}
            />
          </Field>
          <div className="flex flex-wrap gap-2">
            <Button type="button" disabled={createTemplate.isPending} onClick={submitCreate}>
              {t('workflows.templates.createDialog.confirm')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setCreating(false)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}

      {deletingTemplate && (
        <ModalDialog
          label={t('workflows.templates.deleteDialog.title')}
          onClose={() => setDeletingTemplate(null)}
        >
          <h3 className="font-heading text-lg font-semibold">
            {t('workflows.templates.deleteDialog.title')}
          </h3>
          <p className="text-sm text-foreground-muted">
            {t('workflows.templates.deleteDialog.body', { name: deletingTemplate.name })}
          </p>
          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              disabled={deleteTemplate.isPending}
              onClick={() =>
                deleteTemplate.mutate(deletingTemplate.id, {
                  onSuccess: () => setDeletingTemplate(null),
                  onError: (error) => {
                    setActionError(error.message);
                    setDeletingTemplate(null);
                  },
                })
              }
            >
              {t('workflows.templates.deleteDialog.confirm')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setDeletingTemplate(null)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}

      {deletingVersion && (
        <ModalDialog
          label={t('workflows.templates.deleteVersionDialog.title')}
          onClose={() => setDeletingVersion(null)}
        >
          <h3 className="font-heading text-lg font-semibold">
            {t('workflows.templates.deleteVersionDialog.title')}
          </h3>
          <p className="text-sm text-foreground-muted">
            {t('workflows.templates.deleteVersionDialog.body', {
              version: deletingVersion.version,
            })}
          </p>
          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              disabled={deleteVersion.isPending}
              onClick={() =>
                deleteVersion.mutate(deletingVersion.id, {
                  onSuccess: () => setDeletingVersion(null),
                  onError: (error) => {
                    setActionError(error.message);
                    setDeletingVersion(null);
                  },
                })
              }
            >
              {t('workflows.templates.deleteVersionDialog.confirm')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setDeletingVersion(null)}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </ModalDialog>
      )}

      {editing && (
        <VersionDraftEditor
          template={editing.template}
          draft={editing.draft}
          agentDefinitions={agentDefinitions}
          skills={skills}
          tools={tools}
          activeRunVersion={
            activeProjectWorkflow?.templateId === editing.template.id
              ? (activeRunVersion?.version ?? null)
              : null
          }
          onClose={() => setEditing(null)}
        />
      )}

      {comparing && (
        <VersionCompare
          from={comparing.from}
          to={comparing.to}
          fromTemplate={templates.find((tpl) => tpl.id === comparing.from.templateId) ?? null}
          toTemplate={templates.find((tpl) => tpl.id === comparing.to.templateId) ?? null}
          onClose={() => setComparing(null)}
        />
      )}
    </section>
  );
}
