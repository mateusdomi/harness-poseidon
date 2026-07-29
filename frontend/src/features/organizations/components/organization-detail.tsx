import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { ArrowLeft, Pencil } from 'lucide-react';

import type { Brand, Organization } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { formatRelativeTime } from '@/lib/format';
import { priorityVariant, projectStateVariant } from '@/lib/status';
import { InheritanceBadge } from '@/features/shared/components/inheritance-badge';
import {
  useOrganizationProjects,
  useWorkflowTemplates,
} from '@/features/organizations/hooks/use-organizations';
import { usePresentationMode } from '@/app/presentation';

const BRAND_FIELD_KEYS = ['logoUrl', 'primaryColor', 'secondaryColor', 'typography'] as const;

/** Marca da organização, campo a campo, com herança do padrão do produto. */
function BrandSummary({ brand }: { brand: Brand }) {
  const { t } = useTranslation();
  return (
    <ul className="flex flex-col gap-3">
      {BRAND_FIELD_KEYS.map((fieldKey) => {
        const value = brand[fieldKey];
        return (
          <li key={fieldKey} className="flex flex-wrap items-center gap-3">
            <span className="w-32 text-sm text-foreground-muted">
              {t(`common.brand.fields.${fieldKey}`)}
            </span>
            {fieldKey !== 'logoUrl' && fieldKey !== 'typography' && value ? (
              <span
                aria-hidden="true"
                className="size-5 rounded border border-border"
                style={{ backgroundColor: value }}
              />
            ) : null}
            <span className="text-sm font-medium">
              {value ?? t('common.brand.emptyPlaceholder')}
            </span>
            <InheritanceBadge inherited={value === null} source="productDefault" />
          </li>
        );
      })}
    </ul>
  );
}

export interface OrganizationDetailProps {
  organization: Organization;
  onEdit: () => void;
  onBack: () => void;
}

/**
 * Detalhe da organização: marca (herdável), workflows padrão, templates,
 * políticas e projetos associados.
 */
