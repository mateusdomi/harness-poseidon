import {
  Bell,
  Bot,
  ClipboardList,
  FileText,
  Gauge,
  LayoutGrid,
  MessagesSquare,
  Play,
  Puzzle,
  Rocket,
  Scale,
  ScrollText,
  Settings,
  ShieldCheck,
  Building2,
  FolderKanban,
  MessageSquare,
  Workflow,
  Wrench,
  UserRoundCheck,
  type LucideIcon,
} from 'lucide-react';

export interface NavItem {
  /** Chave i18n em `nav.<key>` e rota `/<key>`. */
  key: string;
  path: string;
  icon: LucideIcon;
}

/** Ordem de exibição na navegação principal. */
export const NAV_ITEMS: NavItem[] = [
  { key: 'cockpit', path: '/cockpit', icon: Gauge },
  { key: 'projects', path: '/projects', icon: FolderKanban },
  { key: 'chat', path: '/chat', icon: MessageSquare },
  { key: 'conversations', path: '/conversations', icon: MessagesSquare },
  { key: 'board', path: '/board', icon: ClipboardList },
  { key: 'workflows', path: '/workflows', icon: Workflow },
  { key: 'documents', path: '/documents', icon: FileText },
  { key: 'prototypes', path: '/prototypes', icon: LayoutGrid },
  { key: 'approvals', path: '/approvals', icon: UserRoundCheck },
  { key: 'orchestrator', path: '/orchestrator', icon: Puzzle },
  { key: 'agents', path: '/agents', icon: Bot },
  { key: 'tools', path: '/tools', icon: Wrench },
  { key: 'run-project', path: '/run-project', icon: Play },
  { key: 'onboarding', path: '/onboarding', icon: Rocket },
  { key: 'organizations', path: '/organizations', icon: Building2 },
  { key: 'providers', path: '/providers', icon: ScrollText },
  { key: 'po-assistant', path: '/po-assistant', icon: UserRoundCheck },
  { key: 'governance', path: '/governance', icon: Scale },
  { key: 'licenses', path: '/licenses', icon: ShieldCheck },
  { key: 'notifications', path: '/notifications', icon: Bell },
  { key: 'settings', path: '/settings', icon: Settings },
];

/** Itens fixos da barra inferior mobile (o restante fica no drawer "Mais"). */
export const MOBILE_PRIMARY_KEYS = ['cockpit', 'projects', 'chat', 'notifications'] as const;

export const MOBILE_PRIMARY_ITEMS: NavItem[] = MOBILE_PRIMARY_KEYS.map(
  (key) => NAV_ITEMS.find((item) => item.key === key)!,
);
