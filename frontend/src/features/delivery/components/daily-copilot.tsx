import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Field,
  Select,
  Skeleton,
  Textarea,
} from '@/design-system';

import { formatDateTime } from '../lib/format';
import { useCaptureDaily, useDailyBriefing, useDailySummary } from '../hooks/use-delivery';
import { DAILY_CAPTURE_KINDS } from '../api/types';
import { HealthBadge, PredictabilityBadge, SeverityBadge } from './status-badges';

/**
 * Daily Copilot (DEL-03): briefing pré-daily (foto, mudanças, atenção,
 * perguntas), captura de marcações tipadas durante a daily e resumo pós-daily.
 * NÃO cria nem atualiza cards de PO.
 */
export function DailyCopilot({ deliveryId }: { deliveryId: string }) {
  const { t, i18n } = useTranslation();
  const briefingQuery = useDailyBriefing(deliveryId);
  const summaryQuery = useDailySummary(deliveryId);
  const capture = useCaptureDaily(deliveryId);

  const [kind, setKind] = useState<string>(DAILY_CAPTURE_KINDS[0]);
  const [note, setNote] = useState('');

  function kindLabel(value: string) {
    return t(`delivery.daily.kinds.${value}`);
  }

  return (
    <div className="flex flex-col gap-6" data-testid="daily-copilot">
      <Card>
        <CardHeader>
          <CardTitle>{t('delivery.daily.briefing.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          {briefingQuery.isLoading ? (
            <Skeleton className="h-24 w-full" />
          ) : briefingQuery.data ? (
            <>
              <p className="text-sm text-foreground-muted">
                {briefingQuery.data.isFirstDaily
                  ? t('delivery.daily.briefing.first')
                  : t('delivery.daily.briefing.last', {
                      date: formatDateTime(briefingQuery.data.lastDailyAt, i18n.language) ?? '—',
                    })}
              </p>

              <div className="flex flex-wrap items-center gap-2">
                <span className="text-xs font-medium text-foreground-muted">
                  {t('delivery.daily.briefing.snapshot')}:
                </span>
                <HealthBadge value={briefingQuery.data.snapshot.health} />
                <PredictabilityBadge value={briefingQuery.data.snapshot.predictability} />
                <Badge variant="outline">
                  {briefingQuery.data.snapshot.milestonesDone}/{briefingQuery.data.snapshot.milestonesTotal}
                </Badge>
              </div>

              <div>
                <p className="text-xs font-medium text-foreground-muted">{t('delivery.daily.briefing.changes')}</p>
                <ul className="mt-1 list-disc pl-5 text-sm text-foreground-muted">
                  {briefingQuery.data.changesSinceLast.map((c) => (
                    <li key={c.code}>{c.detail}</li>
                  ))}
                </ul>
              </div>

              {briefingQuery.data.itemsNeedingAttention.length > 0 && (
                <div>
                  <p className="text-xs font-medium text-foreground-muted">{t('delivery.daily.briefing.attention')}</p>
                  <ul className="mt-1 flex flex-col gap-1 text-sm">
                    {briefingQuery.data.itemsNeedingAttention.map((r) => (
                      <li key={r.code} className="flex items-start gap-2">
                        <SeverityBadge value={r.severity} />
                        <span className="text-foreground-muted">{r.detail}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}

              {briefingQuery.data.recommendedQuestions.length > 0 && (
                <div>
                  <p className="text-xs font-medium text-foreground-muted">{t('delivery.daily.briefing.questions')}</p>
                  <ul className="mt-1 list-disc pl-5 text-sm text-foreground-muted">
                    {briefingQuery.data.recommendedQuestions.map((q) => (
                      <li key={q.topic}>{q.question}</li>
                    ))}
                  </ul>
                </div>
              )}
            </>
          ) : null}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('delivery.daily.capture.title')}</CardTitle>
        </CardHeader>
        <CardContent>
          <form
            className="flex flex-col gap-3"
            onSubmit={(event) => {
              event.preventDefault();
              if (note.trim().length === 0) return;
              capture.mutate({ kind, note: note.trim() }, { onSuccess: () => setNote('') });
            }}
          >
            <Field htmlFor="capture-kind" label={t('delivery.daily.capture.kind')} className="max-w-xs">
              <Select id="capture-kind" value={kind} onChange={(e) => setKind(e.target.value)}>
                {DAILY_CAPTURE_KINDS.map((k) => (
                  <option key={k} value={k}>
                    {kindLabel(k)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field htmlFor="capture-note" label={t('delivery.daily.capture.note')}>
              <Textarea
                id="capture-note"
                value={note}
                onChange={(e) => setNote(e.target.value)}
                placeholder={t('delivery.daily.capture.placeholder')}
                rows={2}
              />
            </Field>
            <div>
              <Button type="submit" size="sm" disabled={capture.isPending || note.trim().length === 0}>
                {t('delivery.daily.capture.submit')}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex flex-row items-center justify-between gap-2">
          <CardTitle>{t('delivery.daily.summary.title')}</CardTitle>
          <Badge variant="outline">
            {t('delivery.daily.summary.total', { count: summaryQuery.data?.totalCaptures ?? 0 })}
          </Badge>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {summaryQuery.isLoading ? (
            <Skeleton className="h-16 w-full" />
          ) : (summaryQuery.data?.captures.length ?? 0) === 0 ? (
            <p className="text-sm text-foreground-muted">{t('delivery.daily.summary.none')}</p>
          ) : (
            <>
              <div className="flex flex-wrap gap-2">
                {summaryQuery.data!.byKind.map((b) => (
                  <Badge key={b.kind} variant="info">
                    {kindLabel(b.kind)}: {b.count}
                  </Badge>
                ))}
              </div>
              <ul className="flex flex-col gap-2 text-sm">
                {summaryQuery.data!.captures.map((c) => (
                  <li key={c.id} className="flex items-start gap-2">
                    <Badge variant="outline">{kindLabel(c.kind)}</Badge>
                    <span className="text-foreground-muted">{c.note}</span>
                  </li>
                ))}
              </ul>
            </>
          )}
          <p className="text-xs text-foreground-muted">{t('delivery.daily.summary.poCards')}</p>
        </CardContent>
      </Card>
    </div>
  );
}