export function OrganizationDetail({ organization, onEdit, onBack }: OrganizationDetailProps) {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const projectsQuery = useOrganizationProjects(organization.id);
  const templatesQuery = useWorkflowTemplates();

  const defaultTemplates = (templatesQuery.data ?? []).filter((template) =>
    organization.defaultWorkflowTemplateIds.includes(template.id),
  );

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <Button
          type="button"
          variant="ghost"
          size="icon"
          onClick={onBack}
          aria-label={t('common.actions.back')}
        >
          <ArrowLeft aria-hidden="true" />
        </Button>
        <h2 className="font-heading text-xl font-semibold">{organization.name}</h2>
        {showTechnicalDetails && (
          <Badge variant="brand">
            {t(`organizations.plans.${organization.plan}`, { defaultValue: organization.plan })}
          </Badge>
        )}
        <Button type="button" variant="outline" className="ms-auto" onClick={onEdit}>
          <Pencil aria-hidden="true" />
          {t('organizations.detail.edit')}
        </Button>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>{t('organizations.detail.brand')}</CardTitle>
          </CardHeader>
          <CardContent>
            <BrandSummary brand={organization.brand} />
          </CardContent>
        </Card>

        {showTechnicalDetails && (
          <Card>
            <CardHeader>
              <CardTitle>{t('organizations.detail.workflows')}</CardTitle>
            </CardHeader>
            <CardContent>
              {templatesQuery.isLoading ? (
                <Skeleton className="h-6 w-full" />
              ) : templatesQuery.isError ? (
                <div className="flex flex-col items-start gap-2">
                  <p role="alert" className="text-sm text-error">
                    {t('common.states.errorBody')}
                  </p>
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => void templatesQuery.refetch()}
                  >
                    {t('common.actions.retry')}
                  </Button>
                </div>
              ) : defaultTemplates.length === 0 ? (
                // Empty state que orienta E age: a gestão de workflows existe
                // como tela real, então a CTA leva até ela (§10).
                <div className="flex flex-col items-start gap-2">
                  <p className="text-sm text-foreground-muted">
                    {t('organizations.detail.noWorkflows')}
                  </p>
                  <p className="text-xs text-foreground-muted">
                    {t('organizations.detail.noWorkflowsImpact')}
                  </p>
                  <Button asChild variant="outline" size="sm">
                    <Link to="/workflows">{t('organizations.detail.configureWorkflows')}</Link>
                  </Button>
                </div>
              ) : (
                <ul className="flex flex-col gap-2">
                  {defaultTemplates.map((template) => (
                    <li key={template.id} className="flex flex-col">
                      <span className="text-sm font-medium">{template.name}</span>
                      <span className="text-xs text-foreground-muted">{template.description}</span>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>
        )}

        {showTechnicalDetails && (
          <Card>
            <CardHeader>
              <CardTitle>{t('organizations.detail.templates')}</CardTitle>
            </CardHeader>
            <CardContent>
              {organization.templateKeys.length === 0 ? (
                // Sem contrato de gestão de templates de documento, explicamos o
                // impacto e NÃO oferecemos CTA que não levaria a lugar nenhum.
                <div className="flex flex-col gap-1">
                  <p className="text-sm text-foreground-muted">
                    {t('organizations.detail.noTemplates')}
                  </p>
                  <p className="text-xs text-foreground-muted">
                    {t('organizations.detail.noTemplatesImpact')}
                  </p>
                </div>
              ) : (
                <ul className="flex flex-wrap gap-2">
                  {organization.templateKeys.map((key) => (
                    <li key={key}>
                      <Badge variant="default">
                        {t(`organizations.templateKeys.${key}`, { defaultValue: key })}
                      </Badge>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>
        )}

        {showTechnicalDetails && (
          <Card>
            <CardHeader>
              <CardTitle>{t('organizations.detail.policies')}</CardTitle>
            </CardHeader>
            <CardContent>
              {organization.policies.length === 0 ? (
                // Idem: não há comando de configuração de políticas no contrato.
                <div className="flex flex-col gap-1">
                  <p className="text-sm text-foreground-muted">
                    {t('organizations.detail.noPolicies')}
                  </p>
                  <p className="text-xs text-foreground-muted">
                    {t('organizations.detail.noPoliciesImpact')}
                  </p>
                </div>
              ) : (
                <ul className="flex flex-col gap-3">
                  {organization.policies.map((policy) => (
                    <li key={policy.key} className="flex flex-wrap items-center gap-2">
                      <Badge variant={policy.enabled ? 'success' : 'outline'}>
                        {policy.enabled
                          ? t('organizations.detail.policyEnabled')
                          : t('organizations.detail.policyDisabled')}
                      </Badge>
                      <span className="text-sm">{policy.description}</span>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>
        )}
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t('organizations.detail.projects')}</CardTitle>
        </CardHeader>
        <CardContent>
          {projectsQuery.isLoading ? (
            <div className="flex flex-col gap-2">
              <Skeleton className="h-14 w-full" />
              <Skeleton className="h-14 w-full" />
            </div>
          ) : projectsQuery.isError ? (
            <div className="flex flex-col items-start gap-2">
              <p role="alert" className="text-sm text-error">
                {t('common.states.errorBody')}
              </p>
              <Button type="button" variant="outline" onClick={() => void projectsQuery.refetch()}>
                {t('common.actions.retry')}
              </Button>
            </div>
          ) : (projectsQuery.data ?? []).length === 0 ? (
            // Empty state contextual: cria o primeiro projeto JÁ com esta
            // organização pré-selecionada, preservando o contexto (§6/§10).
            <div className="flex flex-col items-start gap-2">
              <p className="text-sm text-foreground-muted">
                {t('organizations.detail.noProjectsContextual', { name: organization.name })}
              </p>
              <Button asChild size="sm">
                <Link to={`/projects?new=1&org=${organization.id}`}>
                  {t('organizations.detail.createProject')}
                </Link>
              </Button>
            </div>
          ) : (
            <ul className="grid gap-3 md:grid-cols-2">
              {(projectsQuery.data ?? []).map((project) => (
                <li
                  key={project.id}
                  className="flex flex-col gap-2 rounded-md border border-border bg-surface-elevated p-4"
                >
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{project.name}</span>
                    <Badge variant={projectStateVariant(project.state)}>
                      {t(`status.projectState.${project.state}`)}
                    </Badge>
                    <Badge variant={priorityVariant(project.criticality)}>
                      {t(`status.priority.${project.criticality}`)}
                    </Badge>
                  </div>
                  <p className="text-sm text-foreground-muted">{project.description}</p>
                  <p className="text-xs text-foreground-muted">
                    {t('projects.card.lastActivity', {
                      time: formatRelativeTime(project.lastActivityAt),
                    })}
                  </p>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
