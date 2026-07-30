import type { ReactNode } from 'react';

import { BackLink, type BackLinkProps } from '@/features/shared/components/back-link';
import { Breadcrumb, type Crumb } from '@/features/shared/components/breadcrumb';
import { cn } from '@/lib/utils';

export interface PageHeaderProps {
  /** Título já traduzido. */
  title: ReactNode;
  /** Subtítulo/descrição opcional já traduzida. */
  description?: ReactNode;
  /** Trilha de navegação (desktop). */
  breadcrumb?: Crumb[];
  /** Botão "Voltar" (telas de criação/edição/detalhe/wizard/deep link). */
  back?: BackLinkProps;
  /** Ações à direita do título (ex.: CTA primária única). */
  actions?: ReactNode;
  className?: string;
}

/**
 * Cabeçalho de página compartilhado: trilha + "Voltar" + título + ações.
 * Padroniza o padrão antes duplicado inline em cada tela e concentra o
 * comportamento transversal de navegação exigido em criação/edição/detalhe.
 */
export function PageHeader({
  title,
  description,
  breadcrumb,
  back,
  actions,
  className,
}: PageHeaderProps) {
  return (
    <div
      className={cn(
        'poseidon-hero flex flex-col gap-3 rounded-xl border border-border px-5 py-5 shadow-glow md:px-6',
        className,
      )}
    >
      {breadcrumb && breadcrumb.length > 0 ? <Breadcrumb items={breadcrumb} /> : null}
      {back ? <BackLink {...back} /> : null}
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex flex-col gap-1">
          <h1 className="font-heading text-3xl font-bold tracking-tightest text-balance">{title}</h1>
          {description ? <p className="max-w-3xl text-sm text-foreground-muted">{description}</p> : null}
        </div>
        {actions ? <div className="ml-auto flex items-center gap-2">{actions}</div> : null}
      </div>
    </div>
  );
}
