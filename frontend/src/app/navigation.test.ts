import { describe, expect, it } from 'vitest';

import { MOBILE_PRIMARY_KEYS, NAV_GROUPS } from '@/app/navigation';

describe('navegação principal', () => {
  it('prioriza Chat, Dashboard, Quadro, Conversas e Projetos', () => {
    expect(NAV_GROUPS[0].items.slice(0, 5).map((item) => item.path)).toEqual([
      '/chat',
      '/cockpit',
      '/board',
      '/conversations',
      '/projects',
    ]);
    expect(MOBILE_PRIMARY_KEYS).toEqual(['chat', 'cockpit', 'board', 'conversations']);
  });
});
