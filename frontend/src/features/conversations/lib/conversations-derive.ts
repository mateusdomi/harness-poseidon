import type { Conversation } from '@/api';

/** Períodos pré-definidos do filtro (+ intervalo personalizado). */
export type PeriodFilter = '' | 'today' | '7d' | '30d' | 'custom';

export interface ConversationFilters {
  period: PeriodFilter;
  /** Intervalo personalizado (yyyy-mm-dd, inclusivo) — só quando period='custom'. */
  from: string;
  to: string;
  /** '' = todos os autores. */
  authorId: string;
  /** Busca textual no título. */
  search: string;
  /** false = só ativas (padrão); true = só arquivadas. */
  showArchived: boolean;
}

export const EMPTY_CONVERSATION_FILTERS: ConversationFilters = {
  period: '',
  from: '',
  to: '',
  authorId: '',
  search: '',
  showArchived: false,
};

/** Referência temporal da conversa: última mensagem ou criação. */
export function conversationTimestamp(conversation: Conversation): string {
  return conversation.lastMessageAt ?? conversation.createdAt;
}

const DAY_MS = 86_400_000;

function startOfDay(date: Date): number {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
}

/** Início do período em ms (epoch); null = sem filtro de período. */
export function periodStart(period: PeriodFilter, now: Date): number | null {
  const todayStart = startOfDay(now);
  switch (period) {
    case 'today':
      return todayStart;
    case '7d':
      return todayStart - 6 * DAY_MS;
    case '30d':
      return todayStart - 29 * DAY_MS;
    default:
      return null;
  }
}

/**
 * Aplica os filtros da tela de histórico. Arquivadas só aparecem com
 * `showArchived` (arquivar ≠ excluir: saem da lista padrão). No intervalo
 * personalizado, `from`/`to` são datas inclusivas (dia inteiro).
 */
export function filterConversations(
  conversations: Conversation[],
  filters: ConversationFilters,
  activeProjectId: string | null,
  now: Date = new Date(),
): Conversation[] {
  const search = filters.search.trim().toLowerCase();
  const customFrom = filters.from !== '' ? startOfDay(new Date(`${filters.from}T00:00:00`)) : null;
  const customTo =
    filters.to !== '' ? startOfDay(new Date(`${filters.to}T00:00:00`)) + DAY_MS : null;
  const presetStart = periodStart(filters.period, now);

  if (activeProjectId === null) return [];

  return conversations.filter((conversation) => {
    if (filters.showArchived !== (conversation.state === 'archived')) return false;
    if (conversation.projectId !== activeProjectId) return false;
    if (filters.authorId !== '' && conversation.createdByProfileId !== filters.authorId) {
      return false;
    }
    if (search !== '' && !conversation.title.toLowerCase().includes(search)) return false;

    const timestamp = Date.parse(conversationTimestamp(conversation));
    if (filters.period === 'custom') {
      if (customFrom !== null && timestamp < customFrom) return false;
      if (customTo !== null && timestamp >= customTo) return false;
    } else if (presetStart !== null && timestamp < presetStart) {
      return false;
    }
    return true;
  });
}

/** Ordenação da lista: atividade mais recente primeiro. */
export function sortByRecentActivity(conversations: Conversation[]): Conversation[] {
  return [...conversations].sort(
    (a, b) => Date.parse(conversationTimestamp(b)) - Date.parse(conversationTimestamp(a)),
  );
}
