import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Gauge } from 'lucide-react';

import { Badge, Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { useReliability } from '@/features/reliability/hooks/use-reliability';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/**
 * Produtividade e confiabilidade da fleet no projeto ativo (B1+B12/F16) — modo Técnico.
 *
 * Três leituras que só valem juntas:
 *   * ASSINATURAS — o que cada conta entregou e a que custo. Pass@k sozinho esconde consumo: uma
 *     conta pode acertar muito e gastar desproporcionalmente;
 *   * CAPACIDADE — pass@1 e pass@k por par conta+modelo, com o número de rodadas que a medição
 *     recomenda. É a resposta a "quantas tentativas vale dar antes de trocar de conta";
 *   * FALHAS — a distribuição MAST e o que ela recomenda. Concentração em especificação significa
 *     que decompor mais não ajuda: o enunciado é que precisa mudar.
 *
 * Nada disto aparece no modo Negócio: o léxico §2 proíbe conta, modelo e taxa técnica para o dono
 * leigo. É informação de quem opera a plataforma.
 */
export default function UreliabilityPage() {
  const { t, i18n } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const [k, setK] = useState(3);
  const reliability = useReliability(activeProject?.id ?? null, k);

  const loading = isPending || (activeProject !== null && reliability.isPending);
  const errored = isError || reliability.isError;
  const data = reliability.data;

  function retryAll() {
    refetch();
    reliability.refetch();
  }

  const percent = (value: number) => `${Math.round(value * 100)}%`;
  // Formata pelo idioma ESCOLHIDO na aplicação, não pelo locale do sistema onde ela roda: o mesmo
  // número não pode aparecer diferente para dois operadores da mesma instalação.
  const number = (value: number) => value.toLocaleString(i18n.language);
  const tokenBreakdown = (row: { exactTokens: number; estimatedTokens: number; usageUnavailableInvocations: number }) => {
    const parts = [];
    if (row.exactTokens > 0) {
      parts.push(t('reliability.subscriptions.exactTokens', { value: number(row.exactTokens) }));
    }
    if (row.estimatedTokens > 0) {
      parts.push(t('reliability.subscriptions.estimatedTokens', { value: number(row.estimatedTokens) }));
    }
    if (row.usageUnavailableInvocations > 0) {
      parts.push(t('reliability.subscriptions.unavailableUsage', { count: row.usageUnavailableInvocations }));
    }
    return parts.length === 0 ? '—' : parts.join(' · ');
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('reliability.title')}</h1>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <label htmlFor="reliability-k" className="text-sm text-foreground-muted">
            {t('reliability.roundsLabel')}
          </label>
          <Select
            id="reliability-k"
            className="w-auto"
            value={String(k)}
            onChange={(event) => setK(Number(event.target.value))}
          >
            {[1, 2, 3, 4, 5].map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </Select>
          {projects.length > 0 && (
            <>
              <label htmlFor="reliability-project" className="text-sm text-foreground-muted">
                {t('cockpit.projectSelector.label')}
              </label>
              <Select
                id="reliability-project"
                className="w-auto min-w-0 max-w-full md:min-w-48"
                value={activeProject?.id ?? ''}
                onChange={(event) => setActiveProject(event.target.value)}
              >
                {projects.map((project) => (
                  <option key={project.id} value={project.id}>
                    {project.name}
                  </option>
                ))}
              </Select>
            </>
          )}
        </div>
      </div>

      <p className="text-sm text-foreground-muted">{t('reliability.subtitle')}</p>

      {loading ? (
        <div className="grid gap-4 md:grid-cols-2" role="status" aria-label={t('common.states.loading')}>
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-48 w-full" />
          ))}
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={retryAll}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        <Card>
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('reliability.noProject.body')}</p>
            <Button asChild>
              <Link to="/projects">{t('reliability.noProject.cta')}</Link>
            </Button>
          </CardContent>
        </Card>
      ) : !data ||
        (data.subscriptions.length === 0 &&
          data.capabilities.length === 0 &&
          data.intents.length === 0) ? (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 p-6 text-center">
            <Gauge aria-hidden="true" className="size-8 text-foreground-muted" />
            <h2 className="font-heading text-lg font-semibold">{t('reliability.empty.title')}</h2>
            <p className="text-sm text-foreground-muted">{t('reliability.empty.body')}</p>
          </CardContent>
        </Card>
      ) : (
        <>
          {/* Amostra recortada é DITA: um recorte silencioso se lê como "é tudo o que existe". */}
          {data.sampleTruncated && (
            <p role="status" className="text-sm text-warning">
              {t('reliability.sampleTruncated')}
            </p>
          )}

          <Card>
            <CardContent className="flex flex-col gap-3 p-6">
              <h2 className="font-heading text-lg font-semibold">
                {t('reliability.subscriptions.title')}
              </h2>
              <p className="text-sm text-foreground-muted">
                {t('reliability.subscriptions.help')}
              </p>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[46rem] text-left text-sm">
                  <thead className="text-foreground-muted">
                    <tr>
                      <th scope="col" className="py-2 pr-4 font-medium">
                        {t('reliability.subscriptions.account')}
                      </th>
                      <th scope="col" className="py-2 pr-4 font-medium">
                        {t('reliability.subscriptions.provider')}
                      </th>
                      <th scope="col" className="py-2 pr-4 font-medium">
                        {t('reliability.subscriptions.tasks')}
                      </th>
                      <th scope="col" className="py-2 pr-4 font-medium">
                        {t('reliability.subscriptions.invocations')}
                      </th>
                      <th scope="col" className="py-2 pr-4 font-medium">
                        {t('reliability.subscriptions.successRate')}
                      </th>
                      <th scope="col" className="py-2 pr-4 font-medium">
                        {t('reliability.subscriptions.tokens')}
                      </th>
                      <th scope="col" className="py-2 font-medium">
                        {t('reliability.subscriptions.cost')}
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.subscriptions.map((row) => (
                      <tr key={row.accountAlias} className="border-t border-border">
                        <th scope="row" className="py-2 pr-4 font-medium">
                          {row.accountAlias}
                        </th>
                        <td className="py-2 pr-4">{row.providers.join(', ')}</td>
                        <td className="py-2 pr-4">{row.tasksTouched}</td>
                        <td className="py-2 pr-4">{row.invocations}</td>
                        <td className="py-2 pr-4">
                          {row.invocations === 0 ? '—' : percent(row.successes / row.invocations)}
                        </td>
                        <td className="py-2 pr-4">{tokenBreakdown(row)}</td>
                        <td className="py-2">{`US$ ${row.estimatedCostUsd.toFixed(2)}`}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardContent className="flex flex-col gap-3 p-6">
              <h2 className="font-heading text-lg font-semibold">
                {t('reliability.capabilities.title')}
              </h2>
              <p className="text-sm text-foreground-muted">{t('reliability.capabilities.help')}</p>
              {data.capabilities.length === 0 ? (
                <p className="text-sm text-foreground-muted">
                  {t('reliability.capabilities.empty')}
                </p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[40rem] text-left text-sm">
                    <thead className="text-foreground-muted">
                      <tr>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.subscriptions.account')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.capabilities.model')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.capabilities.observed')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.capabilities.passAt1')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.capabilities.passAtK', { k })}
                        </th>
                        <th scope="col" className="py-2 font-medium">
                          {t('reliability.capabilities.recommended')}
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {data.capabilities.map((row) => (
                        <tr
                          key={`${row.accountAlias}:${row.modelTier}:${row.cardType}`}
                          className="border-t border-border"
                        >
                          <th scope="row" className="py-2 pr-4 font-medium">
                            {row.accountAlias}
                          </th>
                          <td className="py-2 pr-4">{row.modelTier}</td>
                          <td className="py-2 pr-4">{row.tasksObserved}</td>
                          <td className="py-2 pr-4">{percent(row.passAt1)}</td>
                          <td className="py-2 pr-4">{percent(row.passAtK)}</td>
                          <td className="py-2">{row.recommendedMaxRounds}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </CardContent>
          </Card>

          {data.intents.length > 0 && (
            <Card>
              <CardContent className="flex flex-col gap-3 p-6">
                <h2 className="font-heading text-lg font-semibold">
                  {t('reliability.intents.title')}
                </h2>
                <p className="text-sm text-foreground-muted">{t('reliability.intents.help')}</p>
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[42rem] text-left text-sm">
                    <thead className="text-foreground-muted">
                      <tr>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.intents.intent')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.intents.turns')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.intents.average')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.intents.p95')}
                        </th>
                        <th scope="col" className="py-2 pr-4 font-medium">
                          {t('reliability.intents.dropped')}
                        </th>
                        <th scope="col" className="py-2 font-medium">
                          {t('reliability.intents.confidence')}
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {data.intents.map((row) => (
                        <tr key={row.intent} className="border-t border-border">
                          <th scope="row" className="py-2 pr-4 font-medium">
                            {t(`reliability.intents.names.${row.intent}`, {
                              defaultValue: row.intent,
                            })}
                          </th>
                          <td className="py-2 pr-4">{number(row.turns)}</td>
                          <td className="py-2 pr-4">{`${number(Math.round(row.averageDurationMs))} ms`}</td>
                          <td className="py-2 pr-4">{`${number(row.p95DurationMs)} ms`}</td>
                          <td className="py-2 pr-4">{number(row.turnsWithDroppedActions)}</td>
                          <td className="py-2">{percent(row.averageConfidence)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </CardContent>
            </Card>
          )}

          <Card>
            <CardContent className="flex flex-col gap-3 p-6">
              <h2 className="font-heading text-lg font-semibold">
                {t('reliability.failures.title')}
              </h2>
              <p className="text-sm text-foreground-muted">
                {t('reliability.failures.classified', { count: data.classifiedAttempts })}
              </p>
              <div className="flex flex-wrap gap-2">
                {Object.entries(data.failureModesByCategory).map(([category, count]) => (
                  <Badge key={category} variant="outline">
                    {t(`reliability.failures.categories.${category}`, { defaultValue: category })}:{' '}
                    {count}
                  </Badge>
                ))}
              </div>
              {data.advice && <p className="text-sm">{data.advice}</p>}
            </CardContent>
          </Card>
        </>
      )}
    </div>
  );
}
