import { cva } from 'class-variance-authority';

export const buttonVariants = cva(
  'inline-flex min-h-touch items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium motion-safe:transition-colors motion-safe:duration-fast focus-visible:outline-none disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4 [&_svg]:shrink-0',
  {
    variants: {
      variant: {
        // Ação primária (CTA) — gradiente violeta→magenta da marca (tokens AA).
        // Hover/active via overlay (::before atrás do conteúdo): background-image
        // não transiciona, então a troca de gradiente é uma fade de opacidade.
        primary:
          'relative isolate overflow-hidden bg-primary bg-[image:var(--gradient-primary)] text-primary-foreground before:absolute before:inset-0 before:-z-10 before:content-[""] before:bg-[image:var(--gradient-primary-hover)] before:opacity-0 motion-safe:before:transition-opacity motion-safe:before:duration-base hover:before:opacity-100 active:before:bg-[image:var(--gradient-primary-active)]',
        brand: 'bg-primary text-primary-foreground hover:bg-primary-hover',
        secondary: 'bg-surface-elevated text-foreground hover:bg-surface-elevated/70',
        outline: 'border border-border-strong bg-transparent text-foreground hover:bg-surface',
        ghost: 'text-foreground hover:bg-surface',
        destructive: 'bg-error text-error-foreground hover:bg-error/90',
        link: 'min-h-0 text-brand-strong underline-offset-4 hover:underline',
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
