import { describe, expect, it } from 'vitest';

import { isOperationalFeatureEnabled } from './features';

describe('operational feature flags', () => {
  it('mantém a UI contratual de governança fail-closed', () => {
    expect(isOperationalFeatureEnabled(undefined)).toBe(false);
    expect(isOperationalFeatureEnabled('off')).toBe(false);
    expect(isOperationalFeatureEnabled('true')).toBe(false);
    expect(isOperationalFeatureEnabled('on')).toBe(true);
  });
});
