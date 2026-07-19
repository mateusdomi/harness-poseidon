import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';

import {
  accountAuthenticationSchema,
  accountCapabilitySchema,
  accountPlanSchema,
  accountQuotaWindowSchema,
  ApiError,
  type Account,
  type Provider,
  type Ulid,
} from '@/api';
import { Button, Checkbox, Field, Input, Select } from '@/design-system';
import { zodResolver } from '@/lib/form';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  useCreateAccount,
  useUpdateAccount,
} from '@/features/providers/hooks/use-providers';

const accountFormSchema = z.object({
  providerId: z.string().min(1),
  label: z.string().trim().min(1, 'providers.accounts.form.labelRequired'),
  credentialReference: z
    .string()
    .refine(
      (value) => value === '' || /^(keychain|dpapi|secret):\/\/.+$/.test(value),
      'providers.accounts.form.credentialInvalid',
    ),
  identity: z.string(),
  plan: accountPlanSchema,
  authentication: accountAuthenticationSchema,
  quotaWindow: accountQuotaWindowSchema,
  quotaLimitUsd: z.string(),
  capabilities: z.array(accountCapabilitySchema),
});
type AccountFormValues = z.infer<typeof accountFormSchema>;

export interface AccountFormDialogProps {
  providers: Provider[];
  /** Conta em edição; ausente = criação (provider pré-selecionado pela seção). */
  account?: Account;
  initialProviderId: Ulid;
  onClose: () => void;
}

/**
 * Criação/edição de conta de provider (FR-5): a referência da credencial só
 * é enviada na criação e nunca reaparece no payload; identidade, plano,
 * autenticação, janela, limite e capacidades seguem os vocabulários fechados
 * do backend. Na edição o provider não pode ser trocado.
 */
