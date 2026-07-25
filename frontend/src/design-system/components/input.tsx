import * as React from 'react';

import { cn } from '@/lib/utils';

export type InputProps = React.InputHTMLAttributes<HTMLInputElement>;

const Input = React.forwardRef<HTMLInputElement, InputProps>(({ className, type, ...props }, ref) => (
  <input
    type={type}
    ref={ref}
    className={cn(
      'flex h-11 w-full min-w-0 max-w-full rounded-md border border-border-strong bg-surface px-3 py-2 text-sm text-foreground',
      'placeholder:text-foreground-muted',
      'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand',
      'disabled:cursor-not-allowed disabled:opacity-50',
      className,
    )}
    {...props}
  />
));
Input.displayName = 'Input';

export { Input };
