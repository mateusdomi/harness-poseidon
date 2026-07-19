import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Archive, ArchiveRestore, MessageSquare, Pencil } from 'lucide-react';

import type { Conversation } from '@/api';
import { Badge, Button, Card, CardContent, Checkbox, Input, Select, Skeleton } from '@/design-system';
import { conversationStateVariant } from '@/lib/status';
import { formatDateTime, formatRelativeTime } from '@/lib/format';
import { RenameConversationDialog } from '@/features/conversations/components/rename-conversation-dialog';
import { useConversations, useUpdateConversation } from '@/features/conversations/hooks/use-conversations';
import {
  EMPTY_CONVERSATION_FILTERS,
  filterConversations,
  sortByRecentActivity,
  type PeriodFilter,
} from '@/features/conversations/lib/conversations-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useProfiles } from '@/features/shared/hooks/use-profiles';
import { PaginationBar } from '@/features/shared/components/pagination';
import { usePagination } from '@/features/shared/hooks/use-pagination';

const PERIODS: PeriodFilter[] = ['', 'today', '7d', '30d', 'custom'];

/**
 * Histórico de conversas: filtros por projeto, período, canal (indisponível
 * no contrato — D-038) e autor, busca textual, renomear, arquivar
 * (≠ excluir) e abertura retomando o contexto no chat (`?conversation=<id>`).
 */