export function AccountFormDialog({
  providers,
  account,
  initialProviderId,
  onClose,
}: AccountFormDialogProps) {
  const { t } = useTranslation();
  const createAccount = useCreateAccount();
  const updateAccount = useUpdateAccount();

  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<AccountFormValues>({
    resolver: zodResolver(accountFormSchema),
    defaultValues: {
      providerId: account?.providerId ?? initialProviderId,
      label: account?.label ?? '',
      credentialReference: '',
      identity: account?.identity ?? '',
      plan: account?.plan ?? 'unknown',
      authentication: account?.authentication ?? 'apiKey',
      quotaWindow: account?.quotaWindow ?? 'monthly',
      quotaLimitUsd: account?.quotaLimitUsd === null ? '' : String(account?.quotaLimitUsd ?? ''),
      capabilities: account?.capabilities ?? [],
    },
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  const mutation = account ? updateAccount : createAccount;

  function handleValidSubmit(values: AccountFormValues) {
    if (!account && values.credentialReference.trim() === '') {
      setError('credentialReference', {
        type: 'required',
        message: 'providers.accounts.form.credentialRequired',
      });
      return;
    }
    const identity = values.identity.trim() || null;
    const quotaLimitUsd = values.quotaLimitUsd.trim() === '' ? null : Number(values.quotaLimitUsd);
    if (account) {
      updateAccount.mutate(
        {
          id: account.id,
          input: {
            label: values.label.trim(),
            identity,
            plan: values.plan,
            authentication: values.authentication,
            quotaWindow: values.quotaWindow,
            quotaLimitUsd,
            capabilities: values.capabilities,
          },
        },
        { onSuccess: onClose },
      );
    } else {
      createAccount.mutate(
        {
          providerId: values.providerId,
          label: values.label.trim(),
          credentialReference: values.credentialReference.trim(),
          identity,
          plan: values.plan,
          authentication: values.authentication,
          quotaWindow: values.quotaWindow,
          quotaLimitUsd,
          capabilities: values.capabilities,
        },
        { onSuccess: onClose },
      );
    }
  }

  return (
    <ModalDialog
      label={account ? t('providers.accounts.form.editTitle') : t('providers.accounts.form.createTitle')}
      onClose={onClose}
    >
      <h3 className="font-heading text-lg font-semibold">
        {account ? t('providers.accounts.form.editTitle') : t('providers.accounts.form.createTitle')}
      </h3>
      <form
        className="flex flex-col gap-4"
        onSubmit={handleSubmit(handleValidSubmit)}
        noValidate
      >
        <Field htmlFor="account-provider" label={t('providers.accounts.form.provider')}>
          <Select id="account-provider" disabled={Boolean(account)} {...register('providerId')}>
            {providers.map((provider) => (
              <option key={provider.id} value={provider.id}>
                {provider.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field
          htmlFor="account-label"
          label={t('providers.accounts.form.label')}
          required
          requiredLabel={t('common.requiredMark')}
          error={errors.label ? t(errors.label.message!) : undefined}
        >
          <Input id="account-label" aria-invalid={Boolean(errors.label)} {...register('label')} />
        </Field>
        {!account && (
          <Field
            htmlFor="account-credential-reference"
            label={t('providers.accounts.form.credentialReference')}
            hint={t('providers.accounts.form.credentialHint')}
            required
            requiredLabel={t('common.requiredMark')}
            error={
              errors.credentialReference ? t(errors.credentialReference.message!) : undefined
            }
          >
            <Input
              id="account-credential-reference"
              type="password"
              autoComplete="off"
              aria-invalid={Boolean(errors.credentialReference)}
              {...register('credentialReference')}
            />
          </Field>
        )}
        <Field htmlFor="account-identity" label={t('providers.accounts.form.identity')}>
          <Input id="account-identity" type="email" {...register('identity')} />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field htmlFor="account-plan" label={t('providers.accounts.form.plan')}>
            <Select id="account-plan" {...register('plan')}>
              {accountPlanSchema.options.map((plan) => (
                <option key={plan} value={plan}>
                  {t(`providers.accounts.plan.${plan}`)}
                </option>
              ))}
            </Select>
          </Field>
          <Field
            htmlFor="account-authentication"
            label={t('providers.accounts.form.authentication')}
          >
            <Select id="account-authentication" {...register('authentication')}>
              {accountAuthenticationSchema.options.map((authentication) => (
                <option key={authentication} value={authentication}>
                  {t(`providers.accounts.authentication.${authentication}`)}
                </option>
              ))}
            </Select>
          </Field>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field htmlFor="account-quota-window" label={t('providers.accounts.form.quotaWindow')}>
            <Select id="account-quota-window" {...register('quotaWindow')}>
              {accountQuotaWindowSchema.options.map((window) => (
                <option key={window} value={window}>
                  {t(`providers.accounts.quotaWindow.${window}`)}
                </option>
              ))}
            </Select>
          </Field>
          <Field htmlFor="account-quota-limit" label={t('providers.accounts.form.quotaLimit')}>
            <Input
              id="account-quota-limit"
              type="number"
              min={0}
              step="0.01"
              {...register('quotaLimitUsd')}
            />
          </Field>
        </div>
        <fieldset className="flex flex-col gap-2">
          <legend className="text-sm font-medium">{t('providers.accounts.form.capabilities')}</legend>
          <Controller
            name="capabilities"
            control={control}
            render={({ field }) => (
              <div className="flex flex-wrap gap-x-4 gap-y-1">
                {accountCapabilitySchema.options.map((capability) => (
                  <label key={capability} className="flex min-h-11 items-center gap-2 text-sm">
                    <Checkbox
                      checked={field.value.includes(capability)}
                      onChange={() =>
                        field.onChange(
                          field.value.includes(capability)
                            ? field.value.filter((value) => value !== capability)
                            : [...field.value, capability],
                        )
                      }
                    />
                    {t(`providers.accounts.capability.${capability}`)}
                  </label>
                ))}
              </div>
            )}
          />
        </fieldset>
        {mutation.isError && (
          <p role="alert" className="text-sm text-error">
            {mutation.error instanceof ApiError
              ? mutation.error.problem.detail || mutation.error.problem.title
              : t('providers.accounts.form.error')}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={onClose}>
            {t('common.actions.cancel')}
          </Button>
          <Button type="submit" disabled={mutation.isPending}>
            {t('common.actions.save')}
          </Button>
        </div>
      </form>
    </ModalDialog>
  );
}
