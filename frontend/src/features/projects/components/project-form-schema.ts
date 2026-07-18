import { z } from 'zod';

import {
  prioritySchema,
  projectStateSchema,
  repositoryProviderSchema,
  type Project,
} from '@/api';

/* ---- schemas por aba (mensagens = CHAVES i18n, traduzidas na render) ---- */

export const identificationSchema = z.object({
  organizationId: z.string().min(1, 'common.validation.required'),
  name: z.string().trim().min(2, 'common.validation.min2'),
  key: z.string().trim().regex(/^[A-Z0-9]{2,12}$/, 'common.validation.key'),
  description: z.string().trim().min(1, 'common.validation.required'),
  criticality: prioritySchema,
  state: projectStateSchema,
});

export const repositorySchema = z.object({
  repositoryProvider: repositoryProviderSchema,
  repositoryUrl: z.union([z.literal(''), z.string().trim().url('common.validation.url')]),
  defaultBranch: z.string().trim().min(1, 'common.validation.required'),
});

export const technologiesSchema = z.object({
  technologies: z.array(z.string().trim().min(1)),
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

export const projectFormSchema = identificationSchema
  .merge(repositorySchema)
  .merge(technologiesSchema)
  .merge(brandTabSchema)
  .merge(peopleSchema);

export type ProjectFormValues = z.infer<typeof projectFormSchema>;

export const PROJECT_FORM_TABS = [
  'identification',
  'repository',
  'technologies',
  'brand',
  'people',
] as const;
export type ProjectFormTab = (typeof PROJECT_FORM_TABS)[number];

/** Schema zod de cada aba — usado para validar por aba ao salvar. */
export const PROJECT_FORM_TAB_SCHEMAS: Record<ProjectFormTab, z.ZodTypeAny> = {
  identification: identificationSchema,
  repository: repositorySchema,
  technologies: technologiesSchema,
  brand: brandTabSchema,
  people: peopleSchema,
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
    technologies: project.technologies,
    brand: project.brand,
    memberProfileIds: project.memberProfileIds,
  };
}
