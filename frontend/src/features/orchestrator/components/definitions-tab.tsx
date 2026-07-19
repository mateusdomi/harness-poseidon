import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';

import type { Account, AgentDefinition, Model, Ulid } from '@/api';
import { Badge, Button, Field, Select, Skeleton } from '@/design-system';
import { DefinitionDetailsDialog } from '@/features/orchestrator/components/definition-details';
import { DefinitionFormDialog } from '@/features/orchestrator/components/definition-form';
import {
  definitionState,
  STATE_BADGE_VARIANT,
  valuesToInput,
  type DefinitionFormValues,
} from '@/features/orchestrator/lib/definitions-form';
import {
  useArchiveDefinition,
  useCreateDefinition,
  useDefinitionsData,
  useDeleteDefinition,
  useDisableDefinition,
  useDuplicateDefinition,
  useEnableDefinition,
  useUpdateDefinition,
} from '@/features/orchestrator/hooks/use-definitions';
import { ModalDialog } from '@/features/shared/components/modal-dialog';

/** Provider da definição: via modelo padrão; sem modelo, via conta preferencial. */
function resolveDefinitionProviderId(
  definition: AgentDefinition,
  models: Model[],
  accounts: Account[],
): Ulid | null {
  const model = models.find((entry) => entry.id === definition.defaultModelId);
  if (model) return model.providerId;
  const account = accounts.find((entry) => entry.id === definition.preferredAccountId);
  return account?.providerId ?? null;
}

const ALL = 'all';
const NO_TEAM = 'none';

/**
 * Aba "Definições de agentes" (/orchestrator?tab=definitions): catálogo de
 * TODAS as definições com filtros client-side (time, stack, papel, provider,
 * modelo, skill e status) e o CRUD completo (visualizar, criar, editar,
 * duplicar, habilitar/desabilitar, arquivar e excluir quando sem instâncias).
 * Deep-link `&definition=<id>` abre a visualização da definição.
 */
