import { ChevronDown, ChevronRight, FileText, Folder } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { cn } from '@/lib/utils';
import type { DocTreeNode } from '@/features/governance-docs/lib/tree';

interface DocTreeProps {
  nodes: DocTreeNode[];
  selectedPath: string | null;
  onSelect: (path: string) => void;
}

/** Árvore navegável de arquivos de governança (diretórios expansíveis). */
export function DocTree({ nodes, selectedPath, onSelect }: DocTreeProps) {
  const { t } = useTranslation();
  return (
    <ul
      className="flex min-w-0 flex-col gap-0.5 overflow-hidden"
      role="tree"
      aria-label={t('governanceDocs.title')}
    >
      {nodes.map((node) => (
        <DocTreeItem
          key={node.path}
          node={node}
          depth={0}
          selectedPath={selectedPath}
          onSelect={onSelect}
        />
      ))}
    </ul>
  );
}

interface DocTreeItemProps {
  node: DocTreeNode;
  depth: number;
  selectedPath: string | null;
  onSelect: (path: string) => void;
}

function DocTreeItem({ node, depth, selectedPath, onSelect }: DocTreeItemProps) {
  const [open, setOpen] = useState(true);
  const indent = { paddingLeft: `${depth * 0.75 + 0.25}rem` };

  if (node.type === 'dir') {
    return (
      <li className="min-w-0" role="treeitem" aria-expanded={open}>
        <button
          type="button"
          onClick={() => setOpen((value) => !value)}
          style={indent}
          className="flex w-full min-w-0 items-center gap-1.5 overflow-hidden rounded-md py-1 pr-2 text-left text-sm text-foreground-muted hover:bg-surface-elevated"
        >
          {open ? (
            <ChevronDown className="size-3.5 shrink-0" aria-hidden />
          ) : (
            <ChevronRight className="size-3.5 shrink-0" aria-hidden />
          )}
          <Folder className="size-3.5 shrink-0" aria-hidden />
          <span className="truncate font-medium">{node.name}</span>
        </button>
        {open && (
          <ul className="flex min-w-0 flex-col gap-0.5 overflow-hidden" role="group">
            {node.children.map((child) => (
              <DocTreeItem
                key={child.path}
                node={child}
                depth={depth + 1}
                selectedPath={selectedPath}
                onSelect={onSelect}
              />
            ))}
          </ul>
        )}
      </li>
    );
  }

  const selected = node.path === selectedPath;
  return (
    <li className="min-w-0" role="treeitem" aria-selected={selected}>
      <button
        type="button"
        onClick={() => onSelect(node.path)}
        style={indent}
        aria-current={selected ? 'true' : undefined}
        className={cn(
          'flex w-full min-w-0 items-center gap-1.5 overflow-hidden rounded-md py-1 pr-2 text-left text-sm hover:bg-surface-elevated',
          selected ? 'bg-surface-elevated font-medium text-brand-strong' : 'text-foreground',
        )}
      >
        <FileText className="ml-3.5 size-3.5 shrink-0" aria-hidden />
        <span className="truncate">{node.name}</span>
      </button>
    </li>
  );
}
