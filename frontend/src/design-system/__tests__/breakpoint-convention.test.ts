import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const SRC = path.resolve(__dirname, '..', '..');
const TAILWIND_CONFIG = path.resolve(SRC, '..', 'tailwind.config.js');

/**
 * Lê os breakpoints do bloco `screens` do tema. O config é lido como texto em vez
 * de importado: ele é JS sem tipos, e um `import` obrigaria a afrouxar o
 * typecheck do projeto só para este teste.
 */
function declaredBreakpoints(): string[] {
  const config = readFileSync(TAILWIND_CONFIG, 'utf8');
  const screens = /screens:\s*\{([^}]*)\}/.exec(config);

  return screens === null
    ? []
    : [...screens[1].matchAll(/(\w+)\s*:/g)].map((match) => match[1]);
}

/**
 * `sm:` é uma classe fantasma neste tema.
 *
 * `tailwind.config.js` declara apenas md/lg/xl, então qualquer utilitário escrito
 * com prefixo `sm:` não gera CSS nenhum — e falha em silêncio: o layout responsivo
 * simplesmente não acontece, e um `hidden sm:flex` esconde o elemento em TODA
 * largura (foi o que manteve a lista de abas do catálogo de Ferramentas invisível).
 *
 * Nenhum gate percebia isso porque o build passa e o TypeScript não sabe de CSS.
 * Este teste torna a convenção verificável: ou o prefixo existe no tema, ou não
 * se escreve.
 */
function tsxFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      return entry.name === '__tests__' ? [] : tsxFiles(full);
    }
    return entry.isFile() && entry.name.endsWith('.tsx') ? [full] : [];
  });
}

/** Prefixos responsivos citados em className, sem os que o tema declara. */
function unknownBreakpoints(source: string, declared: readonly string[]): string[] {
  // Só dentro de className/cn(...): `sm:` como chave de objeto (variantes de
  // tamanho de botão, por exemplo) é legítimo e não é prefixo responsivo.
  const classAttributes = source.matchAll(/(?:className=|cn\()([\s\S]{0,600}?)(?:\/>|>|\);)/g);
  const found = new Set<string>();

  for (const [, block] of classAttributes) {
    for (const [, prefix] of block.matchAll(/\b(sm|xs|2xl|3xl):[a-z[]/g)) {
      if (!declared.includes(prefix)) {
        found.add(prefix);
      }
    }
  }

  return [...found];
}

describe('convenção de breakpoints do tema', () => {
  const declared = declaredBreakpoints();

  it('o tema declara os breakpoints que o código usa', () => {
    expect(declared).toEqual(['md', 'lg', 'xl']);
  });

  it('nenhum componente usa prefixo responsivo ausente do tema', () => {
    const offenders: string[] = [];

    for (const file of tsxFiles(SRC)) {
      const unknown = unknownBreakpoints(readFileSync(file, 'utf8'), declared);
      if (unknown.length > 0) {
        offenders.push(`${path.relative(SRC, file)} → ${unknown.map((p) => `${p}:`).join(', ')}`);
      }
    }

    expect(
      offenders,
      'Prefixo responsivo que o tema não declara não gera CSS — o estilo falha em '
        + `silêncio. Use ${declared.map((p) => `${p}:`).join('/')} ou declare o breakpoint `
        + `em tailwind.config.js:\n  ${offenders.join('\n  ')}`,
    ).toEqual([]);
  });
});
