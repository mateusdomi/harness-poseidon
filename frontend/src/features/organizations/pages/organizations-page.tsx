import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { Organization } from '@/api';
import { Button, Skeleton } from '@/design-system';
import { OrganizationDetail } from '@/features/organizations/components/organization-detail';
import { OrganizationForm } from '@/features/organizations/components/organization-form';
import type { OrganizationFormValues } from '@/features/organizations/components/organization-form-schema';
import { OrganizationList } from '@/features/organizations/components/organization-list';
import {
  useCreateOrganization,
  useOrganizations,
  useUpdateOrganization,
} from '@/features/organizations/hooks/use-organizations';

type View =
  | { kind: 'list' }
  | { kind: 'detail'; organization: Organization }
  | { kind: 'create' }
  | { kind: 'edit'; organization: Organization };

export default function OrganizationsPage() {
  const { t } = useTranslation();
  const organizationsQuery = useOrganizations();
  const createOrganization = useCreateOrganization();
  const updateOrganization = useUpdateOrganization();
  const [view, setView] = useState<View>({ kind: 'list' });

  async function handleCreate(values: OrganizationFormValues) {
    await createOrganization.mutateAsync(values);
    setView({ kind: 'list' });
  }

  async function handleUpdate(organization: Organization, values: OrganizationFormValues) {
    const updated = await updateOrganization.mutateAsync({ id: organization.id, input: values });
    setView({ kind: 'detail', organization: updated });
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.organizations.title')}</h1>
      </div>
      <p className="max-w-prose text-foreground-muted">
        {t('features.organizations.description')}
      </p>

      {view.kind === 'create' && (
        <OrganizationForm
          submitting={createOrganization.isPending}
          onSubmit={(values) => void handleCreate(values)}
          onCancel={() => setView({ kind: 'list' })}
        />
      )}

      {view.kind === 'edit' && (
        <OrganizationForm
          initial={view.organization}
          submitting={updateOrganization.isPending}
          onSubmit={(values) => void handleUpdate(view.organization, values)}
          onCancel={() => setView({ kind: 'detail', organization: view.organization })}
        />
      )}

      {view.kind === 'detail' && (
        <OrganizationDetail
          organization={view.organization}
          onEdit={() => setView({ kind: 'edit', organization: view.organization })}
          onBack={() => setView({ kind: 'list' })}
        />
      )}

      {view.kind === 'list' && (
        <>
          {organizationsQuery.isLoading ? (
            <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
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
