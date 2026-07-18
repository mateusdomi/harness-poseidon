import { useTranslation } from 'react-i18next';

import { auditActorKindSchema } from '@/api';
import { Input, Select } from '@/design-system';
import type { AuditCatalog, AuditFilters } from '@/features/governance/lib/audit-derive';

interface AuditFiltersBarProps {
  filters: AuditFilters;
  onChange: (patch: Partial<AuditFilters>) => void;
  catalog: AuditCatalog;
}

/**
 * Barra de filtros da trilha de auditoria: busca livre, ator (tipo e
 * concreto), projeto, tarefa, tentativa, modelo, ferramenta e período.
 * Os selects são populados das listas reais do catálogo.
 */
export function AuditFiltersBar({ filters, onChange, catalog }: AuditFiltersBarProps) {
  const { t } = useTranslation();

  const taskTitle = (taskId: string) =>
    catalog.tasks.find((task) => task.id === taskId)?.title ?? taskId;

  return (
    <div className="flex flex-wrap items-end gap-3">
      <div className="flex min-w-48 flex-1 flex-col gap-1">
        <label htmlFor="governance-filter-search" className="text-xs font-medium">
          {t('governance.filters.search')}
        </label>
        <Input
          id="governance-filter-search"
          type="search"
          value={filters.search}
          placeholder={t('governance.filters.searchPlaceholder')}
          onChange={(event) => onChange({ search: event.target.value })}
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-actor-kind" className="text-xs font-medium">
          {t('governance.filters.actorKind')}
        </label>
        <Select
          id="governance-filter-actor-kind"
          value={filters.actorKind}
          onChange={(event) =>
            onChange({ actorKind: event.target.value as AuditFilters['actorKind'] })
          }
        >
          <option value="">{t('governance.filters.all')}</option>
          {auditActorKindSchema.options.map((kind) => (
            <option key={kind} value={kind}>
              {t(`status.auditActorKind.${kind}`)}
            </option>
          ))}
        </Select>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-actor" className="text-xs font-medium">
          {t('governance.filters.actor')}
        </label>
        <Select
          id="governance-filter-actor"
          value={filters.actorId}
          onChange={(event) => onChange({ actorId: event.target.value })}
        >
          <option value="">{t('governance.filters.all')}</option>
          <optgroup label={t('governance.filters.profilesGroup')}>
            {catalog.profiles.map((profile) => (
              <option key={profile.id} value={profile.id}>
                {profile.displayName}
              </option>
            ))}
          </optgroup>
          <optgroup label={t('governance.filters.agentsGroup')}>
            {catalog.agents.map((agent) => (
              <option key={agent.id} value={agent.id}>
                {agent.name}
              </option>
            ))}
          </optgroup>
        </Select>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-project" className="text-xs font-medium">
          {t('governance.filters.project')}
        </label>
        <Select
          id="governance-filter-project"
          value={filters.projectId}
          onChange={(event) => onChange({ projectId: event.target.value })}
        >
          <option value="">{t('governance.filters.all')}</option>
          {catalog.projects.map((project) => (
            <option key={project.id} value={project.id}>
              {project.name}
            </option>
          ))}
        </Select>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-task" className="text-xs font-medium">
          {t('governance.filters.task')}
        </label>
        <Select
          id="governance-filter-task"
          value={filters.taskId}
          onChange={(event) => onChange({ taskId: event.target.value })}
        >
          <option value="">{t('governance.filters.allFem')}</option>
          {catalog.tasks.map((task) => (
            <option key={task.id} value={task.id}>
              {task.title}
            </option>
          ))}
        </Select>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-attempt" className="text-xs font-medium">
          {t('governance.filters.attempt')}
        </label>
        <Select
          id="governance-filter-attempt"
          value={filters.attemptId}
          onChange={(event) => onChange({ attemptId: event.target.value })}
        >
          <option value="">{t('governance.filters.allFem')}</option>
          {catalog.attempts.map((attempt) => (
            <option key={attempt.id} value={attempt.id}>
              {t('governance.filters.attemptOption', {
                number: attempt.number,
                task: taskTitle(attempt.taskId),
              })}
            </option>
          ))}
        </Select>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-model" className="text-xs font-medium">
          {t('governance.filters.model')}
        </label>
        <Select
          id="governance-filter-model"
          value={filters.modelId}
          onChange={(event) => onChange({ modelId: event.target.value })}
        >
          <option value="">{t('governance.filters.all')}</option>
          {catalog.models.map((model) => (
            <option key={model.id} value={model.id}>
              {model.displayName}
            </option>
          ))}
        </Select>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-tool" className="text-xs font-medium">
          {t('governance.filters.tool')}
        </label>
        <Select
          id="governance-filter-tool"
          value={filters.toolId}
          onChange={(event) => onChange({ toolId: event.target.value })}
        >
          <option value="">{t('governance.filters.allFem')}</option>
          {catalog.tools.map((tool) => (
            <option key={tool.id} value={tool.id}>
              {tool.name}
            </option>
          ))}
        </Select>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-date-from" className="text-xs font-medium">
          {t('governance.filters.dateFrom')}
        </label>
        <Input
          id="governance-filter-date-from"
          type="date"
          value={filters.dateFrom}
          onChange={(event) => onChange({ dateFrom: event.target.value })}
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="governance-filter-date-to" className="text-xs font-medium">
          {t('governance.filters.dateTo')}
        </label>
        <Input
          id="governance-filter-date-to"
          type="date"
          value={filters.dateTo}
          onChange={(event) => onChange({ dateTo: event.target.value })}
        />
      </div>
    </div>
  );
}