export default function UconversationsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const conversationsQuery = useConversations();
  const profilesQuery = useProfiles();
  const updateConversation = useUpdateConversation();

  const [filters, setFilters] = useState(() => ({
    ...EMPTY_CONVERSATION_FILTERS,
    projectId: activeProject?.id ?? '',
  }));
  const [renaming, setRenaming] = useState<Conversation | null>(null);

  const projectNames = useMemo(
    () => new Map(projects.map((project) => [project.id, project.name])),
    [projects],
  );
  const profileNames = useMemo(
    () => new Map((profilesQuery.data ?? []).map((profile) => [profile.id, profile.displayName])),
    [profilesQuery.data],
  );

  const conversations = useMemo(
    () => sortByRecentActivity(filterConversations(conversationsQuery.data ?? [], filters)),
    [conversationsQuery.data, filters],
  );

  // Paginação client-side; volta para a página 1 ao mudar qualquer filtro.
  const pagination = usePagination(conversations.length, { resetKey: filters });

  function patchFilters(patch: Partial<typeof filters>) {
    setFilters((current) => ({ ...current, ...patch }));
  }

  /** Abre a conversa no chat retomando o contexto (troca o projeto ativo se preciso). */
  function openConversation(conversation: Conversation) {
    if (conversation.projectId !== activeProject?.id) {
      setActiveProject(conversation.projectId);
    }
    navigate(`/chat?conversation=${conversation.id}`);
  }

  const loading = isPending || conversationsQuery.isPending;
  const errored = isError || conversationsQuery.isError;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.conversations.title')}</h1>
        <label className="ml-auto flex min-h-11 items-center gap-2 text-sm">
          <Checkbox
            checked={filters.showArchived}
            onChange={(event) => patchFilters({ showArchived: event.target.checked })}
          />
          {t('conversations.filters.showArchived')}
        </label>
      </div>

      {loading ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-64 w-full" />
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button
            type="button"
            variant="outline"
            onClick={() => {
              refetch();
              void conversationsQuery.refetch();
            }}
          >
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : (
        <>
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1">
              <label htmlFor="filter-project" className="text-xs font-medium">
                {t('conversations.filters.project')}
              </label>
              <Select
                id="filter-project"
                value={filters.projectId}
                onChange={(event) => patchFilters({ projectId: event.target.value })}
              >
                <option value="">{t('conversations.filters.all')}</option>
                {projects.map((project) => (
                  <option key={project.id} value={project.id}>
                    {project.name}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <label htmlFor="filter-period" className="text-xs font-medium">
                {t('conversations.filters.period.label')}
              </label>
              <Select
                id="filter-period"
                value={filters.period}
                onChange={(event) => patchFilters({ period: event.target.value as PeriodFilter })}
              >
                {PERIODS.map((period) => (
                  <option key={period} value={period}>
                    {t(`conversations.filters.period.options.${period === '' ? 'all' : period}`)}
                  </option>
                ))}
              </Select>
            </div>
            {filters.period === 'custom' && (
              <>
                <div className="flex flex-col gap-1">
                  <label htmlFor="filter-from" className="text-xs font-medium">
                    {t('conversations.filters.period.from')}
                  </label>
                  <Input
                    id="filter-from"
                    type="date"
                    value={filters.from}
                    onChange={(event) => patchFilters({ from: event.target.value })}
                  />
                </div>
                <div className="flex flex-col gap-1">
                  <label htmlFor="filter-to" className="text-xs font-medium">
                    {t('conversations.filters.period.to')}
                  </label>
                  <Input
                    id="filter-to"
                    type="date"
                    value={filters.to}
                    onChange={(event) => patchFilters({ to: event.target.value })}
                  />
                </div>
              </>
            )}
            <div className="flex flex-col gap-1">
              <label htmlFor="filter-channel" className="text-xs font-medium">
                {t('conversations.filters.channel')}
              </label>
              <Select id="filter-channel" disabled value="">
                <option value="">{t('conversations.filters.channelUnavailable')}</option>
              </Select>
            </div>
            <div className="flex flex-col gap-1">
              <label htmlFor="filter-author" className="text-xs font-medium">
                {t('conversations.filters.author')}
              </label>
              <Select
                id="filter-author"
                value={filters.authorId}
                onChange={(event) => patchFilters({ authorId: event.target.value })}
              >
                <option value="">{t('conversations.filters.all')}</option>
                {(profilesQuery.data ?? []).map((profile) => (
                  <option key={profile.id} value={profile.id}>
                    {profile.displayName}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex min-w-48 flex-1 flex-col gap-1">
              <label htmlFor="filter-search" className="text-xs font-medium">
                {t('conversations.filters.search')}
              </label>
              <Input
                id="filter-search"
                type="search"
                value={filters.search}
                placeholder={t('conversations.filters.searchPlaceholder')}
                onChange={(event) => patchFilters({ search: event.target.value })}
              />
            </div>
          </div>

          {conversations.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-start gap-3 p-6">
                <h2 className="font-heading text-lg font-semibold">
                  {t('conversations.empty.title')}
                </h2>
                <p className="text-sm text-foreground-muted">{t('conversations.empty.body')}</p>
              </CardContent>
            </Card>
          ) : (
            <>
              <ul className="flex flex-col gap-2" aria-label={t('conversations.listLabel')}>
                {pagination.paginate(conversations).map((conversation) => (
                <li key={conversation.id}>
                  <Card>
                    <CardContent className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center">
                      <div className="flex min-w-0 flex-1 flex-col gap-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <MessageSquare aria-hidden="true" className="size-4 text-foreground-muted" />
                          <span className="truncate font-medium">{conversation.title}</span>
                          <Badge variant={conversationStateVariant(conversation.state)}>
                            {t(`status.conversationState.${conversation.state}`)}
                          </Badge>
                        </div>
                        <p className="text-xs text-foreground-muted">
                          {t('conversations.item.meta', {
                            project: projectNames.get(conversation.projectId) ?? '—',
                            author:
                              profileNames.get(conversation.createdByProfileId) ??
                              t('conversations.item.unknownAuthor'),
                            when: formatRelativeTime(
                              conversation.lastMessageAt ?? conversation.createdAt,
                            ),
                          })}
                        </p>
                        <p className="sr-only">
                          {formatDateTime(conversation.lastMessageAt ?? conversation.createdAt)}
                        </p>
                      </div>
                      <div className="flex flex-wrap items-center gap-2">
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          onClick={() => openConversation(conversation)}
                        >
                          {t('conversations.actions.open')}
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => setRenaming(conversation)}
                          aria-label={t('conversations.actions.rename')}
                          title={t('conversations.actions.rename')}
                        >
                          <Pencil aria-hidden="true" />
                        </Button>
                        {conversation.state === 'active' ? (
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            onClick={() =>
                              updateConversation.mutate({
                                id: conversation.id,
                                input: { state: 'archived' },
                              })
                            }
                            aria-label={t('conversations.actions.archive')}
                            title={t('conversations.actions.archive')}
                          >
                            <Archive aria-hidden="true" />
                          </Button>
                        ) : (
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            onClick={() =>
                              updateConversation.mutate({
                                id: conversation.id,
                                input: { state: 'active' },
                              })
                            }
                            aria-label={t('conversations.actions.unarchive')}
                            title={t('conversations.actions.unarchive')}
                          >
                            <ArchiveRestore aria-hidden="true" />
                          </Button>
                        )}
                      </div>
                    </CardContent>
                  </Card>
                </li>
              ))}
              </ul>
              <PaginationBar pagination={pagination} />
            </>
          )}
        </>
      )}

      {renaming && (
        <RenameConversationDialog conversation={renaming} onClose={() => setRenaming(null)} />
      )}
    </div>
  );
}
