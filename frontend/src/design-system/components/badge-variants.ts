import { cva } from 'class-variance-authority';

export const badgeVariants = cva(
  'inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs font-medium transition-colors',
  {
    variants: {
      variant: {
        default: 'border-border-strong bg-surface-elevated text-foreground',
        brand: 'border-transparent bg-primary text-primary-foreground',
        accent: 'border-transparent bg-accent text-accent-foreground',
        success: 'border-transparent bg-success text-success-foreground',
        warning: 'border-transparent bg-warning text-warning-foreground',
        error: 'border-transparent bg-error text-error-foreground',
        info: 'border-transparent bg-info text-info-foreground',
        // Baixa ênfase ainda precisa ser reconhecível como tag em qualquer tema.
        // O azul semântico translúcido preserva contraste sem competir com estados
        // fortes (success/warning/error).
        outline: 'border-info/50 bg-info/10 text-info',
      },
    },
    defaultVariants: {
      variant: 'default',
    },
  },
);
