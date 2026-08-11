import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Camera } from 'lucide-react';

import type { AgentAccountRoster } from '@/api';
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { useAgentRoster } from '@/features/agents/hooks/use-agent-roster';
import { AgentIdentity } from '@/features/shared/components/agent-identity';
import { ImageCropEditor } from '@/features/shared/components/image-crop-editor';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { useUploadAgentPhoto } from '@/features/shared/hooks/use-leadership-profile';
import { canonicalPhotoAlias } from '@/features/shared/lib/agent-photo';
import { apiMode } from '@/config/features';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { usePresentationMode } from '@/app/presentation';

const STATE_VARIANT: Record<string, 'warning' | 'default' | 'success' | 'info' | 'error'> = {
  working: 'info',
  idle: 'success',
  'out-of-quota': 'error',
  cooldown: 'warning',
  'authentication-required': 'warning',
  degraded: 'warning',
  disabled: 'default',
};

function teamOf(roles: readonly string[]): string {
  if (roles.some((role) => role.includes('frontend') || role.includes('ui'))) return 'frontend';
  if (roles.some((role) => role.includes('critic') || role.includes('review'))) return 'quality';
  if (roles.some((role) => role.includes('chief'))) return 'leadership';
  return 'engineering';
}

/** Organograma da fleet global reutilizável, distinto das alocações de um projeto. */
export function FleetOrgChart() {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const roster = useAgentRoster();
  const accounts = roster.data ?? [];
  const leadership =
    accounts.find((account) => account.roles.some((role) => role.includes('chief'))) ??
    accounts[0] ??
    null;
  const specialists = accounts.filter((account) => account !== leadership);
  const groups = specialists.reduce<Map<string, AgentAccountRoster[]>>((result, account) => {
    const team = teamOf(account.roles);
    result.set(team, [...(result.get(team) ?? []), account]);
    return result;
  }, new Map());

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('agents.fleetTree.title')}</CardTitle>
        <p className="text-sm text-foreground-muted">{t('agents.fleetTree.help')}</p>
      </CardHeader>
      <CardContent className="min-w-0">
        {roster.isLoading ? (
          <Skeleton className="h-72 w-full" />
        ) : roster.isError ? (
          <p role="alert" className="text-sm text-error">
            {t('agents.roster.error')}
          </p>
        ) : leadership ? (
          <div
            role="group"
            aria-label={t('agents.fleetTree.title')}
            className="flex min-w-0 flex-col items-stretch gap-5"
          >
            <div className="mx-auto w-full max-w-sm">
              <PublicChiefNode account={leadership} />
            </div>
            {showTechnicalDetails ? (
              <>
                <div aria-hidden="true" className="mx-auto h-5 w-px bg-border-strong" />
                <div className="grid min-w-0 gap-4 lg:grid-cols-2 xl:grid-cols-4">
                  {[...groups.entries()].map(([team, members]) => (
                    <section
                      key={team}
                      className="flex min-w-0 flex-col gap-3 overflow-hidden rounded-lg border border-border p-3"
                    >
                      <h3 className="font-heading font-semibold">
                        {t(`agents.fleetTree.teams.${team}`)}
                      </h3>
                      {members.map((account) => (
                        <div key={account.alias} className="min-w-0">
                          <FleetNode account={account} />
                        </div>
                      ))}
                    </section>
                  ))}
                </div>
              </>
            ) : specialists.length > 0 ? (
              <Card className="border-dashed">
                <CardContent className="p-4 text-sm text-foreground-muted">
                  {t('agents.fleetTree.pendingProfiles', { count: specialists.length })}
                </CardContent>
              </Card>
            ) : null}
          </div>
        ) : (
          <p className="text-sm text-foreground-muted">{t('agents.roster.empty')}</p>
        )}
      </CardContent>
    </Card>
  );
}

