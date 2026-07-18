/**
 * Máscara de segredos para exibição (logs, payloads, endpoints).
 * Regra inegociável: a UI NUNCA exibe segredos — sempre `****` com a nota
 * de permissão (i18n `common.secrets.maskedNote`).
 */

/** Padrões de "chave: valor" / "chave=valor" que indicam segredo. */
const SECRET_KEY_PATTERN =
  /\b(token|api[-_]?key|secret|password|passwd|authorization|bearer|credential|senha)\b([\s:=]+)("?)(?:Bearer\s+)?[^\s"']+"?/gi;

/** Mascara ocorrências de chave=segredo no texto, preservando a chave. */
export function maskSecrets(text: string): string {
  return text.replace(SECRET_KEY_PATTERN, (_match, key: string, sep: string) => `${key}${sep}****`);
}

/** Texto fixo para valores que são integralmente secretos (ex.: endpoint com credencial). */
export const SECRET_MASK = '****';

/** Mascara credenciais embutidas em URL (userinfo e query sensível). */
export function maskEndpoint(endpoint: string): string {
  return maskSecrets(endpoint.replace(/\/\/[^/@\s]+@/, '//****@'));
}
