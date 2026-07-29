import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import {
  Archive,
  ArchiveRestore,
  ChevronLeft,
  ChevronRight,
  MessageSquare,
  Pencil,
} from 'lucide-react';

import type { Conversation } from '@/api';
import {
  Badge,
  Button,
  Card,
  CardContent,
  Checkbox,
  Input,
  Select,
  Skeleton,
} from '@/design-system';
import { conversationStateVariant } from '@/lib/status';
import { formatDateTime, formatRelativeTime } from '@/lib/format';
import { RenameConversationDialog } from '@/features/conversations/components/rename-conversation-dialog';
import {
  useConversations,
  useUpdateConversation,
} from '@/features/conversations/hooks/use-conversations';
import {
  EMPTY_CONVERSATION_FILTERS,
  filterConversations,
  sortByRecentActivity,
  type PeriodFilter,
} from '@/features/conversations/lib/conversations-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useProfiles } from '@/features/shared/hooks/use-profiles';
import { usePagination } from '@/features/shared/hooks/use-pagination';
import { useConversationPreferencesStore } from '@/stores/conversation-preferences-store';

const PERIODS: PeriodFilter[] = ['', 'today', '7d', '30d', 'custom'];
export const CONVERSATIONS_PAGE_SIZE = 20;

/**
 * Histórico do projeto ativo em grade compacta: período, autor e busca
 * textual, renomear, arquivar (≠ excluir) e abertura retomando o contexto
 * no chat.
 */
export default function UconversationsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { profileId, activeProject, isPending, isError, refetch } = useActiveProject();
  const rememberConversation = useConversationPreferencesStore((state) => state.selectConversation);
  const conversationsQuery = useConversations();
  const profilesQuery = useProfiles();
  const updateConversation = useUpdateConversation();

  const [filters, setFilters] = useState(EMPTY_CONVERSATION_FILTERS);
  const [renaming, setRenaming] = useState<Conversation | null>(null);

  const profileNames = useMemo(
    () => new Map((profilesQuery.data ?? []).map((profile) => [profile.id, profile.displayName])),
    [profilesQuery.data],
  );

  const conversations = useMemo(
    () =>
      sortByRecentActivity(
        filterConversations(conversationsQuery.data ?? [], filters, activeProject?.id ?? null),
      ),
    [activeProject?.id, conversationsQuery.data, filters],
  );

  // Densidade-alvo da F7: até 20 cards por página, voltando ao início
  // quando busca, filtros ou o projeto global mudam.
  const pagination = usePagination(conversations.length, {
    pageSize: CONVERSATIONS_PAGE_SIZE,
    resetKey: `${activeProject?.id ?? ''}:${JSON.stringify(filters)}`,
  });

  function patchFilters(patch: Partial<typeof filters>) {
    setFilters((current) => ({ ...current, ...patch }));
  }

  /** Abre a conversa no chat retomando o contexto do projeto ativo. */
  function openConversation(conversation: Conversation) {
    if (profileId) {
      rememberConversation(profileId, conversation.projectId, conversation.id);
    }
    navigate(`/chat/${conversation.id}`);
  }

  const loading = isPending || conversationsQuery.isLoading;
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
          <div className="grid items-end gap-3 rounded-xl border border-border bg-surface p-3 md:grid-cols-2 lg:grid-cols-4">
            <div className="flex min-w-48 flex-col gap-1 md:col-span-2">
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
              <ul
                className="grid gap-3 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4"
                aria-label={t('conversations.listLabel')}
                data-testid="conversation-grid"
              >
                {pagination.paginate(conversations).map((conversation) => (
                  <li key={conversation.id} className="min-w-0">
                    <Card className="h-full">
                      <CardContent className="flex h-full min-h-36 flex-col gap-3 p-3">
                        <div className="flex min-w-0 flex-1 flex-col gap-1.5">
                          <div className="flex flex-wrap items-center gap-2">
                            <MessageSquare
                              aria-hidden="true"
                              className="size-4 shrink-0 text-foreground-muted"
                            />
                            <Badge variant={conversationStateVariant(conversation.state)}>
                              {t(`status.conversationState.${conversation.state}`)}
                            </Badge>
                          </div>
                          <span className="line-clamp-2 font-medium">{conversation.title}</span>
                          <p className="line-clamp-2 text-xs text-foreground-muted">
                            {t('conversations.item.meta', {
                              project: activeProject?.name ?? '—',
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
                        <div className="flex items-center gap-1 border-t border-border pt-2">
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            className="mr-auto"
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
              {pagination.pageCount > 1 && (
                <nav
                  aria-label={t('common.pagination.label')}
                  className="flex flex-wrap items-center justify-between gap-3"
                >
                  <p className="text-sm text-foreground-muted" aria-live="polite">
                    {t('common.pagination.range', {
                      from: pagination.rangeStart,
                      to: pagination.rangeEnd,
                      total: pagination.total,
                    })}
                  </p>
                  <div className="flex items-center gap-2">
                    <Button
                      type="button"
                      variant="outline"
                      size="icon"
                      disabled={pagination.page <= 1}
                      onClick={() => pagination.setPage(pagination.page - 1)}
                      aria-label={t('common.pagination.previous')}
                    >
                      <ChevronLeft aria-hidden="true" />
                    </Button>
                    <span className="text-sm text-foreground-muted">
                      {t('common.pagination.pageOf', {
                        page: pagination.page,
                        total: pagination.pageCount,
                      })}
                    </span>
                    <Button
                      type="button"
                      variant="outline"
                      size="icon"
                      disabled={pagination.page >= pagination.pageCount}
                      onClick={() => pagination.setPage(pagination.page + 1)}
                      aria-label={t('common.pagination.next')}
                    >
                      <ChevronRight aria-hidden="true" />
                    </Button>
                  </div>
                </nav>
              )}
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
