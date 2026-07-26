import { z } from 'zod';

import {
  prioritySchema,
  projectStateSchema,
  repositoryProviderSchema,
  type Project,
} from '@/api';

/* ---- schemas por aba (mensagens = CHAVES i18n, traduzidas na render) ---- */

export const organizationSchema = z.object({
  organizationId: z.string().min(1, 'common.validation.required'),
});

export const identitySchema = z.object({
  name: z.string().trim().min(2, 'common.validation.min2'),
  key: z.string().trim().regex(/^[A-Z0-9]{2,12}$/, 'common.validation.key'),
});

export const objectiveSchema = z.object({
  description: z.string().trim().min(1, 'common.validation.required'),
});

export const criticalitySchema = z.object({
  criticality: prioritySchema,
});

export const advancedSchema = z.object({
  state: projectStateSchema,
});

export const repositorySchema = z.object({
  repositoryProvider: repositoryProviderSchema,
  repositoryUrl: z.string().trim(),
  defaultBranch: z.string().trim().min(1, 'common.validation.required'),
});

/**
 * Validação cruzada da aba Repositório: o rótulo promete "URL ou caminho local" e o provedor
 * 'local' existe exatamente para repositórios em disco — nesse caso um caminho ABSOLUTO é
 * válido. Para provedores remotos, exige-se URL. Vazio é sempre permitido (projeto ainda sem
 * repositório). Mantida FORA do objeto base porque `.merge()` não aceita ZodEffects.
 */
export const repositoryTabSchema = repositorySchema.superRefine((value, context) => {
  if (value.repositoryUrl.length === 0) return;
  const isAbsolutePath = value.repositoryUrl.startsWith('/');
  const isUrl = z.string().url().safeParse(value.repositoryUrl).success;
  const valid = value.repositoryProvider === 'local' ? isAbsolutePath || isUrl : isUrl;
  if (!valid) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      message: 'common.validation.url',
      path: ['repositoryUrl'],
    });
  }
});

export const technologiesSchema = z.object({
  technologies: z.array(z.string().trim().min(1)),
});

export const workflowSchema = z.object({
  workflowTemplateId: z.string(),
});

const hexColorSchema = z.string().regex(/^#(?:[0-9A-Fa-f]{6})$/, 'common.validation.color');

export const brandTabSchema = z.object({
  brand: z.object({
    logoUrl: z.union([z.string().url('common.validation.url'), z.null()]),
    primaryColor: z.union([hexColorSchema, z.null()]),
    secondaryColor: z.union([hexColorSchema, z.null()]),
    typography: z.union([z.string(), z.null()]),
  }),
});

export const peopleSchema = z.object({
  memberProfileIds: z.array(z.string()).min(1, 'projects.form.people.required'),
});

export const projectFormSchema = organizationSchema
  .merge(identitySchema)
  .merge(objectiveSchema)
  .merge(repositorySchema)
  .merge(workflowSchema)
  .merge(technologiesSchema)
  .merge(criticalitySchema)
  .merge(brandTabSchema)
  .merge(peopleSchema)
  .merge(advancedSchema);

export type ProjectFormValues = z.infer<typeof projectFormSchema>;

export const PROJECT_FORM_TABS = [
  'organization',
  'identity',
  'objective',
  'repository',
  'workflow',
  'technologies',
  'criticality',
  'brand',
  'people',
  'advanced',
] as const;
export type ProjectFormTab = (typeof PROJECT_FORM_TABS)[number];

/** Schema zod de cada aba — usado para validar por aba ao salvar. */
export const PROJECT_FORM_TAB_SCHEMAS: Record<ProjectFormTab, z.ZodTypeAny> = {
  organization: organizationSchema,
  identity: identitySchema,
  objective: objectiveSchema,
  repository: repositoryTabSchema,
  workflow: workflowSchema,
  technologies: technologiesSchema,
  criticality: criticalitySchema,
  brand: brandTabSchema,
  people: peopleSchema,
  advanced: advancedSchema,
};

export function defaultProjectValues(organizationId = ''): ProjectFormValues {
  return {
    organizationId,
    name: '',
    key: '',
    description: '',
    criticality: 'medium',
    state: 'active',
    repositoryProvider: 'github',
    repositoryUrl: '',
    defaultBranch: 'main',
    workflowTemplateId: '',
    technologies: [],
    brand: { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
    memberProfileIds: [],
  };
}

export function projectToFormValues(project: Project): ProjectFormValues {
  return {
    organizationId: project.organizationId,
    name: project.name,
    key: project.key,
    description: project.description,
    criticality: project.criticality,
    state: project.state,
    repositoryProvider: project.repositoryProvider,
    repositoryUrl: project.repositoryUrl ?? '',
    defaultBranch: project.defaultBranch,
    workflowTemplateId: '',
    technologies: project.technologies ?? [],
    brand: project.brand ?? { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
    memberProfileIds: project.memberProfileIds ?? [],
  };
}
