import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Check, X } from 'lucide-react';

import { ApiError, activateLicenseInputSchema } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Input, Skeleton } from '@/design-system';
import { licenseStateVariant } from '@/lib/status';
import { formatDate, formatDateTime, formatNumber } from '@/lib/format';
import { zodResolver } from '@/lib/form';
import { useActivateLicense, useEntitlements, useLicense } from '@/features/licenses/hooks/use-licenses';

/** Form local: mesmo formato do contrato (mensagens como chaves i18n). */
const activationFormSchema = z.object({
  key: activateLicenseInputSchema.shape.key,
});
type ActivationFormValues = z.infer<typeof activationFormSchema>;

/**
 * Licença do dispositivo: estado (enum do contrato), entitlements,
 * dispositivo, expiração, grace period, modo offline e ativação por chave.
 * Aviso permanente: após a expiração, leitura e exportação continuam.
 */
export default function UlicensesPage() {
  const { t } = useTranslation();
  const licenseQuery = useLicense();
  const entitlementsQuery = useEntitlements();
  const activate = useActivateLicense();

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<ActivationFormValues>({
    resolver: zodResolver(activationFormSchema),
    defaultValues: { key: '' },
  });

  const license = licenseQuery.data ?? null;
  const loading = licenseQuery.isLoading || entitlementsQuery.isLoading;
  const errored = licenseQuery.isError || entitlementsQuery.isError;

  async function submit(values: ActivationFormValues) {
    try {
      await activate.mutateAsync({ key: values.key });
      reset();
    } catch {
      // Erro exibido via activate.error abaixo.
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <h1 className="font-heading text-2xl font-semibold">{t('features.licenses.title')}</h1>

      {loading ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-40 w-full" />
          <Skeleton className="h-64 w-full" />
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button
            type="button"
            variant="outline"
            onClick={() => {
              void licenseQuery.refetch();
              void entitlementsQuery.refetch();
            }}
          >
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : (
        <>
          <p className="rounded-md border border-border bg-surface p-3 text-sm">
            {t('licenses.postExpiryNotice')}
          </p>

          <Card>
            <CardHeader className="flex-row flex-wrap items-center gap-3">
              <CardTitle>{t('licenses.state.title')}</CardTitle>
              <Badge variant={licenseStateVariant(license?.state ?? 'unlicensed')}>
                {t(`status.licenseState.${license?.state ?? 'unlicensed'}`)}
              </Badge>
            </CardHeader>
            <CardContent>
              {license ? (
                <dl className="grid gap-2 text-sm sm:grid-cols-2">
                  <div>
                    <dt className="font-medium">{t('licenses.state.plan')}</dt>
                    <dd className="text-foreground-muted">{license.plan}</dd>
                  </div>
                  <div>
                    <dt className="font-medium">{t('licenses.state.device')}</dt>
                    <dd className="text-foreground-muted">
                      {license.deviceName} ({license.deviceId})
                    </dd>
                  </div>
                  <div>
                    <dt className="font-medium">{t('licenses.state.expiresAt')}</dt>
                    <dd className="text-foreground-muted">
                      {license.expiresAt
                        ? formatDate(license.expiresAt)
                        : t('licenses.state.noExpiry')}
                    </dd>
                  </div>
                  {license.gracePeriodEndsAt && (
                    <div>
                      <dt className="font-medium">{t('licenses.state.gracePeriod')}</dt>
                      <dd className="text-foreground-muted">
                        {formatDate(license.gracePeriodEndsAt)}
                      </dd>
                    </div>
                  )}
                  <div>
                    <dt className="font-medium">{t('licenses.state.offlineMode')}</dt>
                    <dd>
                      <Badge variant={license.offlineMode ? 'warning' : 'outline'}>
                        {license.offlineMode
                          ? t('licenses.state.offlineYes')
                          : t('licenses.state.offlineNo')}
                      </Badge>
                    </dd>
                  </div>
                  <div>
                    <dt className="font-medium">{t('licenses.state.lastValidated')}</dt>
                    <dd className="text-foreground-muted">
                      {license.lastValidatedAt ? formatDateTime(license.lastValidatedAt) : '—'}
                    </dd>
                  </div>
                </dl>
              ) : (
                <p className="text-sm text-foreground-muted">{t('licenses.state.none')}</p>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t('licenses.activation.title')}</CardTitle>
            </CardHeader>
            <CardContent>
              <form
                className="flex flex-col gap-3"
                onSubmit={handleSubmit((values) => void submit(values))}
              >
                <div className="flex flex-col gap-1">
                  <label htmlFor="license-key" className="text-sm font-medium">
                    {t('licenses.activation.keyLabel')}
                  </label>
                  <Input
                    id="license-key"
                    placeholder={t('licenses.activation.keyPlaceholder')}
                    aria-invalid={errors.key ? true : undefined}
                    {...register('key')}
                  />
                  {errors.key && (
                    <p role="alert" className="text-sm text-error">
                      {t('licenses.activation.invalidFormat')}
                    </p>
                  )}
                </div>
                {activate.isError && (
                  <p role="alert" className="text-sm text-error">
                    {activate.error instanceof ApiError
                      ? activate.error.problem.detail || activate.error.problem.title
                      : t('licenses.activation.error')}
                  </p>
                )}
                {activate.isSuccess && (
                  <p role="status" className="text-sm text-success">
                    {t('licenses.activation.success')}
                  </p>
                )}
                <div>
                  <Button type="submit" disabled={activate.isPending}>
                    {t('licenses.activation.submit')}
                  </Button>
                </div>
              </form>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t('licenses.entitlements.title')}</CardTitle>
            </CardHeader>
            <CardContent>
              {(entitlementsQuery.data ?? []).length === 0 ? (
                <p className="text-sm text-foreground-muted">
                  {t('licenses.entitlements.empty')}
                </p>
              ) : (
                <ul className="flex flex-col gap-2">
                  {(entitlementsQuery.data ?? []).map((entitlement) => (
                    <li
                      key={entitlement.id}
                      className="flex items-center gap-3 rounded-md border border-border p-3 text-sm"
                    >
                      {entitlement.included ? (
                        <Check aria-hidden="true" className="size-4 shrink-0 text-success" />
                      ) : (
                        <X aria-hidden="true" className="size-4 shrink-0 text-error" />
                      )}
                      <span className="sr-only">
                        {entitlement.included
                          ? t('licenses.entitlements.included')
                          : t('licenses.entitlements.notIncluded')}
                      </span>
                      <div className="flex-1">
                        <p className="font-medium">{entitlement.key}</p>
                        <p className="text-foreground-muted">{entitlement.description}</p>
                      </div>
                      {entitlement.limit !== null && (
                        <Badge variant="outline">{formatNumber(entitlement.limit)}</Badge>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>
        </>
      )}
    </div>
  );
}
