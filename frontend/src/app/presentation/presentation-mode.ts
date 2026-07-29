import type { Ulid } from '@/api';

/**
 * Modo de apresentação do produto (decisão D7 da homologação).
 *
 * - `business`: o cliente leigo. Só vocabulário de negócio; nenhuma superfície
 *   técnica. É o padrão e o fallback de qualquer valor inválido (fail-closed:
 *   na dúvida, mostra menos, nunca mais).
 * - `technical`: soma as telas de operação (Agentes, Governança, Provedores…).
 * - `admin`: soma Arquitetura e Assistente de PO.
 */
export const PRESENTATION_MODES = ['business', 'technical', 'admin'] as const;

export type PresentationMode = (typeof PRESENTATION_MODES)[number];

export const DEFAULT_PRESENTATION_MODE: PresentationMode = 'business';

/** Chave de persistência quando não há perfil escolhido ainda. */
export const ANONYMOUS_PRESENTATION_PROFILE = '__anonymous__';

export function isPresentationMode(value: unknown): value is PresentationMode {
  return (
    typeof value === 'string' &&
    (PRESENTATION_MODES as readonly string[]).includes(value)
  );
}

/**
 * Projeção lida pelas telas. Os campos `show*` existem para que nenhuma tela
 * reimplemente a condição `mode === 'technical' || mode === 'admin'`.
 */
export interface PresentationProjection {
  mode: PresentationMode;
  allowedModes: readonly PresentationMode[];
  isBusiness: boolean;
  /** Detalhe técnico (saúde, provedor, modelo, identificadores) pode aparecer. */
  showTechnicalDetails: boolean;
  /** Ações administrativas (arquitetura, assistente de PO) podem aparecer. */
  showAdministrativeActions: boolean;
}

/** Resolve a projeção a partir de um valor persistido, que pode ser lixo. */
export function resolvePresentationProjection(
  requestedMode: unknown,
  allowedModes: readonly PresentationMode[] = PRESENTATION_MODES,
): PresentationProjection {
  const mode =
    isPresentationMode(requestedMode) && allowedModes.includes(requestedMode)
      ? requestedMode
      : DEFAULT_PRESENTATION_MODE;
  return {
    mode,
    allowedModes,
    isBusiness: mode === 'business',
    showTechnicalDetails: mode === 'technical' || mode === 'admin',
    showAdministrativeActions: mode === 'admin',
  };
}

/** Chave de persistência do modo por perfil (o modo é preferência de pessoa). */
export function presentationProfileKey(profileId: Ulid | null): string {
  return profileId ?? ANONYMOUS_PRESENTATION_PROFILE;
}
