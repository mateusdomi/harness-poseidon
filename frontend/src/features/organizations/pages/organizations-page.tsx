import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

import type { Organization } from '@/api';
import { Button, Skeleton } from '@/design-system';
import { OrganizationDetail } from '@/features/organizations/components/organization-detail';
import { OrganizationForm } from '@/features/organizations/components/organization-form';
import type { OrganizationFormValues } from '@/features/organizations/components/organization-form-schema';
import { OrganizationList } from '@/features/organizations/components/organization-list';
import { BackLink } from '@/features/shared/components/back-link';
import { Breadcrumb } from '@/features/shared/components/breadcrumb';
import {
  useCreateOrganization,
  useOrganizations,
  useUpdateOrganization,
} from '@/features/organizations/hooks/use-organizations';
import { usePresentationMode } from '@/app/presentation';

type View =
  | { kind: 'list' }
  | { kind: 'detail'; organization: Organization }
  | { kind: 'create' }
  | { kind: 'edit'; organization: Organization };

export default function OrganizationsPage() {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const organizationsQuery = useOrganizations();
  const createOrganization = useCreateOrganization();
  const updateOrganization = useUpdateOrganization();
  const [view, setView] = useState<View>({ kind: 'list' });

  // Deep link `?new=1` (ex.: golden path / pré-condição de projeto) abre o
  // formulário de criação direto, preservando a intenção de retorno (`return`).
  const wantsCreate = searchParams.get('new') === '1';
  const returnTo = searchParams.get('return');
  useEffect(() => {
    if (wantsCreate) {
      setView({ kind: 'create' });
      const next = new URLSearchParams(searchParams);
      next.delete('new');
      setSearchParams(next, { replace: true });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [wantsCreate]);

  async function handleCreate(values: OrganizationFormValues) {
    const created = await createOrganization.mutateAsync(values);
    // Retorno ao fluxo de projeto: volta pré-selecionando a nova organização.
    if (returnTo === 'project') {
      navigate(`/projects?new=1&org=${created.id}`);
      return;
    }
    setView({ kind: 'list' });
  }

  async function handleUpdate(organization: Organization, values: OrganizationFormValues) {
    const updated = await updateOrganization.mutateAsync({ id: organization.id, input: values });
    setView({ kind: 'detail', organization: updated });
  }

  const breadcrumbBase = { label: t('features.organizations.title'), to: '/organizations' };

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.organizations.title')}</h1>
      </div>
      <p className="max-w-prose text-foreground-muted">
        {t(
          showTechnicalDetails
            ? 'features.organizations.description'
            : 'features.organizations.businessDescription',
        )}
      </p>

      {view.kind === 'create' && (
        <div className="flex flex-col gap-3">
          <Breadcrumb items={[breadcrumbBase, { label: t('organizations.form.createTitle') }]} />
          <BackLink
            label={t('organizations.back')}
            onBack={() =>
              returnTo === 'project' ? navigate('/projects') : setView({ kind: 'list' })
            }
            fallbackTo="/organizations"
          />
          <OrganizationForm
            submitting={createOrganization.isPending}
            onSubmit={(values) => void handleCreate(values)}
            onCancel={() =>
              returnTo === 'project' ? navigate('/projects') : setView({ kind: 'list' })
            }
          />
        </div>
      )}

      {view.kind === 'edit' && (
        <div className="flex flex-col gap-3">
          <Breadcrumb
            items={[
              breadcrumbBase,
              { label: view.organization.name },
              { label: t('organizations.form.editTitle') },
            ]}
          />
          <BackLink
            label={t('organizations.back')}
            onBack={() => setView({ kind: 'detail', organization: view.organization })}
            fallbackTo="/organizations"
          />
          <OrganizationForm
            initial={view.organization}
            submitting={updateOrganization.isPending}
            onSubmit={(values) => void handleUpdate(view.organization, values)}
            onCancel={() => setView({ kind: 'detail', organization: view.organization })}
          />
        </div>
      )}

      {view.kind === 'detail' && (
        <div className="flex flex-col gap-3">
          <Breadcrumb items={[breadcrumbBase, { label: view.organization.name }]} />
          <OrganizationDetail
            organization={view.organization}
            onEdit={() => setView({ kind: 'edit', organization: view.organization })}
            onBack={() => setView({ kind: 'list' })}
          />
        </div>
      )}

      {view.kind === 'list' && (
        <>
          {organizationsQuery.isLoading ? (
            <div
              className="flex flex-col gap-3"
              role="status"
              aria-label={t('common.states.loading')}
            >
              <Skeleton className="h-11 w-full" />
              <Skeleton className="h-24 w-full" />
              <Skeleton className="h-24 w-full" />
            </div>
          ) : organizationsQuery.isError ? (
            <div className="flex flex-col items-start gap-3">
              <p role="alert" className="text-sm text-error">
                {t('common.states.errorBody')}
              </p>
              <Button
                type="button"
                variant="outline"
                onClick={() => void organizationsQuery.refetch()}
              >
                {t('common.actions.retry')}
              </Button>
            </div>
          ) : (
            <OrganizationList
              organizations={organizationsQuery.data ?? []}
              onSelect={(organization) => setView({ kind: 'detail', organization })}
              onCreateNew={() => setView({ kind: 'create' })}
            />
          )}
        </>
      )}
    </div>
  );
}
