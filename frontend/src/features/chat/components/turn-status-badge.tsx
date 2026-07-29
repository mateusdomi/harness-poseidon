import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Badge } from '@/design-system';
import {
  deriveTurnStatus,
  formatElapsed,
  isTurnActive,
  type TurnStream,
} from '@/features/chat/lib/chat-derive';
import { chiefTurnStateVariant } from '@/lib/status';

/**
 * Tag/badge de status no balão do Chefe: mostra, em tempo real, a fase granular
 * do turno (🧠 pensando, 📖 lendo contexto, 🤝 delegando…), com cronômetro de
 * elapsed para fases longas e sinal claro de "travado" (sem heartbeat há X) vs
 * "ativo". Rótulos 100% via i18n (pt-BR + en).
 */

export function TurnStatusBadge({
  turn,
  showTechnicalDetails = true,
}: {
  turn: TurnStream;
  showTechnicalDetails?: boolean;
}) {
  const { t } = useTranslation();
  const [nowMs, setNowMs] = useState(() => Date.now());

  const active = isTurnActive(turn);
  useEffect(() => {
    if (!active) return;
    // Cronômetro vivo: 1s é suficiente para elapsed e para virar "travado".
    const id = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, [active, turn.phase, turn.turnId]);

  const status = deriveTurnStatus(turn, nowMs);
  if (status === null) return null;

  const label =
    status.phase === 'agent_working'
      ? t('chat.turn.states.agent_working', {
          agent: status.agentName ?? t('chat.turn.agentFallback'),
        })
      : t(`chat.turn.states.${status.phase}`);

  const variant = status.stuck ? 'warning' : chiefTurnStateVariant(status.phase);
  const elapsedText = status.elapsedMs !== null ? formatElapsed(status.elapsedMs) : null;

  if (!showTechnicalDetails) {
    const businessState =
      status.stuck || status.phase === 'blocked' || status.phase === 'failed'
        ? 'attention'
        : status.phase === 'awaiting_review'
          ? 'review'
          : status.phase === 'pending' || status.phase === 'received'
            ? 'received'
            : 'working';
    const businessVariant = {
      attention: 'warning',
      review: 'warning',
      received: 'info',
      working: 'info',
    } as const;

    return (
      <span role="status" aria-live="polite">
        <Badge variant={businessVariant[businessState]}>
          {t(`chat.turn.businessStates.${businessState}`)}
        </Badge>
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-1.5" role="status" aria-live="polite">
      <Badge variant={variant}>
        <span>{label}</span>
        {elapsedText !== null && (
          <span aria-label={t('chat.turn.elapsedLabel', { elapsed: elapsedText })}>
            ({elapsedText})
          </span>
        )}
      </Badge>
      {status.stuck && (
        <span className="inline-flex items-center gap-1 text-xs font-medium text-warning">
          <span aria-hidden="true">⚠️</span>
          {t('chat.turn.stuck')}
        </span>
      )}
    </span>
  );
}
