import { describe, expect, it } from 'vitest';

import { MOBILE_PRIMARY_KEYS, navGroupsFor } from '@/app/navigation';

describe('navegação principal', () => {
  it('prioriza Chat, Dashboard, Conversas, Projetos e Central de Entregas no V3', () => {
    const businessItems = navGroupsFor('business').flatMap((group) => group.items);
    expect(businessItems.slice(0, 5).map((item) => item.path)).toEqual([
      '/chat',
      '/cockpit',
      '/conversations',
      '/projects',
      '/delivery',
    ]);
    expect(MOBILE_PRIMARY_KEYS).toEqual(['chat', 'cockpit', 'conversations', 'projects']);
  });
});
