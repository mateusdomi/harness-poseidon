import type { PresentationMode } from '@/app/presentation/presentation-policy';

export type { PresentationMode };

/**
 * Projeção do modo de apresentação lida pelas telas (decisão D7).
 *
 * O tipo `PresentationMode` e a política de quais modos o perfil pode
 * escolher vivem em `presentation-policy.ts`. Aqui fica só a projeção que as
 * telas consomem, para que nenhuma delas reimplemente a condição
 * `mode === 'technical' || mode === 'admin'`.
 */
export const PRESENTATION_MODES = ['business', 'technical', 'admin'] as const;

export const DEFAULT_PRESENTATION_MODE: PresentationMode = 'business';

export function isPresentationMode(value: unknown): value is PresentationMode {
  return (
    typeof value === 'string' &&
    (PRESENTATION_MODES as readonly string[]).includes(value)
  );
}

export interface PresentationProjection {
  mode: PresentationMode;
  allowedModes: readonly PresentationMode[];
  /** O cliente leigo: só vocabulário e superfícies de negócio. */
  isBusiness: boolean;
  /** Detalhe técnico (saúde, provedor, modelo, identificadores) pode aparecer. */
  showTechnicalDetails: boolean;
  /** Ações administrativas (arquitetura, assistente de PO) podem aparecer. */
  showAdministrativeActions: boolean;
}

/**
 * Resolve a projeção a partir de um valor persistido, que pode ser lixo.
 * Fail-closed: na dúvida, Negócio — mostra menos, nunca mais.
 */
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
