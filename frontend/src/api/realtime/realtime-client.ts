import type { EventEnvelope } from '../contracts';

/** Estado da conexão com o hub `/hubs/events`. */
export type ConnectionState = 'connected' | 'reconnecting' | 'disconnected';

export type EventHandler = (event: EventEnvelope) => void;
export type ConnectionStateHandler = (state: ConnectionState) => void;

export interface Subscription {
  readonly streams: readonly string[];
  unsubscribe(): void;
}

/**
 * Cliente de tempo real — hub único `/hubs/events`, assinatura por streams.
 * Re-sync: snapshot + delta. O consumidor usa {@link SequenceTracker} para
 * dedupe e detecção de lacuna; em lacuna (ou reconexão), pede
 * {@link RealtimeClient.getSnapshot} e reaplica os eventos do snapshot
 * (o tracker descarta duplicados por `sequence`).
 */
export interface RealtimeClient {
  readonly state: ConnectionState;
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  /** Assina um ou mais streams; devolve handle para cancelar. */
  subscribe(streams: string | readonly string[], handler: EventHandler): Subscription;
  onStateChange(handler: ConnectionStateHandler): () => void;
  /**
   * Snapshot do stream (eventos recentes com sequence). `afterSequence`
   * restringe aos eventos posteriores à sequence informada (re-sync).
   */
  getSnapshot(stream: string, afterSequence?: number): Promise<EventEnvelope[]>;
}

/** Resultado da verificação de sequência de um envelope. */
export type SequenceCheck = 'applied' | 'duplicate' | 'gap';

/**
 * Rastreia `sequence` por stream para dedupe e detecção de lacuna.
 * - `duplicate`: sequence já vista (evento repetido) → descartar.
 * - `gap`: sequence pulou números → pedir snapshot do stream.
 *   O tracker avança o cursor; os eventos do snapshot chegam como
 *   `duplicate` e são descartados com segurança.
 */
export class SequenceTracker {
  private lastByStream = new Map<string, number>();

  check(event: Pick<EventEnvelope, 'stream' | 'sequence'>): SequenceCheck {
    const last = this.lastByStream.get(event.stream) ?? 0;
    if (event.sequence <= last) return 'duplicate';
    if (event.sequence > last + 1) return 'gap';
    this.lastByStream.set(event.stream, event.sequence);
    return 'applied';
  }

  /** Aplica um evento vindo de snapshot autoritativo, mesmo após compactação. */
  acceptSnapshot(event: Pick<EventEnvelope, 'stream' | 'sequence'>): boolean {
    const last = this.lastByStream.get(event.stream) ?? 0;
    if (event.sequence <= last) return false;
    this.lastByStream.set(event.stream, event.sequence);
    return true;
  }

  /** Última sequence vista no stream (0 se nenhuma). */
  lastSequence(stream: string): number {
    return this.lastByStream.get(stream) ?? 0;
  }

  reset(stream?: string): void {
    if (stream === undefined) this.lastByStream.clear();
    else this.lastByStream.delete(stream);
  }
}
