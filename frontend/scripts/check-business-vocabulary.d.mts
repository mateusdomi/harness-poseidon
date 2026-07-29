/** Tipos do gate de vocabulário do modo Negócio (implementação em .mjs). */

export type Catalogs = Record<string, Record<string, unknown>>;

export interface VocabularyViolation {
  key: string;
  lang: string;
  term: string;
  hint: string;
  text: string;
}

export declare const TECHNICAL_NAMESPACES: string[];
export declare const EXEMPTIONS: Map<string, string>;

export declare function loadCatalogs(root?: string): Catalogs;
export declare function mergedCatalog(lang: string, root?: string): Record<string, unknown>;
export declare function isExempt(key: string, exemptions: Map<string, string>): boolean;
export declare function collectVocabularyViolations(
  catalogs: Catalogs,
  options?: { technicalNamespaces?: string[]; exemptions?: Map<string, string> },
): VocabularyViolation[];
export declare function staleClassification(
  catalogs: Catalogs,
  technicalNamespaces?: string[],
): string[];
