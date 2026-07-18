import { useTranslation } from 'react-i18next';

import type { Brand } from '@/api';
import { Field, Input } from '@/design-system';
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
}

const BRAND_FIELD_KEYS = ['logoUrl', 'primaryColor', 'secondaryColor', 'typography'] as const;

/**
 * Editor de marca com herança explícita: campo vazio = herda (da
 * organização ou do padrão do produto), preenchido = sobrescrito.
 * O valor herdado aparece como placeholder — nada essencial só no hover.
 */
export function BrandFields({
  value,
  onChange,
  inheritedBrand,
  source,
  idPrefix,
  errors,
}: BrandFieldsProps) {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-foreground-muted">
        {source === 'organization'
          ? t('common.brand.hintOrg')
          : t('common.brand.hintDefault')}
      </p>
      {BRAND_FIELD_KEYS.map((fieldKey) => {
        const current = value[fieldKey];
        const inherited = inheritedBrand?.[fieldKey] ?? null;
        const id = `${idPrefix}-${fieldKey}`;
        return (
          <div key={fieldKey} className="flex flex-col gap-1">
            <Field htmlFor={id} label={t(`common.brand.fields.${fieldKey}`)} error={errors?.[fieldKey]}>
              <Input
                id={id}
                value={current ?? ''}
                placeholder={inherited ?? t('common.brand.emptyPlaceholder')}
                onChange={(event) =>
                  onChange({
                    ...value,
                    [fieldKey]: event.target.value === '' ? null : event.target.value,
                  })
                }
              />
            </Field>
            <div>
              <InheritanceBadge inherited={current === null} source={source} />
            </div>
          </div>
        );
      })}
    </div>
  );
}
