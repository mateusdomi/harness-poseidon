import { useTranslation } from 'react-i18next';
import { OctagonAlert } from 'lucide-react';

import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { taskStateLabelKey } from '@/features/board/lib/board-presentation';

/** Ordem linear do fluxo (Bloqueada é TRANSVERSAL — fica de fora de propósito). */
const FLOW_STEPS = [
  'backlog',
  'ready',
  'development',
  'review',
  'corrections',
  'testsGates',
  'done',
] as const;

/** Ações humanas já implementadas no detalhe da tarefa. */
const HUMAN_ACTIONS = ['priority', 'pause', 'cancel', 'requestReview', 'gate'] as const;

/** Transições conduzidas por Bruna e pelos agentes. */
const AGENT_ACTIONS = ['create', 'move', 'execute', 'unblock'] as const;

export interface BoardFlowDialogProps {
  showTechnicalDetails?: boolean;
  onClose: () => void;
}

/**
 * "Como o trabalho flui" (FR-3, item 7.4): stepper acessível da ordem das
 * colunas com "Bloqueada" representada como transversal (pode ocorrer a
 * partir de qualquer coluna e retoma para a coluna de origem — sem mentir
 * linearidade) + quem faz cada transição (humano vs. chefe/agentes), texto
 * derivado das regras já implementadas nas ações da tarefa.
 */
export function BoardFlowDialog({
  showTechnicalDetails = true,
  onClose,
}: BoardFlowDialogProps) {
  const { t } = useTranslation();
  const flowMode = showTechnicalDetails ? 'technical' : 'business';

  return (
    <ModalDialog label={t('board.flow.title')} onClose={onClose} className="max-w-xl">
      <h2 className="font-heading text-lg font-semibold">{t('board.flow.title')}</h2>
      <p className="text-sm text-foreground-muted">
        {t(`board.flow.${flowMode}.intro`)}
      </p>

      <ol className="flex flex-col gap-1.5">
        {FLOW_STEPS.map((state, index) => (
          <li key={state} className="flex items-center gap-3">
            <span
              aria-hidden="true"
              className="flex size-6 shrink-0 items-center justify-center rounded-full bg-primary/10 text-xs font-semibold text-brand-strong"
            >
              {index + 1}
            </span>
            <span className="flex min-w-0 flex-col">
              <span className="text-sm font-medium">
                {t(taskStateLabelKey(state, showTechnicalDetails))}
              </span>
              <span className="text-xs text-foreground-muted">
                {t(`board.flow.${flowMode}.stages.${state}`)}
              </span>
            </span>
          </li>
        ))}
      </ol>

      <p className="flex items-start gap-2 rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
        <OctagonAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-error" />
        <span>
          <strong className="font-semibold text-foreground">
            {t(taskStateLabelKey('blocked', showTechnicalDetails))}
          </strong>{' '}
          {t(`board.flow.${flowMode}.blockedNote`)}
        </span>
      </p>

      <div className="grid gap-3 sm:grid-cols-2">
        <section aria-labelledby="board-flow-human" className="flex flex-col gap-1.5">
          <h3 id="board-flow-human" className="text-sm font-semibold">
            {t(`board.flow.${flowMode}.humanTitle`)}
          </h3>
          <ul className="list-inside list-disc text-xs text-foreground-muted">
            {HUMAN_ACTIONS.map((action) => (
              <li key={action}>{t(`board.flow.${flowMode}.human.${action}`)}</li>
            ))}
          </ul>
        </section>
        <section aria-labelledby="board-flow-agents" className="flex flex-col gap-1.5">
          <h3 id="board-flow-agents" className="text-sm font-semibold">
            {t(`board.flow.${flowMode}.agentsTitle`)}
          </h3>
          <ul className="list-inside list-disc text-xs text-foreground-muted">
            {AGENT_ACTIONS.map((action) => (
              <li key={action}>{t(`board.flow.${flowMode}.agents.${action}`)}</li>
            ))}
          </ul>
        </section>
      </div>
    </ModalDialog>
  );
}
