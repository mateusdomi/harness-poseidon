import { useEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronRight } from 'lucide-react';

import { type Organization } from '@/api';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Field,
  Input,
} from '@/design-system';
import { zodResolver } from '@/lib/form';
import { slugify } from '@/lib/utils';
import { BrandFields } from '@/features/shared/components/brand-fields';
import {
  organizationFormSchema,
  type OrganizationFormValues,
} from '@/features/organizations/components/organization-form-schema';
import { usePresentationMode } from '@/app/presentation';

export interface OrganizationFormProps {
  /** Presente em edição; ausente = criação. */
  initial?: Organization;
  submitting: boolean;
  onSubmit: (values: OrganizationFormValues) => void;
  onCancel: () => void;
}

/**
 * Criação/edição de organização.
 * - Slug ("Identificador da URL") é gerado automaticamente do nome e só fica
 *   editável no modo Técnico — o usuário não decide no fluxo comum (§7).
 * - O plano NÃO é escolhido aqui: é derivado da licença e mostrado read-only
 *   na edição (§8).
 * - A marca usa o padrão do produto como fonte de herança.
 */
export function OrganizationForm({
  initial,
  submitting,
  onSubmit,
  onCancel,
}: OrganizationFormProps) {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  // Em edição o slug já existe e é do usuário; em criação, geramos do nome até
  // que ele edite manualmente.
  const slugEditedRef = useRef(Boolean(initial));
  const [advancedOpen, setAdvancedOpen] = useState(false);

  const {
    register,
    control,
    handleSubmit,
    setValue,
    watch,
    trigger,
    formState: { errors },
  } = useForm<OrganizationFormValues>({
    resolver: zodResolver(organizationFormSchema),
    defaultValues: initial
      ? { name: initial.name, slug: initial.slug, brand: initial.brand }
      : {
          name: '',
          slug: '',
          brand: { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
        },
    mode: 'onSubmit',
    reValidateMode: 'onChange',
  });

  const nameValue = watch('name');
  const slugValue = watch('slug');

  // Auto-gera o slug a partir do nome enquanto o usuário não o editou.
  useEffect(() => {
    if (!slugEditedRef.current) {
      setValue('slug', slugify(nameValue ?? ''), { shouldValidate: false });
    }
  }, [nameValue, setValue]);

  const slugReg = register('slug');
  const slugError = errors.slug ? t(errors.slug.message!) : undefined;

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

            {initial && showTechnicalDetails ? (
              <Field
                htmlFor="org-plan"
                label={t('organizations.form.plan')}
                hint={t('organizations.form.planManaged')}
              >
                <div id="org-plan" className="flex h-11 items-center">
                  <Badge variant="outline">
                    {t(`organizations.plans.${initial.plan}`, { defaultValue: initial.plan })}
                  </Badge>
                </div>
              </Field>
            ) : null}
          </div>

          {/* Seção avançada: identificador da URL (slug), gerado do nome. */}
          {showTechnicalDetails && (
            <div className="rounded-lg border border-border">
              <button
                type="button"
                onClick={() => setAdvancedOpen((open) => !open)}
                aria-expanded={advancedOpen || Boolean(slugError)}
                className="flex min-h-touch w-full items-center gap-2 px-4 py-2 text-sm font-medium text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
              >
                {advancedOpen || slugError ? (
                  <ChevronDown aria-hidden="true" className="size-4" />
                ) : (
                  <ChevronRight aria-hidden="true" className="size-4" />
                )}
                {t('organizations.form.advanced')}
              </button>
              {advancedOpen || slugError ? (
                <div className="border-t border-border p-4">
                  <Field
                    htmlFor="org-slug"
                    label={t('organizations.form.slugLabel')}
                    required
                    requiredLabel={t('common.requiredMark')}
                    hint={t('organizations.form.slugHelper')}
                    error={slugError}
                  >
                    <Input
                      id="org-slug"
                      aria-invalid={Boolean(errors.slug)}
                      {...slugReg}
                      onChange={(event) => {
                        slugEditedRef.current = true;
                        void slugReg.onChange(event);
                        void trigger('slug');
                      }}
                    />
                  </Field>
                  <p className="mt-1.5 text-xs text-foreground-muted">
                    {t('organizations.form.slugPreview', {
                      slug: slugValue || t('organizations.form.slugEmpty'),
                    })}
                  </p>
                </div>
              ) : null}
            </div>
          )}

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
