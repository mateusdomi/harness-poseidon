import type { GovernanceDocFile } from '@/api';

export interface DocTreeFileNode {
  type: 'file';
  name: string;
  path: string;
  size: number;
  modifiedAt: string;
}

export interface DocTreeDirNode {
  type: 'dir';
  name: string;
  path: string;
  children: DocTreeNode[];
}

export type DocTreeNode = DocTreeFileNode | DocTreeDirNode;

/**
 * Constrói uma árvore aninhada (diretórios + arquivos) a partir da lista
 * plana de paths retornada pelo backend. Diretórios vêm antes de arquivos e
 * ambos ficam em ordem alfabética estável.
 */
export function buildDocTree(files: GovernanceDocFile[]): DocTreeNode[] {
  const roots: DocTreeNode[] = [];
  const dirIndex = new Map<string, DocTreeDirNode>();

  const ensureDir = (segments: string[]): DocTreeNode[] => {
    let container = roots;
    let prefix = '';
    for (const segment of segments) {
      prefix = prefix ? `${prefix}/${segment}` : segment;
      let dir = dirIndex.get(prefix);
      if (!dir) {
        dir = { type: 'dir', name: segment, path: prefix, children: [] };
        dirIndex.set(prefix, dir);
        container.push(dir);
      }
      container = dir.children;
    }
    return container;
  };

  for (const file of files) {
    const segments = file.path.split('/');
    const name = segments.pop() ?? file.path;
    const container = ensureDir(segments);
    container.push({
      type: 'file',
      name,
      path: file.path,
      size: file.size,
      modifiedAt: file.modifiedAt,
    });
  }

  sortNodes(roots);
  return roots;
}

function sortNodes(nodes: DocTreeNode[]): void {
  nodes.sort((a, b) => {
    if (a.type !== b.type) return a.type === 'dir' ? -1 : 1;
    return a.name.localeCompare(b.name);
  });
  for (const node of nodes) {
    if (node.type === 'dir') sortNodes(node.children);
  }
}

/** Extensão em minúsculas (sem ponto) para escolher o modo de visualização. */
export function fileExtension(path: string): string {
  const dot = path.lastIndexOf('.');
  return dot >= 0 ? path.slice(dot + 1).toLowerCase() : '';
}

/** Se a extensão é renderizável como markdown na visualização. */
export function isMarkdown(path: string): boolean {
  const ext = fileExtension(path);
  return ext === 'md' || ext === 'markdown';
}