export function DefinitionsTab() {
  const { t } = useTranslation();
  const data = useDefinitionsData();
  const [searchParams, setSearchParams] = useSearchParams();

  const [teamFilter, setTeamFilter] = useState(ALL);
  const [stackFilter, setStackFilter] = useState(ALL);
  const [roleFilter, setRoleFilter] = useState(ALL);
  const [providerFilter, setProviderFilter] = useState(ALL);
  const [modelFilter, setModelFilter] = useState(ALL);
  const [skillFilter, setSkillFilter] = useState(ALL);
  const [statusFilter, setStatusFilter] = useState(ALL);

  // Modais: visualização (local ou deep-link), formulário, confirmações.
  const [viewingId, setViewingId] = useState<Ulid | null>(null);
  const [editing, setEditing] = useState<AgentDefinition | null>(null);
  const [creating, setCreating] = useState(false);
  const [archiving, setArchiving] = useState<AgentDefinition | null>(null);
  const [deleting, setDeleting] = useState<AgentDefinition | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [confirmError, setConfirmError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const createMutation = useCreateDefinition();
  const updateMutation = useUpdateDefinition();
  const duplicateMutation = useDuplicateDefinition();
  const enableMutation = useEnableDefinition();
  const disableMutation = useDisableDefinition();
  const archiveMutation = useArchiveDefinition();
  const deleteMutation = useDeleteDefinition();

  const deepLinkId = searchParams.get('definition');
  const openId = viewingId ?? deepLinkId;
  const viewing = data.definitions.find((definition) => definition.id === openId) ?? null;

  function closeViewing() {
    setViewingId(null);
    if (deepLinkId) {
      const next = new URLSearchParams(searchParams);
      next.delete('definition');
      setSearchParams(next, { replace: true });
    }
  }

  const usedDefinitionIds = useMemo(
    () => new Set(data.agents.map((agent) => agent.definitionId)),
    [data.agents],
  );

  const teams = useMemo(
    () =>
      [...new Set(data.definitions.map((definition) => definition.team).filter(Boolean))]
        .sort() as string[],
    [data.definitions],
  );
  const stacks = useMemo(
    () =>
      [...new Set(data.definitions.flatMap((definition) => definition.stacks ?? []))].sort(),
    [data.definitions],
  );

  const filtered = data.definitions.filter((definition) => {
    if (teamFilter === NO_TEAM && definition.team) return false;
    if (teamFilter !== ALL && teamFilter !== NO_TEAM && definition.team !== teamFilter) {
      return false;
    }
    if (stackFilter !== ALL && !(definition.stacks ?? []).includes(stackFilter)) return false;
    if (roleFilter !== ALL && definition.role !== roleFilter) return false;
    if (
      providerFilter !== ALL &&
      resolveDefinitionProviderId(definition, data.models, data.accounts) !== providerFilter
    ) {
      return false;
    }
    if (modelFilter !== ALL && definition.defaultModelId !== modelFilter) return false;
    if (skillFilter !== ALL && !definition.skillIds.includes(skillFilter)) return false;
    if (statusFilter !== ALL && definitionState(definition) !== statusFilter) return false;
    return true;
  });

  function submitForm(values: DefinitionFormValues) {
    setFormError(null);
    const input = valuesToInput(values);
    const onError = (error: Error) => setFormError(error.message);
    if (editing) {
      updateMutation.mutate(
        { id: editing.id, input: { ...input, expectedVersion: editing.version ?? 0 } },
        { onSuccess: () => setEditing(null), onError },
      );
    } else {
      createMutation.mutate(input, { onSuccess: () => setCreating(false), onError });
    }
  }

  function runAction(
    mutation: { mutate: (id: Ulid, options: { onError: (error: Error) => void }) => void },
    id: Ulid,
  ) {
    setActionError(null);
    mutation.mutate(id, { onError: (error) => setActionError(error.message) });
  }

  function confirmArchive() {
    if (!archiving) return;
    setConfirmError(null);
    archiveMutation.mutate(archiving.id, {
      onSuccess: () => setArchiving(null),
      onError: (error) => setConfirmError(error.message),
    });
  }

  function confirmDelete() {
    if (!deleting) return;
    setConfirmError(null);
    deleteMutation.mutate(deleting.id, {
      onSuccess: () => setDeleting(null),
      onError: (error) => setConfirmError(error.message),
    });
  }

  if (data.isPending) {
    return (
      <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-28 w-full" />
        <Skeleton className="h-28 w-full" />
      </div>
    );
  }

  if (data.isError) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" onClick={data.refetch}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  return (
    <section aria-labelledby="definitions-title" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="definitions-title" className="font-heading text-lg font-semibold">
          {t('orchestrator.definitions.title')}
        </h2>
        <Button
          type="button"
          onClick={() => {
            setFormError(null);
            setCreating(true);
          }}
        >
          {t('orchestrator.definitions.new')}
        </Button>
      </div>
      <p className="text-sm text-foreground-muted">{t('orchestrator.definitions.help')}</p>

      {actionError ? (
        <p role="alert" className="rounded-md border border-error bg-surface-elevated p-3 text-sm text-error">
          {actionError}
        </p>
      ) : null}

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Field htmlFor="definitions-filter-team" label={t('orchestrator.definitions.filters.team')}>
          <Select
            id="definitions-filter-team"
            value={teamFilter}
            onChange={(event) => setTeamFilter(event.target.value)}
          >
            <option value={ALL}>{t('orchestrator.definitions.filters.all')}</option>
            <option value={NO_TEAM}>{t('orchestrator.definitions.filters.noTeam')}</option>
            {teams.map((team) => (
              <option key={team} value={team}>
                {team}
              </option>
            ))}
          </Select>
        </Field>
        <Field htmlFor="definitions-filter-stack" label={t('orchestrator.definitions.filters.stack')}>
          <Select
            id="definitions-filter-stack"
            value={stackFilter}
            onChange={(event) => setStackFilter(event.target.value)}
          >
            <option value={ALL}>{t('orchestrator.definitions.filters.all')}</option>
            {stacks.map((stack) => (
              <option key={stack} value={stack}>
                {stack}
              </option>
            ))}
          </Select>
        </Field>
        <Field htmlFor="definitions-filter-role" label={t('orchestrator.definitions.filters.role')}>
          <Select
            id="definitions-filter-role"
            value={roleFilter}
            onChange={(event) => setRoleFilter(event.target.value)}
          >
            <option value={ALL}>{t('orchestrator.definitions.filters.all')}</option>
            <option value="chief">{t('orchestrator.definitions.role.chief')}</option>
            <option value="specialist">{t('orchestrator.definitions.role.specialist')}</option>
          </Select>
        </Field>
        <Field
          htmlFor="definitions-filter-provider"
          label={t('orchestrator.definitions.filters.provider')}
        >
          <Select
            id="definitions-filter-provider"
            value={providerFilter}
            onChange={(event) => setProviderFilter(event.target.value)}
          >
            <option value={ALL}>{t('orchestrator.definitions.filters.all')}</option>
            {data.providers.map((provider) => (
              <option key={provider.id} value={provider.id}>
                {provider.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field htmlFor="definitions-filter-model" label={t('orchestrator.definitions.filters.model')}>
          <Select
            id="definitions-filter-model"
            value={modelFilter}
            onChange={(event) => setModelFilter(event.target.value)}
          >
            <option value={ALL}>{t('orchestrator.definitions.filters.all')}</option>
            {data.models.map((model) => (
              <option key={model.id} value={model.id}>
                {model.displayName}
              </option>
            ))}
          </Select>
        </Field>
        <Field htmlFor="definitions-filter-skill" label={t('orchestrator.definitions.filters.skill')}>
          <Select
            id="definitions-filter-skill"
            value={skillFilter}
            onChange={(event) => setSkillFilter(event.target.value)}
          >
            <option value={ALL}>{t('orchestrator.definitions.filters.all')}</option>
            {data.skills.map((skill) => (
              <option key={skill.id} value={skill.id}>
                {skill.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field
          htmlFor="definitions-filter-status"
          label={t('orchestrator.definitions.filters.status')}
        >
          <Select
            id="definitions-filter-status"
            value={statusFilter}
            onChange={(event) => setStatusFilter(event.target.value)}
          >
            <option value={ALL}>{t('orchestrator.definitions.filters.all')}</option>
            <option value="enabled">{t('orchestrator.definitions.state.enabled')}</option>
            <option value="disabled">{t('orchestrator.definitions.state.disabled')}</option>
            <option value="archived">{t('orchestrator.definitions.state.archived')}</option>
          </Select>
        </Field>
      </div>

      {filtered.length === 0 ? (
        <p className="text-sm text-foreground-muted">{t('orchestrator.definitions.empty')}</p>
      ) : (
        <ul className="flex flex-col gap-3">
          {filtered.map((definition) => {
            const state = definitionState(definition);
            const model = data.models.find((entry) => entry.id === definition.defaultModelId);
            const providerId = resolveDefinitionProviderId(definition, data.models, data.accounts);
            const provider = data.providers.find((entry) => entry.id === providerId);
            const inUse = usedDefinitionIds.has(definition.id);
            return (
              <li
                key={definition.id}
                className="flex flex-col gap-3 rounded-lg border border-border bg-surface-elevated p-4"
              >
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{definition.name}</span>
                  <Badge variant="outline">
                    {t(`orchestrator.definitions.role.${definition.role}`)}
                  </Badge>
                  <Badge variant={STATE_BADGE_VARIANT[state]}>
                    {t(`orchestrator.definitions.state.${state}`)}
                  </Badge>
                  <Badge variant="outline">
                    {t('orchestrator.definitions.versionShort', {
                      version: definition.version ?? 1,
                    })}
                  </Badge>
                </div>
                <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm text-foreground-muted">
                  <div className="flex gap-1">
                    <dt>{t('orchestrator.definitions.list.team')}</dt>
                    <dd>{definition.team ?? t('orchestrator.definitions.filters.noTeam')}</dd>
                  </div>
                  <div className="flex gap-1">
                    <dt>{t('orchestrator.definitions.list.model')}</dt>
                    <dd>{model?.displayName ?? t('orchestrator.definitions.list.noModel')}</dd>
                  </div>
                  <div className="flex gap-1">
                    <dt>{t('orchestrator.definitions.list.provider')}</dt>
                    <dd>{provider?.name ?? t('orchestrator.definitions.list.noProvider')}</dd>
                  </div>
                </dl>
                <div className="flex flex-wrap gap-2">
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => setViewingId(definition.id)}
                  >
                    {t('orchestrator.definitions.actions.view')}
                  </Button>
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => {
                      setFormError(null);
                      setEditing(definition);
                    }}
                  >
                    {t('orchestrator.definitions.actions.edit')}
                  </Button>
                  <Button
                    type="button"
                    variant="outline"
                    disabled={duplicateMutation.isPending}
                    onClick={() => {
                      setActionError(null);
                      duplicateMutation.mutate(definition, {
                        onError: (error) => setActionError(error.message),
                      });
                    }}
                  >
                    {t('orchestrator.definitions.actions.duplicate')}
                  </Button>
                  {state === 'enabled' ? (
                    <Button
                      type="button"
                      variant="outline"
                      disabled={disableMutation.isPending}
                      onClick={() => runAction(disableMutation, definition.id)}
                    >
                      {t('orchestrator.definitions.actions.disable')}
                    </Button>
                  ) : (
                    <Button
                      type="button"
                      variant="outline"
                      disabled={enableMutation.isPending}
                      onClick={() => runAction(enableMutation, definition.id)}
                    >
                      {t('orchestrator.definitions.actions.enable')}
                    </Button>
                  )}
                  {state !== 'archived' ? (
                    <Button
                      type="button"
                      variant="outline"
                      onClick={() => {
                        setConfirmError(null);
                        setArchiving(definition);
                      }}
                    >
                      {t('orchestrator.definitions.actions.archive')}
                    </Button>
                  ) : null}
                  <Button
                    type="button"
                    variant="destructive"
                    disabled={inUse}
                    title={inUse ? t('orchestrator.definitions.list.inUse') : undefined}
                    onClick={() => {
                      setConfirmError(null);
                      setDeleting(definition);
                    }}
                  >
                    {t('orchestrator.definitions.actions.delete')}
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      )}

      {viewing ? (
        <DefinitionDetailsDialog
          definition={viewing}
          skills={data.skills}
          tools={data.tools}
          models={data.models}
          accounts={data.accounts}
          providers={data.providers}
          onClose={closeViewing}
        />
      ) : null}

      {creating || editing ? (
        <DefinitionFormDialog
          initial={editing ?? undefined}
          skills={data.skills}
          tools={data.tools}
          models={data.models}
          accounts={data.accounts}
          providers={data.providers}
          submitting={createMutation.isPending || updateMutation.isPending}
          apiError={formError}
          onSubmit={submitForm}
          onClose={() => {
            setCreating(false);
            setEditing(null);
          }}
        />
      ) : null}

      {archiving ? (
        <ModalDialog
          label={t('orchestrator.definitions.archive.title')}
          onClose={() => setArchiving(null)}
        >
          <h3 className="font-heading text-lg font-semibold">
            {t('orchestrator.definitions.archive.title')}
          </h3>
          <p className="text-sm text-foreground-muted">
            {t('orchestrator.definitions.archive.body', { name: archiving.name })}
          </p>
          {confirmError ? (
            <p role="alert" className="text-sm text-error">
              {confirmError}
            </p>
          ) : null}
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => setArchiving(null)}>
              {t('common.actions.cancel')}
            </Button>
            <Button
              type="button"
              disabled={archiveMutation.isPending}
              onClick={confirmArchive}
            >
              {t('orchestrator.definitions.archive.confirm')}
            </Button>
          </div>
        </ModalDialog>
      ) : null}

      {deleting ? (
        <ModalDialog
          label={t('orchestrator.definitions.delete.title')}
          onClose={() => setDeleting(null)}
        >
          <h3 className="font-heading text-lg font-semibold">
            {t('orchestrator.definitions.delete.title')}
          </h3>
          <p className="text-sm text-foreground-muted">
            {t('orchestrator.definitions.delete.body', { name: deleting.name })}
          </p>
          {confirmError ? (
            <p role="alert" className="text-sm text-error">
              {confirmError}
            </p>
          ) : null}
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => setDeleting(null)}>
              {t('common.actions.cancel')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={deleteMutation.isPending}
              onClick={confirmDelete}
            >
              {t('orchestrator.definitions.delete.confirm')}
            </Button>
          </div>
        </ModalDialog>
      ) : null}
    </section>
  );
}
