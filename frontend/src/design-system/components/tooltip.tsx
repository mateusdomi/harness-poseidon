import * as React from 'react';

import { cn } from '@/lib/utils';

export interface TooltipProps {
  /** Texto exibido no tooltip (já traduzido pelo consumidor). */
  label: string;
  children: React.ReactNode;
  className?: string;
}

/**
 * Tooltip visual mínimo: aparece em hover e no foco de teclado do filho,
 * à direita do gatilho. Renderizado com `position: fixed` (medido no evento)
 * para não ser cortado por containers com overflow (ex.: nav rolável).
 * O balão é `aria-hidden` — o nome acessível já vem do filho (ex.: aria-label
 * do link), então leitores de tela não anunciam o texto duas vezes.
 */
export function Tooltip({ label, children, className }: TooltipProps) {
  const triggerRef = React.useRef<HTMLSpanElement>(null);
  const [position, setPosition] = React.useState<{ top: number; left: number } | null>(null);

  const show = () => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (rect) {
      setPosition({ top: rect.top + rect.height / 2, left: rect.right + 8 });
    }
  };
  const hide = () => setPosition(null);

  return (
    <span
      ref={triggerRef}
      className={cn('flex', className)}
      onMouseEnter={show}
      onMouseLeave={hide}
      onFocus={show}
      onBlur={hide}
    >
      {children}
      {position && (
        <span
          aria-hidden="true"
          style={{ top: position.top, left: position.left }}
          className={cn(
            'pointer-events-none fixed z-50 -translate-y-1/2 whitespace-nowrap',
            'rounded-md border border-border bg-surface-elevated px-2 py-1',
            'text-xs font-medium text-foreground shadow-card motion-safe:animate-fade-in',
          )}
        >
          {label}
        </span>
      )}
    </span>
  );
}
