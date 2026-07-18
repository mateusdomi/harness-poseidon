import type { EventEnvelope, EventPayloadMap, EventType, Ulid } from '../contracts';
import type {
  ConnectionState,
  ConnectionStateHandler,
  EventHandler,
  RealtimeClient,
  Subscription,
} from './realtime-client';

export interface MockRealtimeOptions {
  /** Atraso simulado de conexão (ms). Padrão 150. */
  connectDelayMs?: number;
  /** Relógio (ISO-8601 UTC) injetável para determinismo. */
  now?: () => string;
}

interface Subscriber {
  streams: ReadonlySet<string>;
  handler: EventHandler;
}

/**
 * Realtime em memória: emite envelopes com `sequence` crescente por stream,
 * suporta múltiplos subscribers, mantém log por stream para snapshot
 * (re-sync) e simula heartbeat de attempts e chat por chunks.
 * O MockApiClient chama {@link MockRealtimeClient.emit} nas mutações.
 */
export class MockRealtimeClient implements RealtimeClient {
  #state: ConnectionState = 'disconnected';
  #sequences = new Map<string, number>();
  #logs = new Map<string, EventEnvelope[]>();
  #subscribers = new Set<Subscriber>();
  #stateHandlers = new Set<ConnectionStateHandler>();
  #timers = new Set<ReturnType<typeof setInterval>>();
  readonly #options: Required<MockRealtimeOptions>;

  constructor(options: MockRealtimeOptions = {}) {
    this.#options = {
      connectDelayMs: options.connectDelayMs ?? 150,
      now: options.now ?? (() => new Date().toISOString()),
    };
  }

  get state(): ConnectionState {
    return this.#state;
  }

  connect(): Promise<void> {
    this.#setState('reconnecting');
    return new Promise((resolve) => {
      setTimeout(() => {
        this.#setState('connected');
        resolve();
      }, this.#options.connectDelayMs);
    });
  }

  disconnect(): Promise<void> {
    this.#clearTimers();
    this.#setState('disconnected');
    return Promise.resolve();
  }

  /** Limpa timers (heartbeats/chunks) sem alterar subscribers — usado em testes. */
  dispose(): void {
    this.#clearTimers();
  }

  subscribe(streams: string | readonly string[], handler: EventHandler): Subscription {
    const list = typeof streams === 'string' ? [streams] : [...streams];
    const subscriber: Subscriber = { streams: new Set(list), handler };
    this.#subscribers.add(subscriber);
    return {
      streams: list,
      unsubscribe: () => {
        this.#subscribers.delete(subscriber);
      },
    };
  }

  onStateChange(handler: ConnectionStateHandler): () => void {
    this.#stateHandlers.add(handler);
    return () => this.#stateHandlers.delete(handler);
  }

  /** Snapshot = log de eventos já emitidos no stream (ordem crescente). */
  getSnapshot(stream: string): Promise<EventEnvelope[]> {
    return Promise.resolve([...(this.#logs.get(stream) ?? [])]);
  }

  /**
   * Emite um evento: atribui a próxima `sequence` do stream, registra no
   * log (para snapshot) e entrega aos subscribers do stream.
   */
  emit<T extends EventType>(stream: string, type: T, payload: EventPayloadMap[T]): EventEnvelope<T> {
    const sequence = (this.#sequences.get(stream) ?? 0) + 1;
    this.#sequences.set(stream, sequence);
    const envelope = {
      stream,
      sequence,
      type,
      occurredAt: this.#options.now(),
      payload,
    } as EventEnvelope<T>;
    this.#appendLog(envelope);
    this.#deliver(envelope);
    return envelope;
  }

  /** Reentrega um envelope já emitido (simula retry/replay — exercita dedupe). */
  replay(envelope: EventEnvelope): void {
    this.#deliver(envelope);
  }

  /**
   * Emite envelope fora de ordem (simula perda — exercita detecção de
   * lacuna por sequence). NÃO toca o contador nem o log de snapshot.
   */
  emitOutOfOrder<T extends EventType>(
    stream: string,
    sequence: number,
    type: T,
    payload: EventPayloadMap[T],
  ): EventEnvelope<T> {
    const envelope = {
      stream,
      sequence,
      type,
      occurredAt: this.#options.now(),
      payload,
    } as EventEnvelope<T>;
    this.#deliver(envelope);
    return envelope;
  }

  /** Heartbeat periódico de uma tentativa em andamento (streams attempt + task). */
  startAttemptHeartbeat(input: {
    attemptId: Ulid;
    taskId: Ulid;
    intervalMs?: number;
  }): void {
    const intervalMs = input.intervalMs ?? 5_000;
    const startedAt = Date.now();
    let ticks = 0;
    const timer = setInterval(() => {
      ticks += 1;
      const payload = {
        attemptId: input.attemptId,
        taskId: input.taskId,
        elapsedMs: Date.now() - startedAt,
        tokensInput: ticks * 480,
        tokensOutput: ticks * 160,
        costUsd: ticks * 0.004,
      };
      this.emit(`attempt:${input.attemptId}`, 'attempt.heartbeat', payload);
      this.emit(`task:${input.taskId}`, 'attempt.heartbeat', payload);
    }, intervalMs);
    this.#timers.add(timer);
  }

  /** Fluxo de chat por chunks de um turno (streams da conversa). */
  simulateChatTurn(input: {
    conversationId: Ulid;
    turnId: Ulid;
    agentId: Ulid;
    chunks: string[];
    messageId: Ulid;
    chunkDelayMs?: number;
    onCompleted?: () => void;
  }): void {
    const stream = `conversation:${input.conversationId}`;
    const chunkDelayMs = input.chunkDelayMs ?? 120;
    this.emit(stream, 'chat.turnStarted', {
      conversationId: input.conversationId,
      turnId: input.turnId,
      agentId: input.agentId,
    });
    input.chunks.forEach((text, index) => {
      this.#schedule(() => {
        this.emit(stream, 'chat.turnChunk', {
          conversationId: input.conversationId,
          turnId: input.turnId,
          index,
          text,
        });
      }, chunkDelayMs * (index + 1));
    });
    this.#schedule(() => {
      input.onCompleted?.();
      this.emit(stream, 'chat.turnCompleted', {
        conversationId: input.conversationId,
        turnId: input.turnId,
        messageId: input.messageId,
        finishReason: 'stop',
      });
    }, chunkDelayMs * (input.chunks.length + 1));
  }

  #appendLog(envelope: EventEnvelope): void {
    const log = this.#logs.get(envelope.stream) ?? [];
    log.push(envelope);
    // Ring buffer: mantém os 500 eventos mais recentes por stream.
    if (log.length > 500) log.splice(0, log.length - 500);
    this.#logs.set(envelope.stream, log);
  }

  #deliver(envelope: EventEnvelope): void {
    for (const subscriber of this.#subscribers) {
      if (subscriber.streams.has(envelope.stream)) subscriber.handler(envelope);
    }
  }

  #schedule(fn: () => void, delayMs: number): void {
    const timer = setTimeout(() => {
      this.#timers.delete(timer);
      fn();
    }, delayMs);
    this.#timers.add(timer);
  }

  #clearTimers(): void {
    for (const timer of this.#timers) clearTimeout(timer);
    this.#timers.clear();
  }

  #setState(state: ConnectionState): void {
    if (this.#state === state) return;
    this.#state = state;
    for (const handler of this.#stateHandlers) handler(state);
  }
}
