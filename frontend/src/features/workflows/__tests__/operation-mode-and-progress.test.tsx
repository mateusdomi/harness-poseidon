import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import '@/i18n';

import {
  OperationModeSelector,
  type ProjectOperationMode,
} from '@/features/workflows/components/operation-mode-selector';
import { PhaseProgressPanel } from '@/features/workflows/components/phase-progress-panel';

const PHASES = [
  '1-Triagem',
  '2-Descoberta',
  '3-Arquitetura',
  '4-Planejamento',
  '5-Desenvolvimento',
  '6-Testes',
  '7-Homologação',
  '8-Release',
  '9-Sustentação',
];

describe('OperationModeSelector', () => {
  it('oferece os três modos e nenhum deles promete revisão de código ou de card', async () => {
    const onModeChange = vi.fn();
    render(
      <OperationModeSelector
        mode="autonomous"
        phases={PHASES}
        pausedPhases={[]}
        onModeChange={onModeChange}
        onPausedPhasesChange={vi.fn()}
      />,
    );

    expect(screen.getByLabelText(/Autônomo/)).toBeChecked();
    // O texto é o contrato com o dono: em nenhum modo ele revisa código ou card.
    expect(screen.queryByText(/revisar o código/i)).not.toBeInTheDocument();
    expect(screen.getByText(/avança as fases quando as evidências forem aceitas/i)).toBeVisible();
    expect(screen.getByText(/Toda transição de fase aguarda sua aprovação/i)).toBeVisible();

    await userEvent.click(screen.getByLabelText(/Manual/));
    expect(onModeChange).toHaveBeenCalledWith<[ProjectOperationMode]>('manual');
  });

  it('só oferece a escolha de fases no semiautônomo', () => {
    const { rerender } = render(
      <OperationModeSelector
        mode="autonomous"
        phases={PHASES}
        pausedPhases={[]}
        onModeChange={vi.fn()}
        onPausedPhasesChange={vi.fn()}
      />,
    );
    expect(screen.queryByRole('button', { name: /3-Arquitetura/ })).not.toBeInTheDocument();

    rerender(
      <OperationModeSelector
        mode="semiautonomous"
        phases={PHASES}
        pausedPhases={['3-Arquitetura']}
        onModeChange={vi.fn()}
        onPausedPhasesChange={vi.fn()}
      />,
    );
    expect(screen.getByRole('button', { name: /3-Arquitetura/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByRole('button', { name: /5-Desenvolvimento/ })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
  });

  it('marcar e desmarcar uma fase devolve a lista completa, não um delta', async () => {
    const onPausedPhasesChange = vi.fn();
    render(
      <OperationModeSelector
        mode="semiautonomous"
        phases={PHASES}
        pausedPhases={['3-Arquitetura']}
        onModeChange={vi.fn()}
        onPausedPhasesChange={onPausedPhasesChange}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /7-Homologação/ }));
    expect(onPausedPhasesChange).toHaveBeenCalledWith(['3-Arquitetura', '7-Homologação']);

    await userEvent.click(screen.getByRole('button', { name: /3-Arquitetura/ }));
    expect(onPausedPhasesChange).toHaveBeenLastCalledWith([]);
  });

  it('semiautônomo sem fase marcada avisa que o projeto se comporta como autônomo', () => {
    render(
      <OperationModeSelector
        mode="semiautonomous"
        phases={PHASES}
        pausedPhases={[]}
        onModeChange={vi.fn()}
        onPausedPhasesChange={vi.fn()}
      />,
    );
    expect(screen.getByText(/se comporta como Autônomo/i)).toBeVisible();
  });
});

describe('PhaseProgressPanel', () => {
  it('separa trabalho aceito de trabalho em voo', () => {
    render(
      <PhaseProgressPanel
        phaseName="5-Desenvolvimento"
        planVersion={2}
        percentage={40}
        requiredTotal={5}
        requiredAccepted={2}
        inProgress={2}
        inReview={1}
        blocked={0}
        pending={0}
        gate="not_ready"
      />,
    );

    expect(screen.getByText('40%')).toBeVisible();
    expect(screen.getByText(/2 de 5 obrigações aceitas/)).toBeVisible();
    // Em execução e em revisão aparecem como situação, não como conclusão.
    expect(screen.getByText(/Em execução: 2/)).toBeVisible();
    expect(screen.getByText(/Em revisão: 1/)).toBeVisible();
    expect(screen.getByText(/Ainda não pronta/)).toBeVisible();
  });

  it('100% convive com aprovação humana pendente', () => {
    // O ponto do desenho: progresso é o que a equipe entregou; aprovação é outro eixo. Manter a
    // fase em 99% por causa do humano mentiria sobre o trabalho feito.
    render(
      <PhaseProgressPanel
        phaseName="1-Triagem"
        planVersion={1}
        percentage={100}
        requiredTotal={2}
        requiredAccepted={2}
        inProgress={0}
        inReview={0}
        blocked={0}
        pending={0}
        gate="awaiting_human"
      />,
    );

    expect(screen.getByText('100%')).toBeVisible();
    expect(screen.getByText(/Aguardando sua aprovação/)).toBeVisible();
  });

  it('obrigação bloqueada aparece explicitamente', () => {
    render(
      <PhaseProgressPanel
        phaseName="6-Testes"
        planVersion={1}
        percentage={50}
        requiredTotal={4}
        requiredAccepted={2}
        inProgress={0}
        inReview={0}
        blocked={1}
        pending={1}
        gate="not_ready"
      />,
    );

    expect(screen.getByText(/Bloqueadas: 1/)).toBeVisible();
  });
});
