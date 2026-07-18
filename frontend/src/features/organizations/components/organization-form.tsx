import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';

import { type Organization } from '@/api';
import { Button, Card, CardContent, CardHeader, CardTitle, Field, Input, Select } from '@/design-system';
import { zodResolver } from '@/lib/form';
import { BrandFields } from '@/features/shared/components/brand-fields';
import {
  ORGANIZATION_PLANS,
  organizationFormSchema,
  type OrganizationFormValues,
} from '@/features/organizations/components/organization-form-schema';

export interface OrganizationFormProps {
  /** Presente em edição; ausente = criação. */
  initial?: Organization;
  submitting: boolean;
  onSubmit: (values: OrganizationFormValues) => void;
  onCancel: () => void;
}

/**
 * Criação/edição de organização. A marca usa o padrão do produto como
 * fonte de herança (campos vazios = herdados).
 */
export function OrganizationForm({ initial, submitting, onSubmit, onCancel }: OrganizationFormProps) {
  const { t } = useTranslation();
  const {
    register,
    control,
    handleSubmit,
    formState: { errors },
  } = useForm<OrganizationFormValues>({
    resolver: zodResolver(organizationFormSchema),
    defaultValues: initial
      ? {
          name: initial.name,
          slug: initial.slug,
          plan: (ORGANIZATION_PLANS as readonly string[]).includes(initial.plan)
            ? (initial.plan as OrganizationFormValues['plan'])
            : 'free',
          brand: initial.brand,
        }
      : {
          name: '',
          slug: '',
          plan: 'free',
          brand: { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
        },
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>
          {initial ? t('organizations.form.editTitle') : t('organizations.form.createTitle')}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} noValidate className="flex flex-col gap-6">
          <div className="grid gap-4 md:grid-cols-2">
            <Field
              htmlFor="org-name"
              label={t('organizations.form.name')}
              required
              requiredLabel={t('common.requiredMark')}
              error={errors.name ? t(errors.name.message!) : undefined}
            >
              <Input id="org-name" aria-invalid={Boolean(errors.name)} {...register('name')} />
            </Field>
            <Field
              htmlFor="org-slug"
              label={t('organizations.form.slug')}
              required
              requiredLabel={t('common.requiredMark')}
              hint={t('organizations.form.slugHint')}
              error={errors.slug ? t(errors.slug.message!) : undefined}
            >
              <Input id="org-slug" aria-invalid={Boolean(errors.slug)} {...register('slug')} />
            </Field>
            <Field htmlFor="org-plan" label={t('organizations.form.plan')}>
              <Select id="org-plan" {...register('plan')}>
                {ORGANIZATION_PLANS.map((plan) => (
                  <option key={plan} value={plan}>
                    {t(`organizations.plans.${plan}`)}
                  </option>
                ))}
              </Select>
            </Field>
          </div>

          <fieldset className="flex flex-col gap-3">
            <legend className="text-sm font-medium">{t('organizations.form.brandTitle')}</legend>
            <Controller
              control={control}
              name="brand"
              render={({ field }) => (
                <BrandFields
                  idPrefix="org-brand"
                  value={field.value}
                  onChange={field.onChange}
                  source="productDefault"
                />
              )}
            />
          </fieldset>

          <div className="flex flex-wrap gap-3">
            <Button type="submit" disabled={submitting}>
              {submitting
                ? t('common.states.loading')
                : initial
                  ? t('common.actions.save')
                  : t('organizations.form.create')}
            </Button>
            <Button type="button" variant="outline" onClick={onCancel}>
              {t('common.actions.cancel')}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
