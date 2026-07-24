import { useId, type ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';

import { cn } from '@/lib/utils';

export interface FeatureIntroProps {
  /** Ícone opcional exibido ao lado do título. */
  icon?: LucideIcon;
  /** Título já traduzido (o propósito da tela em uma frase). */
  title: ReactNode;
  /** Corpo explicativo já traduzido (o que a tela faz e para quê). */
  children: ReactNode;
  /**
   * Passos numerados de "como usar", já traduzidos. Renderizados como uma
   * lista ordenada quando presentes.
   */
  steps?: ReactNode[];
  /** Rótulo acessível da lista de passos (string traduzida). */
  stepsLabel?: string;
  /** Observação/limitação honesta ao final, já traduzida. */
  note?: ReactNode;
  className?: string;
}

/**
 * Bloco de propósito de uma feature: explica, em texto claro, para que a tela
 * serve e (opcionalmente) como usá-la. Padroniza o "empty-state explicativo"
 * pedido para telas que pareciam cruas. Todo o texto chega já traduzido pelos
 * chamadores — o componente não contém strings próprias.
 */
export function FeatureIntro({
  icon: Icon,
  title,
  children,
  steps,
  stepsLabel,
  note,
  className,
}: FeatureIntroProps) {
  const headingId = useId();

  return (
    <section
      aria-labelledby={headingId}
      className={cn(
        'flex flex-col gap-2 rounded-lg border border-border bg-surface p-4',
        className,
      )}
    >
      <div className="flex items-center gap-2">
        {Icon ? <Icon aria-hidden="true" className="size-5 shrink-0 text-brand-strong" /> : null}
        <h2 id={headingId} className="font-heading text-base font-semibold">
          {title}
        </h2>
      </div>
      <p className="text-sm text-foreground-muted">{children}</p>
      {steps && steps.length > 0 ? (
        <ol
          aria-label={stepsLabel}
          className="ml-5 flex list-decimal flex-col gap-1 text-sm text-foreground-muted marker:text-foreground-muted"
        >
          {steps.map((step, index) => (
            <li key={index}>{step}</li>
          ))}
        </ol>
      ) : null}
      {note ? <p className="text-xs text-foreground-muted">{note}</p> : null}
    </section>
  );
}
