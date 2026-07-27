import { useTranslation } from 'react-i18next';

import { Badge, Card, CardContent } from '@/design-system';

export type PhaseGateStatus =
  | 'not_ready'
  | 'technically_ready'
  | 'approved_by_chief'
  | 'awaiting_human'
  | 'rejected'
  | 'advanced';

export interface PhaseProgressPanelProps {
  readonly phaseName: string;
  readonly planVersion: number;
  readonly percentage: number;
  readonly requiredTotal: number;
  readonly requiredAccepted: number;
  readonly inProgress: number;
  readonly inReview: number;
  readonly blocked: number;
  readonly pending: number;
  readonly gate: PhaseGateStatus;
}

/**
 * Progresso da fase, com os três eixos SEPARADOS.
 *
 * A interface vinha misturando o que a esteira já separava: o trabalho ACEITO (o percentual), o
 * trabalho EM VOO (situação operacional) e a DECISÃO do portão. Card criado, atribuído, em execução
 * ou em revisão é trabalho em andamento — aparece aqui como número próprio e nunca infla a
 * conclusão.
 *
 * E 100% convive com "aguardando aprovação": progresso é o que a equipe entregou, aprovação é
 * outro eixo. Manter a fase em 99% por causa do humano mentiria sobre o trabalho feito.
 */
export function PhaseProgressPanel({
  phaseName,
  planVersion,
  percentage,
  requiredTotal,
  requiredAccepted,
  inProgress,
  inReview,
  blocked,
  pending,
  gate,
}: PhaseProgressPanelProps) {
  const { t } = useTranslation();

  return (
    <Card>
      <CardContent className="flex flex-col gap-4 p-4">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h3 className="text-base font-medium text-foreground">{phaseName}</h3>
          <span className="text-xs text-muted-foreground">
            {t('workflows.phase.planVersion', { defaultValue: `Plano v${planVersion}` })}
          </span>
        </div>

        <div className="flex flex-col gap-1">
          <div className="flex items-baseline justify-between">
            <span className="text-sm text-muted-foreground">
              {t('workflows.phase.workDone', { defaultValue: 'Trabalho concluído' })}
            </span>
            <span className="text-2xl font-semibold tabular-nums text-foreground">
              {percentage.toFixed(0)}%
            </span>
          </div>
          <div
            className="h-2 w-full overflow-hidden rounded-full bg-surface-elevated"
            role="progressbar"
            aria-valuenow={Math.round(percentage)}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-label={phaseName}
          >
            <div
              className="h-full rounded-full bg-primary transition-[width]"
              style={{ width: `${Math.min(100, Math.max(0, percentage))}%` }}
            />
          </div>
          <span className="text-xs text-muted-foreground">
            {t('workflows.phase.obligations', {
              defaultValue: `${requiredAccepted} de ${requiredTotal} obrigações aceitas`,
            })}
          </span>
        </div>

        {/* Em voo: situação operacional, deliberadamente fora do percentual. */}
        <div className="flex flex-wrap gap-2">
          <Badge variant="outline">
            {t('workflows.phase.inProgress', { defaultValue: `Em execução: ${inProgress}` })}
          </Badge>
          <Badge variant="outline">
            {t('workflows.phase.inReview', { defaultValue: `Em revisão: ${inReview}` })}
          </Badge>
          <Badge variant="outline">
            {t('workflows.phase.pending', { defaultValue: `Não iniciadas: ${pending}` })}
          </Badge>
          {blocked > 0 ? (
            <Badge variant="error">
              {t('workflows.phase.blocked', { defaultValue: `Bloqueadas: ${blocked}` })}
            </Badge>
          ) : null}
        </div>

        <div className="flex flex-col gap-1 rounded-lg border border-border p-3">
          <span className="text-sm text-muted-foreground">
            {t('workflows.phase.transition', { defaultValue: 'Transição de fase' })}
          </span>
          <Badge variant={gateVariant(gate)}>{gateLabel(gate)}</Badge>
        </div>
      </CardContent>
    </Card>
  );
}

function gateVariant(gate: PhaseGateStatus): 'success' | 'warning' | 'error' | 'outline' {
  switch (gate) {
    case 'approved_by_chief':
    case 'advanced':
      return 'success';
    case 'awaiting_human':
      return 'warning';
    case 'rejected':
      return 'error';
    default:
      return 'outline';
  }
}

function gateLabel(gate: PhaseGateStatus): string {
  switch (gate) {
    case 'technically_ready':
      return 'Tecnicamente pronta';
    case 'approved_by_chief':
      return 'Aprovada pela Bruna';
    case 'awaiting_human':
      return 'Aguardando sua aprovação';
    case 'rejected':
      return 'Rejeitada — em correção';
    case 'advanced':
      return 'Fase avançada';
    default:
      return 'Ainda não pronta';
  }
}
