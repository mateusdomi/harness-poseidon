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
  repositoryUrl: z.union([z.literal(''), z.string().trim().url('common.validation.url')]),
  defaultBranch: z.string().trim().min(1, 'common.validation.required'),
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
  repository: repositorySchema,
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
