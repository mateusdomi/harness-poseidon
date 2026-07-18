/**
 * Diff de texto linha a linha (LCS clássico, programação dinâmica).
 * Implementado localmente para não adicionar dependência pesada — ver
 * DECISIONS (D-028). Adequado para versões de documentos (centenas de
 * linhas); documentos gigantes são truncados pelo chamador se necessário.
 */

export type DiffLineType = 'same' | 'added' | 'removed';

export interface DiffLine {
  type: DiffLineType;
  text: string;
  /** Número da linha na versão antiga (null em linhas adicionadas). */
  oldLine: number | null;
  /** Número da linha na versão nova (null em linhas removidas). */
  newLine: number | null;
}

export interface DiffResult {
  lines: DiffLine[];
  addedCount: number;
  removedCount: number;
  /** true quando as versões são idênticas. */
  identical: boolean;
}

/** Compara duas versões de texto e retorna as linhas com tipo e numeração. */
export function diffLines(oldText: string, newText: string): DiffResult {
  const a = oldText.split('\n');
  const b = newText.split('\n');
  const rows = a.length;
  const cols = b.length;

  // dp[i][j] = tamanho da LCS de a[i:] e b[j:].
  const dp: number[][] = Array.from({ length: rows + 1 }, () => new Array<number>(cols + 1).fill(0));
  for (let i = rows - 1; i >= 0; i -= 1) {
    for (let j = cols - 1; j >= 0; j -= 1) {
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }

  const lines: DiffLine[] = [];
  let addedCount = 0;
  let removedCount = 0;
  let i = 0;
  let j = 0;
  while (i < rows && j < cols) {
    if (a[i] === b[j]) {
      lines.push({ type: 'same', text: a[i], oldLine: i + 1, newLine: j + 1 });
      i += 1;
      j += 1;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) {
      lines.push({ type: 'removed', text: a[i], oldLine: i + 1, newLine: null });
      removedCount += 1;
      i += 1;
    } else {
      lines.push({ type: 'added', text: b[j], oldLine: null, newLine: j + 1 });
      addedCount += 1;
      j += 1;
    }
  }
  while (i < rows) {
    lines.push({ type: 'removed', text: a[i], oldLine: i + 1, newLine: null });
    removedCount += 1;
    i += 1;
  }
  while (j < cols) {
    lines.push({ type: 'added', text: b[j], oldLine: null, newLine: j + 1 });
    addedCount += 1;
    j += 1;
  }

  return { lines, addedCount, removedCount, identical: addedCount === 0 && removedCount === 0 };
}
