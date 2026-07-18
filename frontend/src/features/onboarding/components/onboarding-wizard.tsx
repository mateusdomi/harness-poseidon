import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';

import { themeSchema, type Profile } from '@/api';
import { useApi } from '@/app/api-context';
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Checkbox, Field, Input, Select } from '@/design-system';
import { SUPPORTED_LANGUAGES } from '@/i18n';
import { zodResolver } from '@/lib/form';
import { useThemeStore, type ThemePreference } from '@/stores/theme-store';
import { useCreateProfile } from '@/features/shared/hooks/use-profiles';
import {
  ONBOARDING_STEP_FIELDS,
  ONBOARDING_STEPS,
  onboardingFormSchema,
  type OnboardingFormValues,
} from '@/features/onboarding/components/onboarding-schema';

export interface OnboardingWizardProps {
  /** Chamado após criar o perfil e persistir as preferências. */
  onCompleted: (profile: Profile) => void;
  /** Cancelar volta para a seleção de perfil (só existe quando já há perfis). */
  onCancel?: () => void;
}

/**
 * Wizard de primeiro uso em etapas: perfil → preferências → diretório de
 * trabalho → aceite explícito do modo inseguro (sandbox indisponível).
 * O aceite é persistido em `settings.unsafeModeAcceptedAt`.
 */
