import type { Priority, ProjectState } from '@/api';
import type { BadgeProps } from '@/design-system';

/**
 * Mapeamento tipado de enums do domínio → variantes semânticas do Badge.
 * NENHUMA cor/status hardcoded nos componentes: toda tela passa por aqui.
 * Os labels ficam no i18n (`status.projectState.*`, `status.priority.*`).
 */

export const PROJECT_STATE_VARIANTS: Record<ProjectState, BadgeProps['variant']> = {
  active: 'success',
  paused: 'warning',
  archived: 'outline',
};

export const PRIORITY_VARIANTS: Record<Priority, BadgeProps['variant']> = {
  low: 'outline',
  medium: 'info',
  high: 'warning',
  critical: 'error',
};

export function projectStateVariant(state: ProjectState): BadgeProps['variant'] {
  return PROJECT_STATE_VARIANTS[state];
}

export function priorityVariant(priority: Priority): BadgeProps['variant'] {
  return PRIORITY_VARIANTS[priority];
}
