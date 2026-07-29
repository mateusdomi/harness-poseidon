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
  Input,
  Select,
  Skeleton,
} from '@/design-system';

import { formatDate } from '@/features/delivery/lib/format';
import {
  useApproveReport,
  useGenerateReport,
  useReports,
  useSendReport,
} from '@/features/delivery/hooks/use-delivery';
import { useDeliveryApi } from '@/features/delivery/api/delivery-context';
import { REPORT_FORMATS, REPORT_TYPES, type DeliveryReport } from '@/features/delivery/api/types';
import { useReportTypeLabel } from '@/features/delivery/hooks/use-report-labels';
import { ReportStatusBadge } from '@/features/delivery/components/status-badges';

function ReportViewer({ report, onClose }: { report: DeliveryReport; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between gap-2">
        <CardTitle>{report.document?.title ?? report.type}</CardTitle>
        <Button variant="ghost" size="sm" onClick={onClose}>
          {t('delivery.reports.close')}
        </Button>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {!report.available && (
          <p className="text-sm text-warning">
            {t('delivery.reports.unavailable', { reason: report.reason ?? '' })}
          </p>
        )}
        {report.document?.sections.map((section) => (
          <section key={section.key} className="flex flex-col gap-2">
            <h4 className="text-sm font-semibold text-foreground">{section.title}</h4>
            {section.fields.length > 0 && (
              <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
                {section.fields.map((f) => (
                  <div key={f.label}>
                    <dt className="text-xs text-foreground-muted">{f.label}</dt>
                    <dd className="text-foreground">{f.value}</dd>
                  </div>
                ))}
              </dl>
            )}
          </section>
        ))}
      </CardContent>
    </Card>
  );
}

