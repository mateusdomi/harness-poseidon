import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';

import { parseEventEnvelope, type EventEnvelope } from '../contracts';
import type {
  ConnectionState,
  ConnectionStateHandler,
  EventHandler,
  RealtimeClient,
  Subscription,
} from './realtime-client';

export interface SignalRRealtimeOptions {
  /** Base URL do backend (ex.: `https://localhost:5001`). Hub em `/hubs/events`. */
  baseUrl: string;
}

interface Subscriber {
  streams: ReadonlySet<string>;
  handler: EventHandler;
}

/**
 * Cliente SignalR real (modo `VITE_API_MODE=http`).
 * Convenções com o backend ASP.NET Core:
 * - hub único `/hubs/events`;
 * - servidor chama `event` com o envelope JSON;
 * - métodos do hub: `SubscribeToStreams(string[])`,
 *   `UnsubscribeFromStreams(string[])`, `GetStreamSnapshot(string)`.
 */
export class SignalRRealtimeClient implements RealtimeClient {
  #state: ConnectionState = 'disconnected';
  #connection: HubConnection | null = null;
  #subscribers = new Set<Subscriber>();
  #stateHandlers = new Set<ConnectionStateHandler>();
  readonly #baseUrl: string;

  constructor(options: SignalRRealtimeOptions) {
    this.#baseUrl = options.baseUrl.replace(/\/$/, '');
  }

  get state(): ConnectionState {
    return this.#state;
  }

  async connect(): Promise<void> {
    if (this.#connection) return;
    const connection = new HubConnectionBuilder()
      .withUrl(`${this.#baseUrl}/hubs/events`)
      .withAutomaticReconnect()
      .build();

    connection.on('event', (raw: unknown) => {
      try {
        this.#deliver(parseEventEnvelope(raw));
      } catch {
        // Envelope fora do contrato: ignora (não derruba o stream).
      }
    });
    connection.onreconnecting(() => this.#setState('reconnecting'));
    connection.onreconnected(() => this.#setState('connected'));
    connection.onclose(() => this.#setState('disconnected'));

    this.#connection = connection;
    await connection.start();
    this.#setState('connected');
  }

  async disconnect(): Promise<void> {
    const connection = this.#connection;
    this.#connection = null;
    if (connection) await connection.stop();
    this.#setState('disconnected');
  }

  subscribe(streams: string | readonly string[], handler: EventHandler): Subscription {
    const list = typeof streams === 'string' ? [streams] : [...streams];
    const subscriber: Subscriber = { streams: new Set(list), handler };
    this.#subscribers.add(subscriber);
    void this.#invoke('SubscribeToStreams', list);
    return {
      streams: list,
      unsubscribe: () => {
        this.#subscribers.delete(subscriber);
        void this.#invoke('UnsubscribeFromStreams', list);
      },
    };
  }

  onStateChange(handler: ConnectionStateHandler): () => void {
    this.#stateHandlers.add(handler);
    return () => this.#stateHandlers.delete(handler);
  }

  async getSnapshot(stream: string): Promise<EventEnvelope[]> {
    if (!this.#connection || this.#connection.state !== HubConnectionState.Connected) {
      return [];
    }
    const raw = await this.#connection.invoke<unknown[]>('GetStreamSnapshot', stream);
    return raw.map((item) => parseEventEnvelope(item));
  }

  #deliver(envelope: EventEnvelope): void {
    for (const subscriber of this.#subscribers) {
      if (subscriber.streams.has(envelope.stream)) subscriber.handler(envelope);
    }
  }

  async #invoke(method: string, streams: readonly string[]): Promise<void> {
    if (!this.#connection || this.#connection.state !== HubConnectionState.Connected) return;
    await this.#connection.invoke(method, [...streams]);
  }

  #setState(state: ConnectionState): void {
    if (this.#state === state) return;
    this.#state = state;
    for (const handler of this.#stateHandlers) handler(state);
  }
}
