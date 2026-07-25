import * as React from 'react';

import { cn } from '@/lib/utils';

export interface FieldProps {
  /** id do controle — liga o <label htmlFor>. */
  htmlFor: string;
  label: React.ReactNode;
  hint?: React.ReactNode;
  /** Mensagem de erro já traduzida (i18n fica na camada de feature). */
  error?: string;
  /** Marca o campo como obrigatório no label. */
  required?: boolean;
  requiredLabel?: string;
  className?: string;
  children: React.ReactNode;
}

/**
 * Envoltório de campo de formulário: label associado, hint e erro com
 * `role="alert"`. Componentes de feature passam textos já traduzidos.
 */
export function Field({
  htmlFor,
  label,
  hint,
  error,
  required = false,
  requiredLabel,
  className,
  children,
}: FieldProps) {
  const errorId = `${htmlFor}-error`;
  const hintId = `${htmlFor}-hint`;

  return (
    <div className={cn('flex min-w-0 flex-col gap-1.5', className)}>
      <label htmlFor={htmlFor} className="text-sm font-medium text-foreground">
        {label}
        {required && requiredLabel ? (
          <span className="ml-1 text-foreground-muted">{requiredLabel}</span>
        ) : null}
      </label>
      {children}
      {hint && !error ? (
        <p id={hintId} className="break-words text-xs text-foreground-muted">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} role="alert" className="text-xs text-error">
          {error}
        </p>
      ) : null}
    </div>
  );
}
