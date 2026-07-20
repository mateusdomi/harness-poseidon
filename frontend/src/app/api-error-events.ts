import { ApiError } from '@/api';

export const SESSION_INVALID_EVENT = 'poseidon:session-invalid';
export const PERMISSION_DENIED_EVENT = 'poseidon:permission-denied';

/** Converte os status de autorização canônicos em estados globais da aplicação. */
export function publishApiAuthorizationError(error: unknown, includePermission: boolean): void {
  if (!(error instanceof ApiError) || typeof window === 'undefined') return;
  if (error.problem.status === 401) window.dispatchEvent(new Event(SESSION_INVALID_EVENT));
  if (includePermission && error.problem.status === 403) {
    window.dispatchEvent(new Event(PERMISSION_DENIED_EVENT));
  }
}