function PublicChiefNode({ account }: { account: AgentAccountRoster }) {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  return (
    <article className="flex min-w-0 flex-col gap-3 overflow-hidden rounded-md border border-border bg-surface-elevated p-3">
      <div className="flex min-w-0 flex-wrap items-start justify-between gap-2">
        <AgentIdentity alias="chief-orchestrator" size={42} />
        <Badge variant={STATE_VARIANT[account.state] ?? 'default'}>
          {t(`agents.roster.state.${account.state}`, { defaultValue: account.state })}
        </Badge>
      </div>
      {showTechnicalDetails ? (
        <p className="min-w-0 break-words text-xs text-foreground-muted">
          {t('agents.fleetTree.backingAccount')}: {account.alias} · {account.providerKind}
        </p>
      ) : (
        <p className="text-xs text-foreground-muted">{t('agents.fleetTree.publicProfileHelp')}</p>
      )}
    </article>
  );
}

function FleetNode({ account }: { account: AgentAccountRoster }) {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const upload = useUploadAgentPhoto(canonicalPhotoAlias(account.alias));
  const identity = resolveAgentIdentity(account.alias);
  const [selectedPhoto, setSelectedPhoto] = useState<File | null>(null);
  const [selectionError, setSelectionError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  return (
    <>
      <article className="flex min-w-0 flex-col gap-3 overflow-hidden rounded-md border border-border bg-surface-elevated p-3">
        <div className="flex min-w-0 flex-wrap items-start justify-between gap-2">
          <AgentIdentity alias={account.alias} size={42} />
          <Badge variant={STATE_VARIANT[account.state] ?? 'default'}>
            {t(`agents.roster.state.${account.state}`, { defaultValue: account.state })}
          </Badge>
        </div>
        {showTechnicalDetails ? (
          <p className="min-w-0 break-words text-xs text-foreground-muted">
            {account.providerKind} · {account.roles.join(', ')}
          </p>
        ) : null}
        <dl className="grid min-w-0 grid-cols-3 gap-2 text-center">
          {(['completed', 'approved', 'rework'] as const).map((metric) => (
            <div key={metric} className="min-w-0 rounded bg-surface p-2">
              <dt className="break-words text-[11px] text-foreground-muted">
                {t(`agents.fleetTree.metrics.${metric}`)}
              </dt>
              <dd className="font-semibold">0</dd>
            </div>
          ))}
        </dl>
        <p className="text-[11px] text-foreground-muted">{t('agents.fleetTree.zeroSource')}</p>
        {apiMode === 'http' ? (
          <>
            <Button asChild variant="outline" size="sm" className="w-fit">
              <label className="cursor-pointer">
                <Camera aria-hidden="true" className="size-4" />
                {t('photoEditor.change')}
                <input
                  className="sr-only"
                  type="file"
                  accept="image/jpeg,image/png,image/webp"
                  disabled={upload.isPending}
                  onChange={(event) => {
                    const file = event.currentTarget.files?.[0];
                    event.currentTarget.value = '';
                    if (!file) return;
                    if (
                      !['image/jpeg', 'image/png', 'image/webp'].includes(file.type) ||
                      file.size > 5 * 1024 * 1024
                    ) {
                      setSelectionError(t('photoEditor.invalid'));
                      return;
                    }
                    upload.reset();
                    setSelectionError(null);
                    setSaved(false);
                    setSelectedPhoto(file);
                  }}
                />
              </label>
            </Button>
            {saved ? (
              <p role="status" className="text-xs font-medium text-success">
                {t('photoEditor.saved')}
              </p>
            ) : null}
          </>
        ) : null}
        {upload.isError || selectionError ? (
          <p role="alert" className="text-xs text-error">
            {upload.error?.message ?? selectionError}
          </p>
        ) : null}
      </article>
      {selectedPhoto ? (
        <ModalDialog
          label={t('photoEditor.cropTitle', {
            name: identity.humanName,
          })}
          onClose={() => {
            upload.reset();
            setSelectedPhoto(null);
          }}
          className="max-w-2xl"
        >
          <ImageCropEditor
            file={selectedPhoto}
            subjectName={identity.humanName}
            pending={upload.isPending}
            error={upload.error?.message ?? selectionError}
            onCancel={() => {
              upload.reset();
              setSelectedPhoto(null);
            }}
            onConfirm={async (photo) => {
              await upload.mutateAsync(photo);
              setSelectedPhoto(null);
              setSaved(true);
            }}
          />
        </ModalDialog>
      ) : null}
    </>
  );
}
