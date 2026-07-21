import { ChevronRight } from 'lucide-react';
import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import { cn } from '@/lib/utils';

export interface Crumb {
  /** Rótulo já traduzido. */
  label: string;
  /** Rota do item; ausente no item atual (último). */
  to?: string;
}

export interface BreadcrumbProps {
  items: Crumb[];
  className?: string;
}

/**
 * Trilha de navegação (desktop). O último item é a página atual
 * (`aria-current="page"`, sem link). Itens intermediários são links
 * acessíveis por teclado. Oculto em telas pequenas para não competir com o
 * botão "Voltar".
 */
export function Breadcrumb({ items, className }: BreadcrumbProps) {
  const { t } = useTranslation();
  if (items.length === 0) return null;

  return (
    <nav
      aria-label={t('common.breadcrumb.label')}
      className={cn('hidden md:block', className)}
    >
      <ol className="flex flex-wrap items-center gap-1 text-xs text-foreground-muted">
        {items.map((item, index) => {
          const isLast = index === items.length - 1;
          return (
            <Fragment key={`${item.label}-${index}`}>
              <li className="flex items-center">
                {item.to && !isLast ? (
                  <Link
                    to={item.to}
                    className="rounded hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
                  >
                    {item.label}
                  </Link>
                ) : (
                  <span aria-current={isLast ? 'page' : undefined} className={cn(isLast && 'text-foreground')}>
                    {item.label}
                  </span>
                )}
              </li>
              {!isLast ? (
                <li aria-hidden="true" className="flex items-center">
                  <ChevronRight className="size-3.5" />
                </li>
              ) : null}
            </Fragment>
          );
        })}
      </ol>
    </nav>
  );
}
