import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { RefreshCw, ShieldAlert } from 'lucide-react';

import { SUPPORTED_LANGUAGES, persistLanguage, type SupportedLanguage } from '@/i18n';
import { product } from '@/config/product';
import { themeSchema, type BackupHandle, type ThemePreference } from '@/api';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Select,
  Skeleton,
} from '@/design-system';
import { licenseStateVariant } from '@/lib/status';
import { formatDateTime } from '@/lib/format';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import {
  useCreateBackup,
  useCurrentSettings,
  useDiagnostics,
  useLicenseSummary,
  useRestoreBackup,
  useUpdateSettings,
} from '@/features/settings/hooks/use-settings';
import {
  apiModeLabel,
  diagnosticKeyLabel,
  realtimeStateLabel,
  translateDiagnosticDetail,
} from '@/features/settings/lib/diagnostics-i18n';
import { useThemeStore } from '@/stores/theme-store';
import { usePresentationPolicy } from '@/app/presentation/use-presentation-policy';

const DIAGNOSTIC_VARIANTS = { ok: 'success', warning: 'warning', error: 'error' } as const;

/**
 * Configurações: idioma e tema (espelhando o shell + persistidos nas
 * settings), diretório de trabalho, sandbox/modo inseguro (aceite visível,
 * revogável com confirmação), backup/restore, diagnóstico, licença
 * resumida e sobre.
 */
