import { ImageOff, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, Field, Input } from '@/design-system';

export function ProjectLogoField({
  currentLogoUrl,
  logoFile,
  onLogoFileChange,
  onClear,
}: {
  currentLogoUrl?: string | null;
  logoFile: File | null;
  onLogoFileChange: (file: File | null) => void;
  onClear: () => void;
}) {
  const { t } = useTranslation();
  const [localLogoUrl, setLocalLogoUrl] = useState<string | null>(null);
  const [logoBroken, setLogoBroken] = useState(false);

  useEffect(() => {
    if (!logoFile || typeof URL.createObjectURL !== 'function') {
      setLocalLogoUrl(null);
      return;
    }
    const url = URL.createObjectURL(logoFile);
    setLocalLogoUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [logoFile]);

  const effectiveLogo = localLogoUrl ?? currentLogoUrl ?? null;

  return (
    <div className="flex flex-col gap-3">
      <Field
        htmlFor="project-logo-file"
        label={t('common.brand.logo.upload')}
        hint={t('common.brand.logo.uploadHint')}
      >
        <Input
          id="project-logo-file"
          type="file"
          accept="image/png,image/jpeg"
          onChange={(event) => {
            setLogoBroken(false);
            onLogoFileChange(event.target.files?.[0] ?? null);
          }}
        />
      </Field>
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex size-20 shrink-0 items-center justify-center overflow-hidden rounded-md border border-border bg-surface-elevated">
          {effectiveLogo && !logoBroken ? (
            <img
              src={effectiveLogo}
              alt={t('common.brand.logo.previewAlt')}
              className="size-full object-contain"
              onError={() => setLogoBroken(true)}
            />
          ) : (
            <ImageOff aria-hidden="true" className="size-6 text-foreground-muted" />
          )}
        </div>
        <div className="flex flex-col gap-2">
          <p className="text-xs text-foreground-muted">
            {effectiveLogo
              ? logoBroken
                ? t('common.brand.logo.broken')
                : t('common.brand.logo.formats')
              : t('common.brand.logo.empty')}
          </p>
          {effectiveLogo ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="self-start"
              onClick={() => {
                onLogoFileChange(null);
                onClear();
                setLogoBroken(false);
              }}
            >
              <Trash2 aria-hidden="true" className="size-4" />
              {t('common.brand.logo.remove')}
            </Button>
          ) : null}
        </div>
      </div>
    </div>
  );
}
