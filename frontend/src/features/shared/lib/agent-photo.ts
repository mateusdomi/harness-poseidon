const LEADERSHIP_ALIASES = new Set(['chief', 'chief-orchestrator', 'chief-claude-primary']);

const PHOTO_ALIAS_BY_PERSONA: Readonly<Record<string, string>> = {
  'architecture-critic': 'worker-antigravity-review',
  'frontend-engineer': 'worker-codex-frontend',
  designer: 'worker-kimi-ui',
  'prototype-designer': 'worker-kimi-ui',
};

export function isLeadershipAlias(alias: string): boolean {
  return LEADERSHIP_ALIASES.has(alias);
}

/**
 * Uma pessoa pode aparecer como persona, definição e conta de execução.
 * A foto usa um alias canônico para que a mesma identidade seja consistente
 * em todas essas superfícies sem duplicar o asset gerenciado.
 */
export function canonicalPhotoAlias(alias: string): string {
  if (isLeadershipAlias(alias)) return 'chief-claude-primary';
  return PHOTO_ALIAS_BY_PERSONA[alias] ?? alias;
}

export function versionedPhotoUrl(url: string, revision: number): string {
  if (url.startsWith('blob:') || revision <= 0) return url;
  const separator = url.includes('?') ? '&' : '?';
  return `${url}${separator}v=${revision}`;
}
