import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Workflow as WorkflowIcon } from 'lucide-react';

import type { Ulid, WorkflowTemplate, WorkflowVersion } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Select } from '@/design-system';
import { recommendWorkflowTemplate } from '@/features/workflows/lib/recommend-template';

export interface WorkflowOnboardingEmptyProps {
  templates: WorkflowTemplate[];
  versions: WorkflowVersion[];
  linking: boolean;
  error: boolean;
  onLink: (templateId: Ulid) => void;
}

/**
 * Estado vazio orientado do workflow (§14): explica o que é, recomenda um
 * template publicado real, resume suas fases e vincula em um clique — sem
 * obrigar o usuário a construir um workflow do zero. "Escolher outro" abre a
 * lista dos demais templates publicados; a gestão completa segue disponível.
 */
export function WorkflowOnboardingEmpty({
  templates,
  versions,
  linking,
  error,
  onLink,
}: WorkflowOnboardingEmptyProps) {
  const { t } = useTranslation();
  const recommendation = recommendWorkflowTemplate(templates, versions);
  const [choosing, setChoosing] = useState(false);
  const [selectedId, setSelectedId] = useState<Ulid | ''>('');

  // Sem template publicado não há o que recomendar: orientamos a criar um,
  // em vez de prometer um caminho que não existe (fail-closed).
  if (!recommendation) {
    return (
      <Card>
        <CardHeader className="flex flex-row items-center gap-2">
          <WorkflowIcon aria-hidden="true" className="size-5 text-foreground-muted" />
          <CardTitle>{t('workflows.empty.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col items-start gap-3">
          <p className="max-w-prose text-sm text-foreground-muted">
            {t('workflows.empty.explanation')}
          </p>
          <p className="max-w-prose text-sm text-foreground-muted">
            {t('workflows.empty.noTemplates')}
          </p>
        </CardContent>
      </Card>
    );
  }

  const { template, version } = recommendation;
  const others = templates.filter(
    (candidate) => candidate.id !== template.id && candidate.currentVersionId !== null,
  );

  return (
    <Card>
      <CardHeader className="flex flex-row items-center gap-2">
        <WorkflowIcon aria-hidden="true" className="size-5 text-foreground-muted" />
        <CardTitle>{t('workflows.empty.title')}</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col items-start gap-4">
        <p className="max-w-prose text-sm text-foreground-muted">
          {t('workflows.empty.explanation')}
        </p>

        <section
          aria-labelledby="recommended-template-title"
          className="flex w-full flex-col gap-2 rounded-lg border border-border bg-surface-elevated p-4"
        >
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant="brand">{t('workflows.empty.recommendedBadge')}</Badge>
            <h3 id="recommended-template-title" className="font-medium">
              {template.name}
            </h3>
            <Badge variant="outline">
              {t('workflows.empty.versionLabel', { version: version.version })}
            </Badge>
          </div>
          {template.description ? (
            <p className="text-sm text-foreground-muted">{template.description}</p>
          ) : null}
          <div className="flex flex-col gap-1">
            <span className="text-xs font-medium text-foreground-muted">
              {t('workflows.empty.phasesLabel')}
            </span>
            <ol className="flex flex-wrap items-center gap-1.5">
              {version.phases.map((phase, index) => (
                <li key={phase} className="flex items-center gap-1.5">
                  <span className="rounded-md bg-surface px-2 py-0.5 text-xs">
                    {index + 1}. {phase}
                  </span>
                </li>
              ))}
            </ol>
          </div>
        </section>

        {error ? (
          <p role="alert" className="text-sm text-error">
            {t('workflows.empty.linkError')}
          </p>
        ) : null}

        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" disabled={linking} onClick={() => onLink(template.id)}>
            {linking ? t('common.states.loading') : t('workflows.empty.useRecommended')}
          </Button>
          {others.length > 0 && !choosing ? (
            <Button type="button" variant="outline" onClick={() => setChoosing(true)}>
              {t('workflows.empty.chooseAnother')}
            </Button>
          ) : null}
        </div>

        {choosing && others.length > 0 ? (
          <div className="flex w-full flex-wrap items-end gap-3">
            <div className="flex min-w-56 flex-1 flex-col gap-1.5">
              <label htmlFor="workflow-template-choice" className="text-sm font-medium">
                {t('workflows.empty.otherTemplateLabel')}
              </label>
              <Select
                id="workflow-template-choice"
                value={selectedId}
                onChange={(event) => setSelectedId(event.target.value)}
              >
                <option value="">{t('workflows.empty.otherTemplatePlaceholder')}</option>
                {others.map((candidate) => (
                  <option key={candidate.id} value={candidate.id}>
                    {candidate.name}
                  </option>
                ))}
              </Select>
            </div>
            <Button
              type="button"
              variant="outline"
              disabled={selectedId === '' || linking}
              onClick={() => selectedId !== '' && onLink(selectedId)}
            >
              {t('workflows.empty.linkSelected')}
            </Button>
          </div>
        ) : null}

        <p className="text-xs text-foreground-muted">{t('workflows.empty.manageHint')}</p>
      </CardContent>
    </Card>
  );
}
