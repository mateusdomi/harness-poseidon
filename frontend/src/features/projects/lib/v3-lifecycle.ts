export type V3LifecycleState =
  | 'DRAFT'
  | 'UNDERSTANDING'
  | 'AWAITING_INPUT'
  | 'READY_TO_START'
  | 'BUILDING'
  | 'PAUSED_QUOTA'
  | 'BLOCKED'
  | 'VALIDATING'
  | 'READY_FOR_HUMAN_ACCEPTANCE'
  | 'HUMAN_ACCEPTED';

export type V3LifecycleMacro = 'understand' | 'build' | 'validate' | 'acceptance';

export interface V3LifecycleProjection {
  state: V3LifecycleState;
  macro: V3LifecycleMacro;
  macroLabel: string;
  statusLabel: string;
  badge: 'default' | 'success' | 'warning' | 'error' | 'outline';
}

const KNOWN_STATES = new Set<string>([
  'DRAFT',
  'UNDERSTANDING',
  'AWAITING_INPUT',
  'READY_TO_START',
  'BUILDING',
  'PAUSED_QUOTA',
  'BLOCKED',
  'VALIDATING',
  'READY_FOR_HUMAN_ACCEPTANCE',
  'HUMAN_ACCEPTED',
]);

export const V3_LIFECYCLE_MACROS: ReadonlyArray<{
  id: V3LifecycleMacro;
  label: string;
  states: readonly V3LifecycleState[];
}> = [
  {
    id: 'understand',
    label: 'Entendimento',
    states: ['DRAFT', 'UNDERSTANDING', 'AWAITING_INPUT', 'READY_TO_START'],
  },
  {
    id: 'build',
    label: 'Desenvolvimento',
    states: ['BUILDING', 'PAUSED_QUOTA', 'BLOCKED'],
  },
  {
    id: 'validate',
    label: 'Validação',
    states: ['VALIDATING'],
  },
  {
    id: 'acceptance',
    label: 'Aceite Humano',
    states: ['READY_FOR_HUMAN_ACCEPTANCE', 'HUMAN_ACCEPTED'],
  },
];

export function normalizeV3LifecycleState(value: string | null | undefined): V3LifecycleState {
  const candidate = typeof value === 'string' ? value.trim().toUpperCase() : '';
  return KNOWN_STATES.has(candidate) ? (candidate as V3LifecycleState) : 'UNDERSTANDING';
}

export function projectV3Lifecycle(value: string | null | undefined): V3LifecycleProjection {
  const state = normalizeV3LifecycleState(value);
  switch (state) {
    case 'DRAFT':
    case 'UNDERSTANDING':
      return {
        state,
        macro: 'understand',
        macroLabel: 'Entendimento',
        statusLabel: 'Em andamento',
        badge: 'default',
      };
    case 'AWAITING_INPUT':
      return {
        state,
        macro: 'understand',
        macroLabel: 'Entendimento',
        statusLabel: 'Aguardando informação',
        badge: 'warning',
      };
    case 'READY_TO_START':
      return {
        state,
        macro: 'understand',
        macroLabel: 'Entendimento',
        statusLabel: 'Pronto para iniciar',
        badge: 'success',
      };
    case 'BUILDING':
      return {
        state,
        macro: 'build',
        macroLabel: 'Desenvolvimento',
        statusLabel: 'Em andamento',
        badge: 'default',
      };
    case 'PAUSED_QUOTA':
      return {
        state,
        macro: 'build',
        macroLabel: 'Desenvolvimento',
        statusLabel: 'Pausado por capacidade',
        badge: 'warning',
      };
    case 'BLOCKED':
      return {
        state,
        macro: 'build',
        macroLabel: 'Desenvolvimento',
        statusLabel: 'Bloqueado',
        badge: 'error',
      };
    case 'VALIDATING':
      return {
        state,
        macro: 'validate',
        macroLabel: 'Validação',
        statusLabel: 'Em andamento',
        badge: 'default',
      };
    case 'READY_FOR_HUMAN_ACCEPTANCE':
      return {
        state,
        macro: 'acceptance',
        macroLabel: 'Aceite Humano',
        statusLabel: 'Pronto para homologação',
        badge: 'success',
      };
    case 'HUMAN_ACCEPTED':
      return {
        state,
        macro: 'acceptance',
        macroLabel: 'Aceite Humano',
        statusLabel: 'Aceito',
        badge: 'success',
      };
  }
}

export function v3LifecycleMacroIndex(macro: V3LifecycleMacro): number {
  return V3_LIFECYCLE_MACROS.findIndex((item) => item.id === macro);
}
