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

    const handle = (event: EventEnvelope) => {
      const check = tracker.check(event);
      if (check === 'duplicate') return;
      if (check === 'gap') {
        // Lacuna de sequence: re-sync snapshot+delta. Eventos do snapshot
        // voltam por `apply` direto (já passaram pelo log ordenado).
        void realtime.getSnapshot(event.stream).then((snapshot) => {
          for (const past of snapshot) {
            if (tracker.check(past) !== 'duplicate') apply(past);
          }
          for (const queryKey of optionsRef.current.invalidate ?? []) {
            void queryClient.invalidateQueries({ queryKey });
          }
        });
        return;
      }
      apply(event);
    };

    const subscription = realtime.subscribe(streams, handle);
    return () => subscription.unsubscribe();
  }, [realtime, queryClient, key]);
}