function SendForm({
  deliveryId,
  reportId,
  onDone,
}: {
  deliveryId: string;
  reportId: string;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const send = useSendReport(deliveryId);
  const [channel, setChannel] = useState('email');
  const [recipient, setRecipient] = useState('secret://coordination-inbox');

  return (
    <form
      className="mt-2 flex flex-col gap-2 border-t border-border-strong pt-3"
      onSubmit={(event) => {
        event.preventDefault();
        send.mutate(
          { reportId, input: { channel, recipientReference: recipient } },
          { onSuccess: onDone },
        );
      }}
    >
      <p className="text-sm font-medium text-foreground">{t('delivery.reports.sendDialog.title')}</p>
      <Field htmlFor={`channel-${reportId}`} label={t('delivery.reports.sendDialog.channel')}>
        <Select id={`channel-${reportId}`} value={channel} onChange={(e) => setChannel(e.target.value)}>
          <option value="email">{t('delivery.reports.channels.email')}</option>
          <option value="teams">{t('delivery.reports.channels.teams')}</option>
        </Select>
      </Field>
      <Field
        htmlFor={`recipient-${reportId}`}
        label={t('delivery.reports.sendDialog.recipient')}
        hint={t('delivery.reports.sendDialog.recipientHint')}
      >
        <Input
          id={`recipient-${reportId}`}
          value={recipient}
          onChange={(e) => setRecipient(e.target.value)}
        />
      </Field>
      {send.isError && <p className="text-xs text-error">{(send.error as Error).message}</p>}
      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={send.isPending}>
          {t('delivery.reports.sendDialog.submit')}
        </Button>
        <Button type="button" variant="ghost" size="sm" onClick={onDone}>
          {t('delivery.reports.sendDialog.cancel')}
        </Button>
      </div>
    </form>
  );
}

/**
 * Central de Relatórios (DEL-04/05/10): gerar (rascunho), aprovar (obrigatório
 * antes do envio) e enviar externamente por canal para uma referência opaca.
 */
export function ReportsCenter({ deliveryId }: { deliveryId: string }) {
  const { t, i18n } = useTranslation();
  const reportsQuery = useReports(deliveryId);
  const generate = useGenerateReport(deliveryId);
  const approve = useApproveReport(deliveryId);
  const api = useDeliveryApi();
  const typeLabel = useReportTypeLabel();

  const [type, setType] = useState<string>(REPORT_TYPES[0]);
  const [format, setFormat] = useState<string>(REPORT_FORMATS[0]);
  const [viewing, setViewing] = useState<DeliveryReport | null>(null);
  const [sendingId, setSendingId] = useState<string | null>(null);

  async function handleView(reportId: string) {
    const report = await api.getReport(deliveryId, reportId);
    setViewing(report);
  }

  return (
    <div className="flex flex-col gap-6" data-testid="reports-center">
      <Card>
        <CardHeader>
          <CardTitle>{t('delivery.reports.generate.title')}</CardTitle>
        </CardHeader>
        <CardContent>
          <form
            className="flex flex-wrap items-end gap-3"
            onSubmit={(event) => {
              event.preventDefault();
              generate.mutate({ type, format });
            }}
          >
            <Field htmlFor="report-type" label={t('delivery.reports.generate.type')} className="min-w-56">
              <Select id="report-type" value={type} onChange={(e) => setType(e.target.value)}>
                {REPORT_TYPES.map((rt) => (
                  <option key={rt} value={rt}>
                    {typeLabel(rt)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field htmlFor="report-format" label={t('delivery.reports.generate.format')} className="min-w-40">
              <Select id="report-format" value={format} onChange={(e) => setFormat(e.target.value)}>
                {REPORT_FORMATS.map((rf) => (
                  <option key={rf} value={rf}>
                    {rf}
                  </option>
                ))}
              </Select>
            </Field>
            <Button type="submit" disabled={generate.isPending}>
              {t('delivery.reports.generate.submit')}
            </Button>
          </form>
        </CardContent>
      </Card>

      {viewing && <ReportViewer report={viewing} onClose={() => setViewing(null)} />}

      <Card>
        <CardHeader>
          <CardTitle>{t('delivery.reports.title')}</CardTitle>
        </CardHeader>
        <CardContent>
          {reportsQuery.isLoading ? (
            <Skeleton className="h-24 w-full" />
          ) : (reportsQuery.data?.reports.length ?? 0) === 0 ? (
            <p className="text-sm text-foreground-muted">{t('delivery.reports.empty')}</p>
          ) : (
            <ul className="flex flex-col gap-3" aria-label={t('delivery.reports.title')}>
              {reportsQuery.data!.reports.map((r) => (
                <li key={r.id} className="rounded-md border border-border p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="flex flex-col">
                      <span className="text-sm font-medium text-foreground">{typeLabel(r.type)}</span>
                      <span className="text-xs text-foreground-muted">
                        {`${r.format} · v${r.version} · ${formatDate(r.createdAt, i18n.language) ?? ''}`}
                      </span>
                    </div>
                    <div className="flex items-center gap-2">
                      <ReportStatusBadge value={r.status} />
                      {r.approvedBy && (
                        <Badge variant="outline">
                          {t('delivery.reports.approvedByLabel', { name: r.approvedBy })}
                        </Badge>
                      )}
                    </div>
                  </div>

                  <div className="mt-2 flex flex-wrap gap-2">
                    <Button variant="outline" size="sm" onClick={() => handleView(r.id)}>
                      {t('delivery.reports.view')}
                    </Button>
                    {r.status === 'draft' && (
                      <Button
                        variant="outline"
                        size="sm"
                        onClick={() => approve.mutate({ reportId: r.id, input: {} })}
                        disabled={approve.isPending}
                      >
                        {t('delivery.reports.approve')}
                      </Button>
                    )}
                    {r.status === 'approved' && (
                      <Button
                        size="sm"
                        onClick={() => setSendingId(sendingId === r.id ? null : r.id)}
                      >
                        {t('delivery.reports.send')}
                      </Button>
                    )}
                  </div>

                  {sendingId === r.id && (
                    <SendForm deliveryId={deliveryId} reportId={r.id} onDone={() => setSendingId(null)} />
                  )}
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
