import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronRight, ImageOff, Trash2 } from 'lucide-react';

import type { Brand } from '@/api';
import { Button, Field, Input, Select } from '@/design-system';
import {
  InheritanceBadge,
  type InheritanceSource,
} from '@/features/shared/components/inheritance-badge';

export interface BrandFieldsProps {
  value: Brand;
  onChange: (brand: Brand) => void;
  /** Marca herdada (da organização); omitida quando a fonte é o padrão do produto. */
  inheritedBrand?: Brand;
  source: InheritanceSource;
  /** Prefixo único para ids de campo (acessibilidade). */
  idPrefix: string;
  /** Erros já traduzidos por campo. */
  errors?: Partial<Record<keyof Brand, string>>;
  logoFile?: File | null;
  onLogoFileChange?: (file: File | null) => void;
}

/**
 * Presets de tipografia explicados (§9). Os valores são as famílias que o
 * design system realmente carrega — não prometemos fontes inexistentes.
 */
const TYPOGRAPHY_PRESETS = ['Inter', 'Space Grotesk'] as const;

/** Padrões Poseidon usados como referência visual quando nada foi definido. */
const POSEIDON_DEFAULTS = {
  primaryColor: '#6D5EF8',
  secondaryColor: '#F45BD8',
} as const;

const HEX_PATTERN = /^#(?:[0-9A-Fa-f]{6})$/;

/**
 * Editor de marca (§9) com herança explícita: campo vazio = herda (da
 * organização ou do padrão do produto), preenchido = sobrescrito.
 *
 * Projetos podem fornecer `onLogoFileChange`: o arquivo é pré-visualizado e
 * enviado ao armazenamento gerenciado depois que o projeto é salvo. A URL
 * externa continua disponível na seção avançada e a organização pode usar
 * apenas essa modalidade quando não houver endpoint de asset.
 *
 * Para v1 são exatamente DUAS cores canônicas (primária e secundária), como
 * o contrato define — sem editor livre de paleta.
 */
