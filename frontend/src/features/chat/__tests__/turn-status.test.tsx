import { render, screen } from '@testing-library/react';

import '@/i18n';
import { TurnStatusBadge } from '@/features/chat/components/turn-status-badge';
import {
  IDLE_TURN,
  STUCK_THRESHOLD_MS,
  deriveTurnStatus,
  formatElapsed,
  reduceChatTurn,
  type TurnStream,
} from '@/features/chat/lib/chat-derive';

function envelope(type: string, payload: unknown, occurredAt = '2026-07-17T12:00:00Z') {
  return { stream: 'conversation:x', sequence: 1, type, occurredAt, payload } as never;
}

describe('reduceChatTurn — estados granulares', () => {
  it('mantém o turno ativo em fases granulares e captura heartbeat/início', () => {
    const turn = reduceChatTurn(
      { ...IDLE_TURN, turnId: 't1', phase: 'processing' },
      envelope(
        'chief.turnStateChanged',
        {
          turnId: 't1',
          conversationId: 'c',
          projectId: 'p',
          state: 'thinking',
          lastActivityAt: '2026-07-17T12:00:05Z',
          activityStartedAt: '2026-07-17T12:00:04Z',
        },
        '2026-07-17T12:00:05Z',
      ),
    );
    expect(turn.turnId).toBe('t1');
    expect(turn.phase).toBe('thinking');
    expect(turn.lastActivityAt).toBe('2026-07-17T12:00:05Z');
    expect(turn.activityStartedAt).toBe('2026-07-17T12:00:04Z');
  });

  it('preserva o agente delegado e reseta ao concluir', () => {
    let turn = reduceChatTurn(
      { ...IDLE_TURN, turnId: 't1', phase: 'delegating' },
      envelope('chief.turnStateChanged', {
        turnId: 't1',
        conversationId: 'c',
        projectId: 'p',
        state: 'agent_working',
        agentName: 'Iara',
      }),
    );
    expect(turn.agentName).toBe('Iara');

    turn = reduceChatTurn(
      turn,
      envelope('chief.turnStateChanged', {
        turnId: 't1',
        conversationId: 'c',
        projectId: 'p',
        state: 'completed',
      }),
    );
    expect(turn).toEqual(IDLE_TURN);
  });

  it('renova o heartbeat a cada chunk', () => {
    const started = reduceChatTurn(
      IDLE_TURN,
      envelope('chat.turnStarted', { conversationId: 'c', turnId: 't1', agentId: 'a' }, '2026-07-17T12:00:00Z'),
    );
    const chunked = reduceChatTurn(
      started,
      envelope('chat.turnChunk', { conversationId: 'c', turnId: 't1', index: 0, text: 'oi' }, '2026-07-17T12:00:09Z'),
    );
    expect(chunked.lastActivityAt).toBe('2026-07-17T12:00:09Z');
    expect(chunked.phase).toBe('processing');
  });
});

describe('deriveTurnStatus', () => {
  const base: TurnStream = {
    ...IDLE_TURN,
    turnId: 't1',
    phase: 'thinking',
    activityStartedAt: '2026-07-17T12:00:00Z',
    lastActivityAt: '2026-07-17T12:00:00Z',
  };
  const startMs = Date.parse('2026-07-17T12:00:00Z');

  it('calcula elapsed para fases longas com heartbeat vivo (não travado)', () => {
    const nowMs = startMs + 12 * 60_000;
    // Heartbeat recente: trabalhando há 12min, mas vivo.
    const status = deriveTurnStatus(
      { ...base, lastActivityAt: new Date(nowMs - 2_000).toISOString() },
      nowMs,
    );
    expect(status?.elapsedMs).toBe(12 * 60_000);
    expect(status?.stuck).toBe(false);
  });

  it('sinaliza travado quando o heartbeat expira', () => {
    const status = deriveTurnStatus(base, startMs + STUCK_THRESHOLD_MS + 1_000);
    expect(status?.stuck).toBe(true);
  });

  it('não deriva status sem turno ativo', () => {
    expect(deriveTurnStatus(IDLE_TURN, startMs)).toBeNull();
  });
});

describe('formatElapsed', () => {
  it('formata segundos, minutos e horas', () => {
    expect(formatElapsed(45_000)).toBe('45s');
    expect(formatElapsed(12 * 60_000)).toBe('12min');
    expect(formatElapsed(2 * 3_600_000 + 5 * 60_000)).toBe('2h05');
  });
});

describe('TurnStatusBadge', () => {
  it('renderiza a tag da fase (pt-BR)', () => {
    const turn: TurnStream = { ...IDLE_TURN, turnId: 't1', phase: 'reading_context' };
    render(<TurnStatusBadge turn={turn} />);
    expect(screen.getByText(/lendo contexto/i)).toBeInTheDocument();
  });

  it('mostra o agente delegado e o cronômetro', () => {
    const turn: TurnStream = {
      ...IDLE_TURN,
      turnId: 't1',
      phase: 'agent_working',
      agentName: 'Iara',
      activityStartedAt: new Date(Date.now() - 12 * 60_000).toISOString(),
      lastActivityAt: new Date().toISOString(),
    };
    render(<TurnStatusBadge turn={turn} />);
    expect(screen.getByText(/delegado a Iara/i)).toBeInTheDocument();
    expect(screen.getByText(/\(12min\)/)).toBeInTheDocument();
  });

  it('resume o andamento no modo business sem expor agente ou cronômetro', () => {
    const turn: TurnStream = {
      ...IDLE_TURN,
      turnId: 't1',
      phase: 'agent_working',
      agentName: 'Iara',
      activityStartedAt: new Date(Date.now() - 12 * 60_000).toISOString(),
      lastActivityAt: new Date().toISOString(),
    };
    render(<TurnStatusBadge turn={turn} showTechnicalDetails={false} />);

    expect(screen.getByText('Equipe trabalhando')).toBeInTheDocument();
    expect(screen.queryByText(/Iara/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/12min/)).not.toBeInTheDocument();
  });

  it('exibe o aviso de travado quando não há heartbeat', () => {
    const turn: TurnStream = {
      ...IDLE_TURN,
      turnId: 't1',
      phase: 'thinking',
      activityStartedAt: new Date(Date.now() - 5 * 60_000).toISOString(),
      lastActivityAt: new Date(Date.now() - 5 * 60_000).toISOString(),
    };
    render(<TurnStatusBadge turn={turn} />);
    expect(screen.getByText(/possivelmente travado/i)).toBeInTheDocument();
  });
});
