import type { ReactNode } from 'react';
import { ChevronDown, type LucideIcon } from 'lucide-react';

import { cn } from '@/lib/utils';

export interface CollapsibleSectionProps {
  /** Título visível no cabeçalho da seção. */
  title: string;
  /** Resumo curto (ex.: contagem) exibido ao lado do título. */
  summary?: string;
  /** Ícone opcional à esquerda do título. */
  icon?: LucideIcon;
  /** Começa aberta? (padrão: recolhida — conteúdo secundário). */
  defaultOpen?: boolean;
  className?: string;
  children: ReactNode;
}

/**
 * Seção recolhível (`<details>`/`<summary>`) para conteúdo secundário —
 * mantém a hierarquia da tela limpa sem esconder informação do DOM.
 * O `<summary>` é acessível por teclado nativamente; o marcador padrão é
 * suprimido em favor de um chevron que gira ao abrir.
 */
export function CollapsibleSection({
  title,
  summary,
  icon: Icon,
  defaultOpen = false,
  className,
  children,
}: CollapsibleSectionProps) {
  return (
    <details
      open={defaultOpen}
      className={cn('group rounded-lg border border-border bg-surface', className)}
    >
      <summary className="flex cursor-pointer list-none items-center gap-3 rounded-lg p-4 [&::-webkit-details-marker]:hidden focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent">
        {Icon && <Icon aria-hidden="true" className="size-5 shrink-0 text-foreground-muted" />}
        <span className="font-heading text-base font-semibold">{title}</span>
        {summary && (
          <span className="truncate text-sm text-foreground-muted">{summary}</span>
        )}
        <ChevronDown
          aria-hidden="true"
          className="ml-auto size-5 shrink-0 text-foreground-muted transition-transform group-open:rotate-180"
        />
      </summary>
      <div className="border-t border-border p-4">{children}</div>
    </details>
  );
}
