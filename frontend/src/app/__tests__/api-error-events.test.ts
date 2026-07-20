import { describe, expect, it, vi } from 'vitest';

import { ApiError } from '@/api';
import {
  PERMISSION_DENIED_EVENT,
  publishApiAuthorizationError,
  SESSION_INVALID_EVENT,
} from '@/app/api-error-events';

describe('API authorization events', () => {
  it('publica sessão inválida para 401', () => {
    const listener = vi.fn();
    window.addEventListener(SESSION_INVALID_EVENT, listener);
    publishApiAuthorizationError(ApiError.of(401, 'Unauthorized'), true);
    expect(listener).toHaveBeenCalledOnce();
    window.removeEventListener(SESSION_INVALID_EVENT, listener);
  });

  it('publica permissão negada apenas para erro 403 de query', () => {
    const listener = vi.fn();
    window.addEventListener(PERMISSION_DENIED_EVENT, listener);
    publishApiAuthorizationError(ApiError.of(403, 'Forbidden'), false);
    expect(listener).not.toHaveBeenCalled();
    publishApiAuthorizationError(ApiError.of(403, 'Forbidden'), true);
    expect(listener).toHaveBeenCalledOnce();
    window.removeEventListener(PERMISSION_DENIED_EVENT, listener);
  });
});
