import type { Notification } from '@/api';
import {
  effectiveStatus,
  filterNotifications,
  groupNotifications,
  sortByCreatedAtDesc,
} from '@/features/notifications/lib/notifications-derive';

/** Notificação sintética para os testes das funções puras. */
function makeNotification(overrides: Partial<Notification> & { id: string }): Notification {
  return {
    profileId: 'profile-1',
    severity: 'info',
    category: 'system',
    title: 'Título',
    body: 'Corpo',
    groupKey: null,
    dedupeCount: 1,
    status: 'unread',
    link: null,
    createdAt: '2026-07-17T12:00:00Z',
    readAt: null,
    ...overrides,
  };
}

describe('notifications-derive', () => {
  it('ordena por createdAt desc (mais recente primeiro)', () => {
    const sorted = sortByCreatedAtDesc([
      makeNotification({ id: 'a', createdAt: '2026-07-15T10:00:00Z' }),
      makeNotification({ id: 'b', createdAt: '2026-07-17T10:00:00Z' }),
      makeNotification({ id: 'c', createdAt: '2026-07-16T10:00:00Z' }),
    ]);
    expect(sorted.map((item) => item.id)).toEqual(['b', 'c', 'a']);
  });

  it('status efetivo: sem settings usa o status persistido', () => {
    const notification = makeNotification({ id: 'a', status: 'unread' });
    expect(effectiveStatus(notification, undefined)).toBe('unread');
  });

  it('status efetivo: categoria mutada aparece como silenciada', () => {
    const notification = makeNotification({ id: 'a', category: 'quota', status: 'unread' });
    expect(
      effectiveStatus(notification, { notificationsEnabled: true, mutedCategories: ['quota'] }),
    ).toBe('muted');
    expect(
      effectiveStatus(notification, { notificationsEnabled: true, mutedCategories: ['task'] }),
    ).toBe('unread');
  });

  it('status efetivo: toggle global desligado silencia tudo', () => {
    const notification = makeNotification({ id: 'a', status: 'read', readAt: '2026-07-17T12:00:00Z' });
    expect(
      effectiveStatus(notification, { notificationsEnabled: false, mutedCategories: [] }),
    ).toBe('muted');
  });

  it('agrupa itens com o mesmo groupKey: representante mais recente e dedupe somado', () => {
    const entries = groupNotifications([
      makeNotification({ id: 'nova', groupKey: 'g1', dedupeCount: 2, createdAt: '2026-07-17T12:00:00Z' }),
      makeNotification({ id: 'solta', createdAt: '2026-07-16T12:00:00Z' }),
      makeNotification({ id: 'antiga', groupKey: 'g1', dedupeCount: 3, createdAt: '2026-07-15T12:00:00Z' }),
    ]);
    expect(entries).toHaveLength(2);
    const group = entries[0];
    expect(group).toMatchObject({ kind: 'group', groupKey: 'g1', dedupeCount: 5 });
    if (group.kind === 'group') {
      expect(group.notifications.map((item) => item.id)).toEqual(['nova', 'antiga']);
    }
    expect(entries[1]).toMatchObject({ kind: 'single' });
  });

  it('itens sem groupKey ficam soltos mesmo com mais de um', () => {
    const entries = groupNotifications([
      makeNotification({ id: 'a' }),
      makeNotification({ id: 'b' }),
    ]);
    expect(entries.every((entry) => entry.kind === 'single')).toBe(true);
  });

  it('filtra por status efetivo (preferências aplicadas) e por categoria', () => {
    const settings = { notificationsEnabled: true, mutedCategories: ['quota' as const] };
    const notifications = [
      makeNotification({ id: 'lida', status: 'read', readAt: '2026-07-17T12:00:00Z' }),
      makeNotification({ id: 'nao-lida', status: 'unread', category: 'task' }),
      makeNotification({ id: 'mutada-por-pref', status: 'unread', category: 'quota' }),
    ];
    expect(
      filterNotifications(notifications, 'unread', '', settings).map((item) => item.id),
    ).toEqual(['nao-lida']);
    expect(
      filterNotifications(notifications, 'muted', '', settings).map((item) => item.id),
    ).toEqual(['mutada-por-pref']);
    expect(
      filterNotifications(notifications, 'all', 'task', settings).map((item) => item.id),
    ).toEqual(['nao-lida']);
  });
});
