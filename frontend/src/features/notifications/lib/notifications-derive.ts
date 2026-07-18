import type {
  Notification,
  NotificationCategory,
  NotificationStatus,
  Settings,
} from '@/api';

/**
 * Derivações da central de notificações: status efetivo (preferências de
 * silenciamento aplicadas na UI), agrupamento/deduplicação por `groupKey`,
 * ordenação (mais recente primeiro) e filtros de status/categoria.
 * Funções puras, testadas sem render.
 */

/** Filtro de status da central: todas, não lidas ou silenciadas. */
export type NotificationStatusFilter = 'all' | 'unread' | 'muted';

/**
 * Status efetivo para exibição: preferências do perfil sobrepõem o status
 * persistido — notificações desligadas globalmente ou de categoria mutada
 * aparecem como silenciadas (regra de exibição fica na UI, não no backend).
 */
export function effectiveStatus(
  notification: Notification,
  settings: Pick<Settings, 'notificationsEnabled' | 'mutedCategories'> | undefined,
): NotificationStatus {
  if (settings && !settings.notificationsEnabled) return 'muted';
  if (settings?.mutedCategories.includes(notification.category)) return 'muted';
  return notification.status;
}

/** Ordenação da central: mais recente primeiro. */
export function sortByCreatedAtDesc(notifications: Notification[]): Notification[] {
  return [...notifications].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

/** Entrada da lista da central: item solto ou card de grupo. */
export type NotificationEntry =
  | { kind: 'single'; notification: Notification }
  | {
      kind: 'group';
      groupKey: string;
      /** Notificações do grupo, mais recente primeiro. */
      notifications: Notification[];
      /** Soma das ocorrências deduplicadas do grupo. */
      dedupeCount: number;
    };

/**
 * Agrupa itens com o mesmo `groupKey` não nulo em UM card de grupo
 * (o mais recente é o representante); itens sem `groupKey` ficam soltos.
 * A lista de entrada já deve estar ordenada por `createdAt` desc.
 */
export function groupNotifications(notifications: Notification[]): NotificationEntry[] {
  const groups = new Map<string, Notification[]>();
  for (const notification of notifications) {
    if (notification.groupKey === null) continue;
    const items = groups.get(notification.groupKey) ?? [];
    items.push(notification);
    groups.set(notification.groupKey, items);
  }
  return notifications.map((notification) => {
    if (notification.groupKey === null) return { kind: 'single', notification };
    const items = groups.get(notification.groupKey)!;
    // Só o representante (mais recente) vira entrada; os demais ficam no card.
    if (items[0] !== notification) return null;
    return {
      kind: 'group',
      groupKey: notification.groupKey,
      notifications: items,
      dedupeCount: items.reduce((total, item) => total + item.dedupeCount, 0),
    } as NotificationEntry;
  }).filter((entry): entry is NotificationEntry => entry !== null);
}

/** Filtra por status efetivo (considerando preferências) e por categoria. */
export function filterNotifications(
  notifications: Notification[],
  statusFilter: NotificationStatusFilter,
  categoryFilter: NotificationCategory | '',
  settings: Pick<Settings, 'notificationsEnabled' | 'mutedCategories'> | undefined,
): Notification[] {
  return notifications.filter((notification) => {
    if (categoryFilter !== '' && notification.category !== categoryFilter) return false;
    if (statusFilter !== 'all' && effectiveStatus(notification, settings) !== statusFilter) {
      return false;
    }
    return true;
  });
}

/** Categorias presentes na lista (para o filtro), na ordem de aparecimento. */
export function presentCategories(notifications: Notification[]): NotificationCategory[] {
  const seen = new Set<NotificationCategory>();
  for (const notification of notifications) seen.add(notification.category);
  return [...seen];
}