export function OnboardingWizard({ onCompleted, onCancel }: OnboardingWizardProps) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const createProfile = useCreateProfile();
  const setThemePreference = useThemeStore((s) => s.setPreference);
  const [stepIndex, setStepIndex] = useState(0);
  const [submitError, setSubmitError] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    trigger,
    formState: { errors, isSubmitting },
  } = useForm<OnboardingFormValues>({
    resolver: zodResolver(onboardingFormSchema),
    defaultValues: {
      displayName: '',
      email: '',
      language: (i18n.language as OnboardingFormValues['language']) ?? 'pt-BR',
      theme: 'system',
      workingDirectory: '',
      // checkbox começa desmarcado — aceite deve ser explícito
      unsafeAccepted: undefined as unknown as true,
    },
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  const step = ONBOARDING_STEPS[stepIndex];
  const isLastStep = stepIndex === ONBOARDING_STEPS.length - 1;

  async function goNext() {
    const valid = await trigger(ONBOARDING_STEP_FIELDS[step]);
    if (valid) setStepIndex((index) => Math.min(index + 1, ONBOARDING_STEPS.length - 1));
  }

  function goBack() {
    setStepIndex((index) => Math.max(index - 1, 0));
  }

  async function onSubmit(values: OnboardingFormValues) {
    setSubmitError(null);
    try {
      const profile = await createProfile.mutateAsync({
        displayName: values.displayName,
        email: values.email === '' ? undefined : values.email,
        locale: values.language,
      });
      // O mock cria settings padrão junto com o perfil; aplicamos as escolhas
      // do wizard (tema, idioma, diretório, aceite do modo inseguro).
      const settingsPage = await api.list('settings', { filter: { profileId: profile.id } });
      const settings = settingsPage.items[0];
      if (settings) {
        await api.update('settings', settings.id, {
          theme: values.theme,
          language: values.language,
          workingDirectory: values.workingDirectory === '' ? null : values.workingDirectory,
          unsafeModeAcceptedAt: new Date().toISOString(),
        });
      }
      setThemePreference(values.theme as ThemePreference);
      void i18n.changeLanguage(values.language);
      onCompleted(profile);
    } catch {
      setSubmitError(t('common.states.errorBody'));
    }
  }

  return (
    <Card className="w-full max-w-xl">
      <CardHeader>
        <CardTitle>{t('onboarding.wizard.title')}</CardTitle>
        <CardDescription>
          {t('onboarding.wizard.step', { current: stepIndex + 1, total: ONBOARDING_STEPS.length })}
        </CardDescription>
        <ol className="flex flex-wrap gap-2 pt-2" aria-label={t('onboarding.wizard.stepsLabel')}>
          {ONBOARDING_STEPS.map((stepKey, index) => (
            <li
              key={stepKey}
              aria-current={index === stepIndex ? 'step' : undefined}
              className={
                index === stepIndex
                  ? 'rounded-full border border-accent px-3 py-1 text-xs font-medium text-accent'
                  : 'rounded-full border border-border px-3 py-1 text-xs text-foreground-muted'
              }
            >
              {t(`onboarding.wizard.steps.${stepKey}`)}
            </li>
          ))}
        </ol>
      </CardHeader>
      <CardContent>
        <form
          onSubmit={handleSubmit(onSubmit)}
          noValidate
          aria-label={t('onboarding.wizard.title')}
        >
          <div role="group" aria-label={t(`onboarding.wizard.steps.${step}`)}>
            {step === 'profile' && (
              <div className="flex flex-col gap-4">
                <Field
                  htmlFor="onboarding-displayName"
                  label={t('onboarding.wizard.profile.name')}
                  required
                  requiredLabel={t('common.requiredMark')}
                  error={errors.displayName ? t(errors.displayName.message!) : undefined}
                >
                  <Input
                    id="onboarding-displayName"
                    autoComplete="name"
                    aria-invalid={Boolean(errors.displayName)}
                    {...register('displayName')}
                  />
                </Field>
                <Field
                  htmlFor="onboarding-email"
                  label={t('onboarding.wizard.profile.email')}
                  error={errors.email ? t(errors.email.message!) : undefined}
                >
                  <Input
                    id="onboarding-email"
                    type="email"
                    autoComplete="email"
                    aria-invalid={Boolean(errors.email)}
                    {...register('email')}
                  />
                </Field>
              </div>
            )}

            {step === 'preferences' && (
              <div className="flex flex-col gap-4">
                <Field htmlFor="onboarding-language" label={t('onboarding.wizard.preferences.language')}>
                  <Select id="onboarding-language" {...register('language')}>
                    {SUPPORTED_LANGUAGES.map((language) => (
                      <option key={language} value={language}>
                        {t(`shell.language.${language}`)}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field htmlFor="onboarding-theme" label={t('onboarding.wizard.preferences.theme.label')}>
                  <Select id="onboarding-theme" {...register('theme')}>
                    {themeSchema.options.map((theme) => (
                      <option key={theme} value={theme}>
                        {t(`onboarding.wizard.preferences.theme.options.${theme}`)}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
            )}

            {step === 'workspace' && (
              <Field
                htmlFor="onboarding-workingDirectory"
                label={t('onboarding.wizard.workspace.directory')}
                hint={t('onboarding.wizard.workspace.hint')}
              >
                <Input
                  id="onboarding-workingDirectory"
                  placeholder={t('onboarding.wizard.workspace.directoryPlaceholder')}
                  {...register('workingDirectory')}
                />
              </Field>
            )}

            {step === 'safety' && (
              <div className="flex flex-col gap-4">
                <div
                  role="alert"
                  className="rounded-md border border-warning bg-surface-elevated p-4 text-sm text-foreground"
                >
                  <p className="font-medium text-warning">{t('onboarding.wizard.safety.title')}</p>
                  <p className="mt-1 text-foreground-muted">{t('onboarding.wizard.safety.warning')}</p>
                </div>
                <div className="flex flex-col gap-1.5">
                  <div className="flex items-start gap-3">
                    <Checkbox
                      id="onboarding-unsafeAccepted"
                      aria-invalid={Boolean(errors.unsafeAccepted)}
                      aria-describedby={
                        errors.unsafeAccepted ? 'onboarding-unsafeAccepted-error' : undefined
                      }
                      {...register('unsafeAccepted')}
                    />
                    <label htmlFor="onboarding-unsafeAccepted" className="text-sm text-foreground">
                      {t('onboarding.wizard.safety.accept')}
                    </label>
                  </div>
                  {errors.unsafeAccepted ? (
                    <p id="onboarding-unsafeAccepted-error" role="alert" className="text-xs text-error">
                      {t(errors.unsafeAccepted.message!)}
                    </p>
                  ) : null}
                </div>
              </div>
            )}
          </div>

          {submitError ? (
            <p role="alert" className="mt-4 text-sm text-error">
              {submitError}
            </p>
          ) : null}

          <div className="mt-6 flex flex-wrap items-center gap-3">
            {stepIndex > 0 ? (
              <Button type="button" variant="outline" onClick={goBack}>
                {t('common.actions.previous')}
              </Button>
            ) : null}
            {stepIndex === 0 && onCancel ? (
              <Button type="button" variant="ghost" onClick={onCancel}>
                {t('common.actions.cancel')}
              </Button>
            ) : null}
            <div className="ms-auto">
              {isLastStep ? (
                <Button type="submit" disabled={isSubmitting}>
                  {isSubmitting ? t('common.states.loading') : t('common.actions.finish')}
                </Button>
              ) : (
                <Button type="button" onClick={goNext}>
                  {t('common.actions.next')}
                </Button>
              )}
            </div>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
