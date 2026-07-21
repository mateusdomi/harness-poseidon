import * as React from 'react';
import { Check, Minus } from 'lucide-react';

import { cn } from '@/lib/utils';

export interface CheckboxProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'> {
  /**
   * Estado "misto" (ex.: selecionar-todos parcial). Reflete a propriedade
   * nativa `indeterminate` no DOM — o leitor de tela anuncia "mixed" — e
   * exibe um traço em vez do check.
   */
  indeterminate?: boolean;
}

/**
 * Checkbox do design system.
 *
 * É um `input[type=checkbox]` real (semântica, teclado e leitor de tela
 * nativos) com `appearance-none`. O indicador marcado/misto é um SVG
 * renderizado no DOM e sobreposto ao quadrado — NÃO um `background-image`
 * com `var()` (custom properties não resolvem dentro de data-URI de SVG,
 * o que deixava o check invisível). O estado marcado combina preenchimento
 * accent + ícone de check, de modo que a informação não depende só da cor.
 */
const Checkbox = React.forwardRef<HTMLInputElement, CheckboxProps>(
  ({ className, indeterminate = false, ...props }, ref) => {
    const innerRef = React.useRef<HTMLInputElement | null>(null);

    React.useImperativeHandle(ref, () => innerRef.current as HTMLInputElement);

    React.useEffect(() => {
      if (innerRef.current) {
        innerRef.current.indeterminate = indeterminate;
      }
    }, [indeterminate]);

    return (
      <span className="relative inline-flex size-5 shrink-0">
        <input
          type="checkbox"
          ref={innerRef}
          data-indeterminate={indeterminate ? '' : undefined}
          className={cn(
            'peer size-5 shrink-0 appearance-none rounded border border-border-strong bg-surface',
            'motion-safe:transition-colors motion-safe:duration-fast',
            'hover:border-accent',
            'checked:border-accent checked:bg-accent',
            'data-[indeterminate]:border-accent data-[indeterminate]:bg-accent',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand focus-visible:ring-offset-2 focus-visible:ring-offset-background',
            'active:border-accent',
            'aria-[invalid=true]:border-error aria-[invalid=true]:focus-visible:ring-error',
            'disabled:cursor-not-allowed disabled:opacity-50',
            className,
          )}
          {...props}
        />
        {/* Check: visível apenas quando marcado e não-indeterminado. */}
        <Check
          aria-hidden="true"
          strokeWidth={3}
          className={cn(
            'pointer-events-none absolute left-1/2 top-1/2 size-3.5 -translate-x-1/2 -translate-y-1/2',
            'text-accent-foreground opacity-0',
            'peer-checked:opacity-100 peer-data-[indeterminate]:opacity-0',
          )}
        />
        {/* Traço: visível apenas no estado misto (indeterminate). */}
        <Minus
          aria-hidden="true"
          strokeWidth={3}
          className={cn(
            'pointer-events-none absolute left-1/2 top-1/2 size-3.5 -translate-x-1/2 -translate-y-1/2',
            'text-accent-foreground opacity-0',
            'peer-data-[indeterminate]:opacity-100',
          )}
        />
      </span>
    );
  },
);
Checkbox.displayName = 'Checkbox';

export { Checkbox };
