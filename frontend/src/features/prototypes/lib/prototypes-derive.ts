import type { Brand, Project, Organization, VisualReference } from '@/api';

/**
 * Metadados livres da referência visual via tags prefixadas: o contrato de
 * VisualReference não tem campos próprios para briefing/momento do fluxo,
 * então eles são persistidos como tags `briefing:<texto>` e `fluxo:<texto>`
 * (tags são rótulos livres do contrato). Pendência de campos próprios
 * registrada no HANDOFF.
 */
export const BRIEFING_TAG_PREFIX = 'briefing:';
export const FLOW_MOMENT_TAG_PREFIX = 'fluxo:';

/** Valor de uma tag prefixada (ex.: briefing) ou null. */
export function prefixedTagValue(tags: string[], prefix: string): string | null {
  const tag = tags.find((entry) => entry.startsWith(prefix));
  return tag ? tag.slice(prefix.length) : null;
}

/** Tags visíveis na galeria (sem os metadados prefixados). */
export function plainTags(tags: string[]): string[] {
  return tags.filter(
    (entry) =>
      !entry.startsWith(BRIEFING_TAG_PREFIX) && !entry.startsWith(FLOW_MOMENT_TAG_PREFIX),
  );
}

/** Monta a lista final de tags do upload: livres + metadados prefixados. */
export function buildReferenceTags(input: {
  tags: string;
  briefing: string;
  flowMoment: string;
}): string[] {
  const free = input.tags
    .split(',')
    .map((tag) => tag.trim())
    .filter((tag) => tag !== '');
  const briefing = input.briefing.trim();
  const flowMoment = input.flowMoment.trim();
  return [
    ...free,
    ...(briefing !== '' ? [`${BRIEFING_TAG_PREFIX}${briefing}`] : []),
    ...(flowMoment !== '' ? [`${FLOW_MOMENT_TAG_PREFIX}${flowMoment}`] : []),
  ];
}

/** Marca efetiva do projeto: campo a campo, projeto → organização (herança). */
export function effectiveBrand(
  project: Project,
  organization: Organization | null,
): { brand: Brand; inheritedFields: (keyof Brand)[] } {
  const org = organization?.brand;
  const inheritedFields: (keyof Brand)[] = [];
  const resolve = (field: keyof Brand): string | null => {
    if (project.brand[field] !== null) return project.brand[field];
    if (org && org[field] !== null) {
      inheritedFields.push(field);
      return org[field];
    }
    return null;
  };
  return {
    brand: {
      logoUrl: resolve('logoUrl'),
      primaryColor: resolve('primaryColor'),
      secondaryColor: resolve('secondaryColor'),
      typography: resolve('typography'),
    },
    inheritedFields,
  };
}

/** imageUrl de ZIP (mock) usa o esquema `zip:<nome>` — nunca é imagem. */
export function isZipReference(reference: VisualReference): boolean {
  return reference.imageUrl.startsWith('zip:');
}
