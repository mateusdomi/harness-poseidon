import { describe, expect, it } from 'vitest';

import { buildDocTree, fileExtension, isMarkdown } from '@/features/governance-docs/lib/tree';

const file = (path: string) => ({ path, name: path.split('/').pop()!, size: 1, modifiedAt: '2026-07-01T00:00:00Z' });

describe('buildDocTree', () => {
  it('aninha diretórios e arquivos preservando o path', () => {
    const tree = buildDocTree([
      file('governance/core.md'),
      file('governance/rules/a.md'),
      file('docs/INDEX.md'),
    ]);

    // Diretórios primeiro, em ordem alfabética: docs antes de governance.
    expect(tree.map((node) => node.name)).toEqual(['docs', 'governance']);
    const governance = tree.find((node) => node.name === 'governance')!;
    expect(governance.type).toBe('dir');
    if (governance.type !== 'dir') throw new Error('expected dir');
    // rules (dir) antes de core.md (file).
    expect(governance.children.map((node) => `${node.type}:${node.name}`)).toEqual([
      'dir:rules',
      'file:core.md',
    ]);
    const rules = governance.children.find((node) => node.name === 'rules')!;
    if (rules.type !== 'dir') throw new Error('expected dir');
    expect(rules.children[0]).toMatchObject({ type: 'file', path: 'governance/rules/a.md' });
  });

  it('lista vazia produz árvore vazia', () => {
    expect(buildDocTree([])).toEqual([]);
  });
});

describe('fileExtension / isMarkdown', () => {
  it('extrai extensão em minúsculas', () => {
    expect(fileExtension('governance/manifest.YAML')).toBe('yaml');
    expect(fileExtension('noext')).toBe('');
  });

  it('reconhece markdown', () => {
    expect(isMarkdown('a/b.md')).toBe(true);
    expect(isMarkdown('a/b.markdown')).toBe(true);
    expect(isMarkdown('a/b.yaml')).toBe(false);
  });
});
