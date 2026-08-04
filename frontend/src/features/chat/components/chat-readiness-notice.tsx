import { AlertTriangle, ArrowRight, CheckCircle2, Circle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import { Button } from '@/design-system';

export interface ChatReadinessNoticeProps {
  hasProvider: boolean;
  hasModel: boolean;
  hasWorkflow: boolean;
  /** O read model canônico pode bloquear por outros motivos, como agente degradado. */
  executionBlocked?: boolean;
  /**
   * Para onde a ação leva no modo Negócio. Vem do `nextAction` do read model — a MESMA fonte que
   * decidiu bloquear. Enquanto era `/onboarding` fixo, o botão prometia resolver e entregava a
   * tela inicial: o dono clicava, era jogado no começo, e voltava sem saber o que faltava.
   * Ausente, o botão não é oferecido — botão que não resolve nada é pior que botão nenhum.
   */
  resolutionRoute?: string | null;
  showTechnicalDetails: boolean;
}

const ROUTES = {
  provider: '/providers',
  model: '/providers?tab=models',
  workflow: '/workflows',
} as const;

/**
 * Explica por que a execução do Chief está bloqueada e o que falta (§13).
 * Bloqueamos apenas a EXECUÇÃO — nunca escondemos o motivo, e cada
 * pré-requisito tem uma CTA única para a tela correta.
 */
export function ChatReadinessNotice({
  hasProvider,
  hasModel,
  hasWorkflow,
  executionBlocked = false,
  resolutionRoute = null,
  showTechnicalDetails,
}: ChatReadinessNoticeProps) {
  const { t } = useTranslation();
  const items = [
    { id: 'provider' as const, done: hasProvider },
    { id: 'model' as const, done: hasModel },
    { id: 'workflow' as const, done: hasWorkflow },
  ];
  const firstMissing = items.find((item) => !item.done);
  if (!firstMissing && !executionBlocked) return null;

  if (!showTechnicalDetails) {
    return (
      <section
        aria-labelledby="chat-readiness-title"
        className="flex flex-col gap-3 rounded-xl border border-warning/40 bg-warning/5 p-4"
      >
        <div className="flex items-center gap-2">
          <AlertTriangle aria-hidden="true" className="size-4 shrink-0 text-warning" />
          <h2 id="chat-readiness-title" className="text-sm font-semibold">
            {t('chat.readiness.businessTitle')}
          </h2>
        </div>
        <p className="text-sm text-foreground-muted">{t('chat.readiness.businessBody')}</p>
        {resolutionRoute ? (
          <Button asChild size="sm" className="self-start">
            <Link to={resolutionRoute}>
              {t('chat.readiness.actions.reviewConfiguration')}
              <ArrowRight aria-hidden="true" className="size-4" />
            </Link>
          </Button>
        ) : null}
      </section>
    );
  }

  return (
    <section
      aria-labelledby="chat-readiness-title"
      className="flex flex-col gap-3 rounded-xl border border-warning/40 bg-warning/5 p-4"
    >
      <div className="flex items-center gap-2">
        <AlertTriangle aria-hidden="true" className="size-4 shrink-0 text-warning" />
        <h2 id="chat-readiness-title" className="text-sm font-semibold">
          {t('chat.readiness.title')}
        </h2>
      </div>
      <p className="text-sm text-foreground-muted">
        {firstMissing ? t('chat.readiness.body') : t('chat.readiness.additionalBlocker')}
      </p>
      <ul className="flex flex-col gap-1.5">
        {items.map((item) => (
          <li key={item.id} className="flex items-center gap-2 text-sm">
            {item.done ? (
              <CheckCircle2 aria-hidden="true" className="size-4 shrink-0 text-success" />
            ) : (
              <Circle aria-hidden="true" className="size-4 shrink-0 text-foreground-muted" />
            )}
            <span className="sr-only">
              {t(item.done ? 'goldenPath.status.done' : 'goldenPath.status.pending')}
            </span>
            <span className={item.done ? 'text-foreground-muted line-through' : undefined}>
              {t(`chat.readiness.items.${item.id}`)}
            </span>
          </li>
        ))}
      </ul>
      <Button asChild size="sm" className="self-start">
        <Link to={firstMissing ? ROUTES[firstMissing.id] : '/orchestrator'}>
          {firstMissing
            ? t(`chat.readiness.actions.${firstMissing.id}`)
            : t('chat.readiness.actions.diagnostics')}
          <ArrowRight aria-hidden="true" className="size-4" />
        </Link>
      </Button>
    </section>
  );
}
