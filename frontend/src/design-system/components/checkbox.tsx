import * as React from 'react';

import { cn } from '@/lib/utils';

export type CheckboxProps = Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'>;

const Checkbox = React.forwardRef<HTMLInputElement, CheckboxProps>(
  ({ className, ...props }, ref) => (
    <input
      type="checkbox"
      ref={ref}
      className={cn(
        'size-5 shrink-0 appearance-none rounded border border-border-strong bg-surface',
        'checked:border-accent checked:bg-accent',
        'checked:bg-[center] checked:bg-no-repeat',
        "checked:bg-[url('data:image/svg+xml;utf8,<svg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 16 16%22 fill=%22none%22><path d=%22M3 8.5l3.5 3.5L13 4.5%22 stroke=%22var(--color-accent-foreground)%22 stroke-width=%222.5%22 stroke-linecap=%22round%22 stroke-linejoin=%22round%22/></svg>')]",
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
        'disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
      {...props}
    />
  ),
);
Checkbox.displayName = 'Checkbox';

export { Checkbox };
