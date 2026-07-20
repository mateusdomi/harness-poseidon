import { useEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import type { EventEnvelope, EventType } from '@/api';
import { SequenceTracker } from '@/api';
import { useRealtime } from '@/app/api-context';

export interface UseRealtimeStreamOptions {
  /** Só reage a estes tipos (omitir = todos os tipos do stream). */
  types?: readonly EventType[];
  /** Callback por evento aplicado (pós-dedupe). */
  onEvent?: (event: EventEnvelope) => void;
  /** Query keys invalidadas a cada evento aplicado. */
  invalidate?: readonly (readonly unknown[])[];
  /** Quando definido, só invalida se o tipo do evento estiver na lista. */
  invalidateEvents?: readonly EventType[];
}

/**
 * Assina streams do hub realtime com cleanup no unmount.
 * Dedupe/lacuna por `sequence` via {@link SequenceTracker}: em lacuna,
 * pede o snapshot do stream e o reaplica (o tracker descarta duplicados).
 * `null` em `streamNames` desliga a assinatura (ex.: sem projeto ativo).
 */
export function useRealtimeStream(
  streamNames: string | readonly string[] | null,
  options: UseRealtimeStreamOptions = {},
): void {
  const realtime = useRealtime();
  const queryClient = useQueryClient();
  // Refs: a assinatura não é recriada quando callbacks/keys mudam de identidade.
  const optionsRef = useRef(options);
  optionsRef.current = options;
  const trackerRef = useRef<SequenceTracker | null>(null);
  trackerRef.current ??= new SequenceTracker();

  const key = streamNames === null ? null : [streamNames].flat().join('|');

  useEffect(() => {
    if (key === null) return;
    const streams = key.split('|');
    const tracker = trackerRef.current!;

    const apply = (event: EventEnvelope) => {
      const { types, onEvent, invalidate, invalidateEvents } = optionsRef.current;
      if (types && !types.includes(event.type)) return;
      onEvent?.(event);
      if (invalidateEvents && !invalidateEvents.includes(event.type)) return;
      for (const queryKey of invalidate ?? []) {
        void queryClient.invalidateQueries({ queryKey });
      }
    };

    const resync = async (stream: string, pending?: EventEnvelope) => {
      const snapshot = await realtime.getSnapshot(stream, tracker.lastSequence(stream));
      for (const past of [...snapshot].sort((a, b) => a.sequence - b.sequence)) {
        if (tracker.acceptSnapshot(past)) apply(past);
      }
      // O delta normalmente contém o envelope que revelou a lacuna. Se não
      // contiver, o envelope pendente ainda é aplicado após o snapshot.
      if (pending && tracker.acceptSnapshot(pending)) apply(pending);
      for (const queryKey of optionsRef.current.invalidate ?? []) {
        void queryClient.invalidateQueries({ queryKey });
      }
    };

    const handle = (event: EventEnvelope) => {
      const check = tracker.check(event);
      if (check === 'duplicate') return;
      if (check === 'gap') {
        void resync(event.stream, event).catch(() => undefined);
        return;
      }
      apply(event);
    };

    const subscription = realtime.subscribe(streams, handle);
    const handleState = (state: string) => {
      if (state !== 'connected') return;
      for (const stream of streams) void resync(stream).catch(() => undefined);
    };
    const unsubscribeState = realtime.onStateChange(handleState);
    if (realtime.state === 'connected') handleState('connected');
    return () => {
      unsubscribeState();
      subscription.unsubscribe();
    };
  }, [realtime, queryClient, key]);
}
