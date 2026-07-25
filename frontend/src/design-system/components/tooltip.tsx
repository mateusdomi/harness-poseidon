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
  const [position, setPosition] = React.useState<{
    top: number;
    left: number;
    placement: 'above' | 'below';
  } | null>(null);

  const show = () => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (rect) {
      const viewportWidth = window.innerWidth;
      const tooltipWidth = Math.min(320, viewportWidth - 16);
      const centeredLeft = rect.left + rect.width / 2 - tooltipWidth / 2;
      const left = Math.max(8, Math.min(centeredLeft, viewportWidth - tooltipWidth - 8));
      const placement = rect.top > 176 ? 'above' : 'below';
      setPosition({
        top: placement === 'above' ? rect.top - 8 : rect.bottom + 8,
        left,
        placement,
      });
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
      onClick={() => (position ? hide() : show())}
      onKeyDown={(event) => {
        if (event.key === 'Escape') hide();
      }}
    >
      {children}
      {position && (
        <span
          role="tooltip"
          style={{ top: position.top, left: position.left }}
          className={cn(
            'fixed z-50 w-[min(20rem,calc(100vw-1rem))]',
            position.placement === 'above' && '-translate-y-full',
            'max-h-[min(20rem,calc(100vh-1rem))] overflow-y-auto whitespace-pre-line break-words',
            'rounded-md border border-border bg-surface-elevated px-3 py-2',
            'text-left text-xs font-medium leading-relaxed text-foreground shadow-card motion-safe:animate-fade-in',
          )}
        >
          {label}
        </span>
      )}
    </span>
  );
}
