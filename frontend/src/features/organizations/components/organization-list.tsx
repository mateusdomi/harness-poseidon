import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Building2 } from 'lucide-react';

import type { Organization } from '@/api';
import { Badge, Button, Card, CardContent, Input } from '@/design-system';
import { useProjectCountsByOrganization } from '@/features/organizations/hooks/use-organizations';
import { PaginationBar } from '@/features/shared/components/pagination';
import { usePagination } from '@/features/shared/hooks/use-pagination';
import { usePresentationMode } from '@/app/presentation';

export interface OrganizationListProps {
  organizations: Organization[];
  onSelect: (organization: Organization) => void;
  onCreateNew: () => void;
}

/**
 * Lista de organizações com busca client-side. Cards empilhados no mobile,
 * grid no desktop.
 */
export function OrganizationList({ organizations, onSelect, onCreateNew }: OrganizationListProps) {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const [query, setQuery] = useState('');
  const countsQuery = useProjectCountsByOrganization();

  const normalized = query.trim().toLocaleLowerCase();
  const filtered = normalized
    ? organizations.filter(
        (org) =>
          org.name.toLocaleLowerCase().includes(normalized) ||
          org.slug.toLocaleLowerCase().includes(normalized),
      )
    : organizations;

  // Paginação client-side; volta para a página 1 ao mudar a busca.
  const pagination = usePagination(filtered.length, { resetKey: normalized });

  // Coleção vazia: apenas o empty state, que é dono da CTA única. A barra de
  // busca e o botão do topo ficam ocultos para não duplicar a ação (§4).
  if (organizations.length === 0) {
    return (
      <Card>
        <CardContent className="flex flex-col items-start gap-3 p-6">
          <p className="font-medium">{t('organizations.empty.title')}</p>
          <p className="text-sm text-foreground-muted">
            {t(
              showTechnicalDetails
                ? 'organizations.empty.body'
                : 'organizations.empty.businessBody',
            )}
          </p>
          <Button type="button" onClick={onCreateNew}>
            {t('organizations.empty.cta')}
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 md:flex-row md:items-center">
        <div className="flex-1">
          <label htmlFor="org-search" className="sr-only">
            {t('organizations.search')}
          </label>
          <Input
            id="org-search"
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder={t('organizations.search')}
          />
        </div>
        <Button type="button" onClick={onCreateNew}>
          {t('organizations.create')}
        </Button>
      </div>

      {filtered.length === 0 ? (
        <p className="text-sm text-foreground-muted">{t('organizations.emptySearch', { query })}</p>
      ) : (
        <>
          <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {pagination.paginate(filtered).map((org) => (
              // `min-w-0` precisa existir em TODA a cadeia até o `truncate`: um nome longo de
              // organização esticava o card inteiro para 400 px dentro de um viewport de 360 e
              // a página passava a rolar na horizontal. O `truncate` do span interno não
              // adiantava porque os pais podiam crescer.
              <li key={org.id} className="min-w-0">
                <button
                  type="button"
                  onClick={() => onSelect(org)}
                  className="flex min-h-touch w-full min-w-0 flex-col gap-2 rounded-lg border border-border bg-surface p-4 text-start transition-colors hover:border-border-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                  <span className="flex w-full min-w-0 items-center gap-2">
                    <span
                      aria-hidden="true"
                      className="flex size-9 shrink-0 items-center justify-center rounded-md bg-surface-elevated text-foreground-muted"
                    >
                      <Building2 className="size-5" aria-hidden="true" />
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-medium">{org.name}</span>
                      {showTechnicalDetails && (
                        <span className="block truncate text-sm text-foreground-muted">
                          {org.slug}
                        </span>
                      )}
                    </span>
                  </span>
                  <span className="flex flex-wrap items-center gap-2">
                    {showTechnicalDetails && (
                      <Badge variant="brand">
                        {t(`organizations.plans.${org.plan}`, { defaultValue: org.plan })}
                      </Badge>
                    )}
                    <span className="text-xs text-foreground-muted">
                      {t('organizations.projectsCount', {
                        count: countsQuery.data?.get(org.id) ?? 0,
                      })}
                    </span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
          <PaginationBar pagination={pagination} />
        </>
      )}
    </div>
  );
}