export function BrandFields({
  value,
  onChange,
  inheritedBrand,
  source,
  idPrefix,
  errors,
  logoFile = null,
  onLogoFileChange,
}: BrandFieldsProps) {
  const { t } = useTranslation();
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [logoBroken, setLogoBroken] = useState(false);
  const [localLogoUrl, setLocalLogoUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!logoFile || typeof URL.createObjectURL !== 'function') {
      setLocalLogoUrl(null);
      return;
    }
    const url = URL.createObjectURL(logoFile);
    setLocalLogoUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [logoFile]);

  function patch(partial: Partial<Brand>) {
    onChange({ ...value, ...partial });
  }

  /** Cor efetiva para prévia: valor → herdado → padrão Poseidon. */
  function effectiveColor(field: 'primaryColor' | 'secondaryColor'): string {
    const candidate = value[field] ?? inheritedBrand?.[field] ?? POSEIDON_DEFAULTS[field];
    return HEX_PATTERN.test(candidate) ? candidate : POSEIDON_DEFAULTS[field];
  }

  const effectiveLogo = localLogoUrl ?? value.logoUrl ?? inheritedBrand?.logoUrl ?? null;
  const effectiveTypography = value.typography ?? inheritedBrand?.typography ?? TYPOGRAPHY_PRESETS[0];

  return (
    <div className="flex flex-col gap-5">
      <p className="text-sm text-foreground-muted">
        {source === 'organization' ? t('common.brand.hintOrg') : t('common.brand.hintDefault')}
      </p>

      {/* ---- Logo: prévia + remoção. URL fica na seção avançada. ---- */}
      <div className="flex flex-col gap-2">
        <span className="text-sm font-medium">{t('common.brand.logo.label')}</span>
        {onLogoFileChange ? (
          <Field
            htmlFor={`${idPrefix}-logo-file`}
            label={t('common.brand.logo.upload')}
            hint={t('common.brand.logo.uploadHint')}
          >
            <Input
              id={`${idPrefix}-logo-file`}
              type="file"
              accept="image/png,image/jpeg"
              onChange={(event) => {
                setLogoBroken(false);
                onLogoFileChange(event.target.files?.[0] ?? null);
              }}
            />
          </Field>
        ) : null}
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
          <div className="flex flex-col gap-1">
            <p className="text-xs text-foreground-muted">
              {effectiveLogo
                ? logoBroken
                  ? t('common.brand.logo.broken')
                  : t('common.brand.logo.formats')
                : t('common.brand.logo.empty')}
            </p>
            {value.logoUrl || logoFile ? (
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="self-start"
                onClick={() => {
                  patch({ logoUrl: null });
                  onLogoFileChange?.(null);
                  setLogoBroken(false);
                }}
              >
                <Trash2 aria-hidden="true" className="size-4" />
                {t('common.brand.logo.remove')}
              </Button>
            ) : null}
          </div>
        </div>
        <InheritanceBadge inherited={value.logoUrl === null} source={source} />
      </div>

      {/* ---- Cores canônicas: seletor + HEX sincronizados ---- */}
      <div className="grid gap-4 md:grid-cols-2">
        {(['primaryColor', 'secondaryColor'] as const).map((fieldKey) => {
          const id = `${idPrefix}-${fieldKey}`;
          const current = value[fieldKey];
          return (
            <div key={fieldKey} className="flex flex-col gap-1">
              <Field
                htmlFor={`${id}-hex`}
                label={t(`common.brand.fields.${fieldKey}`)}
                error={errors?.[fieldKey]}
              >
                <div className="flex items-center gap-2">
                  <input
                    type="color"
                    id={id}
                    aria-label={t('common.brand.colors.pickerLabel', {
                      field: t(`common.brand.fields.${fieldKey}`),
                    })}
                    value={effectiveColor(fieldKey)}
                    onChange={(event) => patch({ [fieldKey]: event.target.value } as Partial<Brand>)}
                    className="size-11 shrink-0 cursor-pointer rounded-md border border-border-strong bg-surface p-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
                  />
                  <Input
                    id={`${id}-hex`}
                    value={current ?? ''}
                    placeholder={inheritedBrand?.[fieldKey] ?? POSEIDON_DEFAULTS[fieldKey]}
                    aria-invalid={Boolean(errors?.[fieldKey])}
                    onChange={(event) =>
                      patch({
                        [fieldKey]: event.target.value === '' ? null : event.target.value,
                      } as Partial<Brand>)
                    }
                  />
                </div>
              </Field>
              <InheritanceBadge inherited={current === null} source={source} />
            </div>
          );
        })}
      </div>

      {/* ---- Tipografia como presets explicados ---- */}
      <div className="flex flex-col gap-1">
        <Field
          htmlFor={`${idPrefix}-typography`}
          label={t('common.brand.fields.typography')}
          hint={t('common.brand.typography.hint')}
          error={errors?.typography}
        >
          <Select
            id={`${idPrefix}-typography`}
            value={value.typography ?? ''}
            onChange={(event) =>
              patch({ typography: event.target.value === '' ? null : event.target.value })
            }
          >
            <option value="">{t('common.brand.typography.inherit')}</option>
            {TYPOGRAPHY_PRESETS.map((preset) => (
              <option key={preset} value={preset}>
                {t(`common.brand.typography.presets.${preset}`, { defaultValue: preset })}
              </option>
            ))}
          </Select>
        </Field>
        <InheritanceBadge inherited={value.typography === null} source={source} />
      </div>

      {/* ---- Prévia da marca ---- */}
      <div className="flex flex-col gap-2">
        <span className="text-sm font-medium">{t('common.brand.preview.title')}</span>
        <div
          className="flex flex-wrap items-center gap-3 rounded-lg border border-border p-3"
          style={{ fontFamily: effectiveTypography }}
        >
          <span
            aria-hidden="true"
            className="size-8 shrink-0 rounded-md"
            style={{ backgroundColor: effectiveColor('primaryColor') }}
          />
          <span
            aria-hidden="true"
            className="size-8 shrink-0 rounded-md"
            style={{ backgroundColor: effectiveColor('secondaryColor') }}
          />
          <span className="text-sm font-medium">{t('common.brand.preview.sample')}</span>
        </div>
      </div>

      {/* ---- Avançado: URL externa da logo ---- */}
      <div className="rounded-lg border border-border">
        <button
          type="button"
          onClick={() => setAdvancedOpen((open) => !open)}
          aria-expanded={advancedOpen || Boolean(errors?.logoUrl)}
          className="flex min-h-touch w-full items-center gap-2 px-4 py-2 text-sm font-medium text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
        >
          {advancedOpen || errors?.logoUrl ? (
            <ChevronDown aria-hidden="true" className="size-4" />
          ) : (
            <ChevronRight aria-hidden="true" className="size-4" />
          )}
          {t('common.brand.advanced')}
        </button>
        {advancedOpen || errors?.logoUrl ? (
          <div className="border-t border-border p-4">
            <Field
              htmlFor={`${idPrefix}-logoUrl`}
              label={t('common.brand.fields.logoUrl')}
              hint={t('common.brand.logo.urlHint')}
              error={errors?.logoUrl}
            >
              <Input
                id={`${idPrefix}-logoUrl`}
                value={value.logoUrl ?? ''}
                placeholder={inheritedBrand?.logoUrl ?? t('common.brand.emptyPlaceholder')}
                aria-invalid={Boolean(errors?.logoUrl)}
                onChange={(event) => {
                  setLogoBroken(false);
                  patch({ logoUrl: event.target.value === '' ? null : event.target.value });
                }}
              />
            </Field>
          </div>
        ) : null}
      </div>
    </div>
  );
}
