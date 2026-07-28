import { describe, expect, it } from 'vitest';

import {
  resolvePresentationPolicy,
  TECHNICAL_PRESENTATION_ENTITLEMENT,
} from '@/app/presentation/presentation-policy';

const technicalEntitlement = {
  key: TECHNICAL_PRESENTATION_ENTITLEMENT,
  included: true,
};

describe('presentation policy', () => {
  it('mantém a experiência de negócio como padrão', () => {
    expect(resolvePresentationPolicy({ role: 'member' }, [], undefined)).toEqual({
      mode: 'business',
      allowedModes: ['business'],
      showTechnicalDetails: false,
      showAdministrativeActions: false,
    });
  });

  it('autoriza detalhes técnicos somente por entitlement', () => {
    const denied = resolvePresentationPolicy({ role: 'member' }, [], 'technical');
    const allowed = resolvePresentationPolicy(
      { role: 'member' },
      [technicalEntitlement],
      'technical',
    );

    expect(denied.mode).toBe('business');
    expect(allowed).toMatchObject({
      mode: 'technical',
      showTechnicalDetails: true,
      showAdministrativeActions: false,
    });
  });

  it('autoriza ações administrativas somente pelo papel vindo da API', () => {
    expect(resolvePresentationPolicy({ role: 'member' }, [technicalEntitlement], 'admin').mode)
      .toBe('business');
    expect(resolvePresentationPolicy({ role: 'admin' }, [], 'admin')).toMatchObject({
      mode: 'admin',
      showTechnicalDetails: true,
      showAdministrativeActions: true,
    });
  });
});