export default function UsettingsPage() {
  const { t, i18n } = useTranslation();
  const settingsQuery = useCurrentSettings();
  const diagnosticsQuery = useDiagnostics();
  const licenseQuery = useLicenseSummary();
  const updateSettings = useUpdateSettings();
  const createBackup = useCreateBackup();
  const restoreBackup = useRestoreBackup();
  const presentation = usePresentationPolicy();

  const themePreference = useThemeStore((s) => s.preference);
  const setThemePreference = useThemeStore((s) => s.setPreference);

  const [workingDirectory, setWorkingDirectory] = useState('');
  const [confirmRevoke, setConfirmRevoke] = useState(false);
  const [confirmBackup, setConfirmBackup] = useState(false);
  const [confirmRestore, setConfirmRestore] = useState(false);
  const [lastBackup, setLastBackup] = useState<BackupHandle | null>(null);
  const [feedback, setFeedback] = useState<string | null>(null);

  const settings = settingsQuery.data ?? null;

  useEffect(() => {
    if (settings) setWorkingDirectory(settings.workingDirectory ?? '');
  }, [settings]);

  function update(input: Parameters<typeof updateSettings.mutate>[0]['input']) {
    if (!settings) return;
    updateSettings.mutate({ id: settings.id, input });
  }

  function changeLanguage(language: SupportedLanguage) {
    void i18n.changeLanguage(language);
    persistLanguage(language);
    update({ language });
  }

  function changeTheme(theme: ThemePreference) {
    setThemePreference(theme);
    update({ theme });
  }

  /** Rótulo pt-BR de um check de diagnóstico (fallback: código cru). */
  function checkKeyLabel(key: string): string {
    const label = diagnosticKeyLabel(key);
    return label ? t(label) : key;
  }

  /** Detalhe pt-BR de um check (fallback: string original do backend/mock). */
  function checkDetailLabel(detail: string): string {
    const translated = translateDiagnosticDetail(detail);
    return translated ? t(translated.key, translated.params) : detail;
  }

  /** Modo da API traduzido (fallback: valor cru). */
  function apiModeText(mode: string): string {
    const label = apiModeLabel(mode);
    return label ? t(label) : mode;
  }

  /** Estado de tempo real traduzido (fallback: valor cru). */
  function realtimeText(state: string): string {
    const label = realtimeStateLabel(state);
    return label ? t(label) : state;
  }

  const loading = settingsQuery.isLoading;
  const errored = settingsQuery.isError;

  if (loading) {
    return (
      <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-40 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }

  if (errored) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" onClick={() => void settingsQuery.refetch()}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <h1 className="font-heading text-2xl font-semibold">{t('features.settings.title')}</h1>

      <Card>
        <CardHeader>
          <CardTitle>{t('settings.preferences.title')}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <div className="flex flex-col gap-1">
            <label htmlFor="settings-language" className="text-sm font-medium">
              {t('settings.preferences.language')}
            </label>
            <Select
              id="settings-language"
              value={i18n.language}
              onChange={(event) => changeLanguage(event.target.value as SupportedLanguage)}
            >
              {SUPPORTED_LANGUAGES.map((language) => (
                <option key={language} value={language}>
                  {t(`shell.language.${language}`)}
                </option>
              ))}
            </Select>
          </div>
          <div className="flex flex-col gap-1">
            <label htmlFor="settings-theme" className="text-sm font-medium">
              {t('settings.preferences.theme')}
            </label>
            <Select
              id="settings-theme"
              value={themePreference}
              onChange={(event) => changeTheme(event.target.value as ThemePreference)}
            >
              {themeSchema.options.map((theme) => (
                <option key={theme} value={theme}>
                  {t(`settings.preferences.themeOptions.${theme}`)}
                </option>
              ))}
            </Select>
          </div>
          <div className="flex flex-col gap-1">
            <label htmlFor="settings-presentation" className="text-sm font-medium">
              {t('settings.presentation.label')}
            </label>
            <Select
              id="settings-presentation"
              value={presentation.mode}
              disabled={presentation.isPending}
              onChange={(event) =>
                presentation.setMode(
                  event.target.value as (typeof presentation.allowedModes)[number],
                )
              }
            >
              {presentation.allowedModes.map((mode) => (
                <option key={mode} value={mode}>
                  {t(`settings.presentation.modes.${mode}`)}
                </option>
              ))}
            </Select>
            <p className="text-xs text-foreground-muted">
              {t(`settings.presentation.help.${presentation.mode}`)}
            </p>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('settings.workspace.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <div className="flex flex-col gap-1">
            <label htmlFor="settings-workdir" className="text-sm font-medium">
              {t('settings.workspace.directory')}
            </label>
            <Input
              id="settings-workdir"
              value={workingDirectory}
              placeholder={t('settings.workspace.placeholder')}
              onChange={(event) => setWorkingDirectory(event.target.value)}
            />
            <p className="text-xs text-foreground-muted">{t('settings.workspace.hint')}</p>
            {settings?.workingDirectory ? (
              <p className="text-xs text-foreground-muted">
                {t('settings.workspace.current', { path: settings.workingDirectory })}
              </p>
            ) : (
              <p className="text-xs text-warning">{t('settings.workspace.empty')}</p>
            )}
          </div>
          <div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={updateSettings.isPending}
              onClick={() => update({ workingDirectory: workingDirectory.trim() || null })}
            >
              {t('common.actions.save')}
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex-row items-center gap-2">
          <ShieldAlert aria-hidden="true" className="size-5 text-warning" />
          <CardTitle>{t('settings.sandbox.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {settings?.unsafeModeAcceptedAt ? (
            <>
              <p className="text-sm">
                {t('settings.sandbox.acceptedAt', {
                  date: formatDateTime(settings.unsafeModeAcceptedAt),
                })}
              </p>
              <div>
                <Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRevoke(true)}>
                  {t('settings.sandbox.revoke')}
                </Button>
              </div>
            </>
          ) : (
            <p className="text-sm text-foreground-muted">{t('settings.sandbox.notAccepted')}</p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('settings.backup.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <p className="text-sm text-foreground-muted">{t('settings.backup.description')}</p>
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" size="sm" onClick={() => setConfirmBackup(true)}>
              {t('settings.backup.create')}
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={!lastBackup}
              onClick={() => setConfirmRestore(true)}
            >
              {t('settings.backup.restore')}
            </Button>
          </div>
          {feedback && (
            <p role="status" className="text-sm text-foreground-muted">
              {feedback}
            </p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex-row items-center justify-between gap-2">
          <CardTitle>{t('settings.diagnostics.title')}</CardTitle>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => void diagnosticsQuery.refetch()}
            aria-label={t('settings.diagnostics.refresh')}
            title={t('settings.diagnostics.refresh')}
          >
            <RefreshCw aria-hidden="true" />
          </Button>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {diagnosticsQuery.isLoading ? (
            <Skeleton className="h-24 w-full" />
          ) : diagnosticsQuery.isError ? (
            <p role="alert" className="text-sm text-error">
              {t('common.states.errorBody')}
            </p>
          ) : (
            <>
              <dl className="grid gap-2 text-sm sm:grid-cols-3">
                <div>
                  <dt className="font-medium">{t('settings.diagnostics.apiMode')}</dt>
                  <dd className="text-foreground-muted">
                    {apiModeText(diagnosticsQuery.data!.apiMode)}
                  </dd>
                </div>
                <div>
                  <dt className="font-medium">{t('settings.diagnostics.realtime')}</dt>
                  <dd className="text-foreground-muted">
                    {realtimeText(diagnosticsQuery.data!.realtimeState)}
                  </dd>
                </div>
                <div>
                  <dt className="font-medium">{t('settings.diagnostics.generatedAt')}</dt>
                  <dd className="text-foreground-muted">
                    {formatDateTime(diagnosticsQuery.data!.generatedAt)}
                  </dd>
                </div>
              </dl>
              <ul className="flex flex-col gap-2">
                {diagnosticsQuery.data!.checks.map((check) => (
                  <li key={check.key} className="flex items-center gap-2 text-sm">
                    <Badge variant={DIAGNOSTIC_VARIANTS[check.state]}>
                      {t(`settings.diagnostics.states.${check.state}`)}
                    </Badge>
                    <span className="font-medium">{checkKeyLabel(check.key)}</span>
                    <span className="text-foreground-muted">{checkDetailLabel(check.detail)}</span>
                  </li>
                ))}
              </ul>
            </>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('settings.license.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-wrap items-center gap-3">
          {licenseQuery.data ? (
            <>
              <span className="text-sm text-foreground-muted">{t('settings.license.status')}</span>
              <Badge variant={licenseStateVariant(licenseQuery.data.state)}>
                {t(`status.licenseState.${licenseQuery.data.state}`)}
              </Badge>
              {licenseQuery.data.state !== 'unlicensed' && licenseQuery.data.plan ? (
                <span className="text-sm">
                  {t('settings.license.plan', { plan: licenseQuery.data.plan })}
                </span>
              ) : null}
            </>
          ) : (
            <span className="text-sm text-foreground-muted">
              {t('settings.license.none')}
            </span>
          )}
          <Button asChild variant="outline" size="sm" className="ml-auto">
            <Link to="/licenses">{t('settings.license.open')}</Link>
          </Button>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('settings.about.title')}</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm">
            {t('settings.about.body', {
              name: product.name,
              version: product.version,
              codename: product.codename,
            })}
          </p>
        </CardContent>
      </Card>

      {confirmRevoke && (
        <ModalDialog label={t('settings.sandbox.revokeTitle')} onClose={() => setConfirmRevoke(false)}>
          <div className="flex flex-col gap-4">
            <p className="text-sm">{t('settings.sandbox.revokeBody')}</p>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => setConfirmRevoke(false)}>
                {t('common.actions.cancel')}
              </Button>
              <Button
                type="button"
                variant="destructive"
                disabled={updateSettings.isPending}
                onClick={() => {
                  update({ unsafeModeAcceptedAt: null });
                  setConfirmRevoke(false);
                }}
              >
                {t('settings.sandbox.revokeConfirm')}
              </Button>
            </div>
          </div>
        </ModalDialog>
      )}

      {confirmBackup && (
        <ModalDialog label={t('settings.backup.createTitle')} onClose={() => setConfirmBackup(false)}>
          <div className="flex flex-col gap-4">
            <p className="text-sm">{t('settings.backup.createBody')}</p>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => setConfirmBackup(false)}>
                {t('common.actions.cancel')}
              </Button>
              <Button
                type="button"
                disabled={createBackup.isPending}
                onClick={() => {
                  createBackup.mutate(undefined, {
                    onSuccess: (handle) => {
                      setLastBackup(handle);
                      setFeedback(
                        t('settings.backup.created', {
                          date: formatDateTime(handle.createdAt),
                          id: handle.backupId,
                        }),
                      );
                    },
                    onSettled: () => setConfirmBackup(false),
                  });
                }}
              >
                {t('settings.backup.createConfirm')}
              </Button>
            </div>
          </div>
        </ModalDialog>
      )}

      {confirmRestore && lastBackup && (
        <ModalDialog label={t('settings.backup.restoreTitle')} onClose={() => setConfirmRestore(false)}>
          <div className="flex flex-col gap-4">
            <p className="text-sm">
              {t('settings.backup.restoreBody', { date: formatDateTime(lastBackup.createdAt) })}
            </p>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => setConfirmRestore(false)}>
                {t('common.actions.cancel')}
              </Button>
              <Button
                type="button"
                variant="destructive"
                disabled={restoreBackup.isPending}
                onClick={() => {
                  restoreBackup.mutate(lastBackup.backupId, {
                    onSuccess: () => setFeedback(t('settings.backup.restored')),
                    onSettled: () => setConfirmRestore(false),
                  });
                }}
              >
                {t('settings.backup.restoreConfirm')}
              </Button>
            </div>
          </div>
        </ModalDialog>
      )}
    </div>
  );
}
