import { cva } from 'class-variance-authority';

export const buttonVariants = cva(
  'inline-flex min-h-touch items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition-colors focus-visible:outline-none disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4 [&_svg]:shrink-0',
  {
    variants: {
      variant: {
        // Ação principal (CTA) — verde-limão, não é cor de sucesso.
        primary: 'bg-accent text-accent-foreground hover:bg-accent/90',
        brand: 'bg-brand text-white hover:bg-brand/90',
        secondary: 'bg-surface-elevated text-foreground hover:bg-surface-elevated/70',
        outline: 'border border-border-strong bg-transparent text-foreground hover:bg-surface',
        ghost: 'text-foreground hover:bg-surface',
        destructive: 'bg-error text-error-foreground hover:bg-error/90',
        link: 'min-h-0 text-brand underline-offset-4 hover:underline',
      },
      size: {
        sm: 'h-9 min-h-touch px-3 text-xs',
        md: 'h-10 px-4',
        lg: 'h-12 px-6 text-base',
        icon: 'size-11 p-0',
      },
    },
    defaultVariants: {
      variant: 'primary',
      size: 'md',
    },
  },
);
