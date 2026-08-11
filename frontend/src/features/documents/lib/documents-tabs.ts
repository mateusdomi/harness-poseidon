/**
 * As duas faces da MESMA tela (decisão D9): o que a equipe produziu e o que
 * espera uma decisão sua. Eram quatro telas de documento; no modo Negócio
 * viraram uma, e aprovar deixou de ser um destino separado — acontece aqui.
 *
 * A aba vive na URL (`?tab=`) porque o endereço antigo `/approvals` redireciona
 * para cá com a aba já escolhida, e porque um link enviado no chat precisa abrir
 * exatamente na aba que o remetente estava vendo.
 */
export const DOCUMENT_TABS = ['sources', 'records', 'delivery', 'approvals'] as const;

export type DocumentTabId = (typeof DOCUMENT_TABS)[number];

export const DEFAULT_DOCUMENT_TAB: DocumentTabId = 'sources';

/** Valor de `?tab=` vindo da URL — que pode ser lixo. Na dúvida, o catálogo. */
export function parseDocumentTab(value: string | null | undefined): DocumentTabId {
  if (value === 'catalog') return DEFAULT_DOCUMENT_TAB;
  return DOCUMENT_TABS.includes(value as DocumentTabId)
    ? (value as DocumentTabId)
    : DEFAULT_DOCUMENT_TAB;
}

export function documentTabId(id: DocumentTabId): string {
  return `documents-tab-${id}`;
}

export function documentPanelId(id: DocumentTabId): string {
  return `documents-panel-${id}`;
}
