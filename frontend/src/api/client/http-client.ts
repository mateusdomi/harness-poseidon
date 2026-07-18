import {
  ApiError,
  problemDetailsSchema,
  type AppendTaskInstructionInput,
  type ChatTurnHandle,
  type CreatableResource,
  type CreateInputMap,
  type ListQuery,
  type MoveTaskInput,
  type Page,
  type Profile,
  type RemovableResource,
  type ResolveApprovalInput,
  type ResourceKind,
  type ResourceMap,
  type SetOperationModeInput,
  type StartChatTurnInput,
  type Task,
  type TaskInstruction,
  type TransitionDocumentInput,
  type TransitionSolicitationInput,
  type Ulid,
  type UpdatableResource,
  type UpdateInputMap,
  type Workflow,
  type Approval,
  type Document,
  type Solicitation,
} from '../contracts';
import type { ApiClient } from './api-client';

export interface HttpApiClientOptions {
  /** Base URL do backend (ex.: `https://localhost:5001`). Rotas em `/api/v1`. */
  baseUrl: string;
  /** fetch injetável (testes). Padrão: fetch global com credentials de sessão. */
  fetchFn?: typeof fetch;
}

/**
 * Cliente HTTP real (modo `VITE_API_MODE=http`): fetch, base `/api/v1`,
 * paginação por cursor, erros `application/problem+json` (RFC 7807) e
 * sessão local via cookie (`credentials: 'include'`, preparado p/ OIDC).
 */
export class HttpApiClient implements ApiClient {
  readonly #baseUrl: string;
  readonly #fetch: typeof fetch;

  constructor(options: HttpApiClientOptions) {
    this.#baseUrl = `${options.baseUrl.replace(/\/$/, '')}/api/v1`;
    this.#fetch = options.fetchFn ?? ((input, init) => fetch(input, init));
  }

  list<K extends ResourceKind>(resource: K, query?: ListQuery): Promise<Page<ResourceMap[K]>> {
    const params = new URLSearchParams();
    if (query?.cursor) params.set('cursor', query.cursor);
    if (query?.limit) params.set('limit', String(query.limit));
    for (const [key, value] of Object.entries(query?.filter ?? {})) {
      if (value !== undefined) params.set(key, String(value));
    }
    const qs = params.toString();
    return this.#request('GET', `/${resource}${qs ? `?${qs}` : ''}`);
  }

  get<K extends ResourceKind>(resource: K, id: Ulid): Promise<ResourceMap[K]> {
    return this.#request('GET', `/${resource}/${id}`);
  }

  create<K extends CreatableResource>(
    resource: K,
    input: CreateInputMap[K],
  ): Promise<ResourceMap[K]> {
    return this.#request('POST', `/${resource}`, input);
  }

  update<K extends UpdatableResource>(
    resource: K,
    id: Ulid,
    input: UpdateInputMap[K],
  ): Promise<ResourceMap[K]> {
    return this.#request('PATCH', `/${resource}/${id}`, input);
  }

  async remove(resource: RemovableResource, id: Ulid): Promise<void> {
    await this.#request('DELETE', `/${resource}/${id}`);
  }

  getCurrentProfile(): Promise<Profile> {
    return this.#request('GET', '/profiles/current');
  }

  moveTask(taskId: Ulid, input: MoveTaskInput): Promise<Task> {
    return this.#request('POST', `/tasks/${taskId}/moves`, input);
  }

  appendTaskInstruction(taskId: Ulid, input: AppendTaskInstructionInput): Promise<TaskInstruction> {
    return this.#request('POST', `/tasks/${taskId}/instructions`, input);
  }

  transitionSolicitation(id: Ulid, input: TransitionSolicitationInput): Promise<Solicitation> {
    return this.#request('POST', `/solicitations/${id}/transitions`, input);
  }

  resolveApproval(id: Ulid, input: ResolveApprovalInput): Promise<Approval> {
    return this.#request('POST', `/approvals/${id}/resolution`, input);
  }

  transitionDocument(id: Ulid, input: TransitionDocumentInput): Promise<Document> {
    return this.#request('POST', `/documents/${id}/transitions`, input);
  }

  setWorkflowOperationMode(workflowId: Ulid, input: SetOperationModeInput): Promise<Workflow> {
    return this.#request('POST', `/workflows/${workflowId}/operation-mode`, input);
  }

  markNotificationsRead(ids: Ulid[]): Promise<number> {
    return this.#request('POST', '/notifications/read', { ids });
  }

  muteNotifications(ids: Ulid[]): Promise<number> {
    return this.#request('POST', '/notifications/mute', { ids });
  }

  startChatTurn(conversationId: Ulid, input: StartChatTurnInput): Promise<ChatTurnHandle> {
    return this.#request('POST', `/conversations/${conversationId}/turns`, input);
  }

  async #request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.#fetch(`${this.#baseUrl}${path}`, {
      method,
      credentials: 'include',
      headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });

    if (!response.ok) {
      throw await this.#toApiError(response);
    }
    if (response.status === 204) return undefined as T;
    return (await response.json()) as T;
  }

  async #toApiError(response: Response): Promise<ApiError> {
    const contentType = response.headers.get('content-type') ?? '';
    if (contentType.includes('application/problem+json')) {
      const parsed = problemDetailsSchema.safeParse(await response.json());
      if (parsed.success) return new ApiError(parsed.data);
    }
    return ApiError.of(response.status, response.statusText || 'Erro na API');
  }
}
