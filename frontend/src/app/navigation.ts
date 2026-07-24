import {
  Bell,
  Bot,
  ClipboardList,
  FileText,
  Gauge,
  LayoutGrid,
  MessagesSquare,
  Network,
  Play,
  Puzzle,
  Radio,
  Rocket,
  Scale,
  ScrollText,
  Settings,
  ShieldCheck,
  Building2,
  FolderKanban,
  PackageCheck,
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

export interface NavGroup {
  /** Chave i18n do rótulo da seção: `nav.groups.<key>`. */
  key: string;
  items: NavItem[];
}

/**
 * Navegação principal agrupada em seções rotuladas (apenas apresentação:
 * rotas, itens e ordem de registro de feature não mudam).
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    key: 'operation',
    items: [
      { key: 'cockpit', path: '/cockpit', icon: Gauge },
      { key: 'chat', path: '/chat', icon: MessageSquare },
      { key: 'board', path: '/board', icon: ClipboardList },
      { key: 'conversations', path: '/conversations', icon: MessagesSquare },
      { key: 'projects', path: '/projects', icon: FolderKanban },
      { key: 'delivery', path: '/delivery', icon: PackageCheck },
    ],
  },
  {
    key: 'orchestration',
    items: [
      { key: 'workflows', path: '/workflows', icon: Workflow },
      { key: 'orchestrator', path: '/orchestrator', icon: Puzzle },
      { key: 'agents', path: '/agents', icon: Bot },
      { key: 'run-project', path: '/run-project', icon: Play },
    ],
  },
  {
    key: 'artifacts',
    items: [
      { key: 'documents', path: '/documents', icon: FileText },
      { key: 'prototypes', path: '/prototypes', icon: LayoutGrid },
      { key: 'architecture', path: '/architecture', icon: Network },
      { key: 'approvals', path: '/approvals', icon: UserRoundCheck },
      { key: 'governance', path: '/governance', icon: Scale },
    ],
  },
  {
    key: 'administration',
    items: [
      { key: 'organizations', path: '/organizations', icon: Building2 },
      { key: 'providers', path: '/providers', icon: ScrollText },
      { key: 'channels', path: '/channels', icon: Radio },
      { key: 'tools', path: '/tools', icon: Wrench },
      { key: 'licenses', path: '/licenses', icon: ShieldCheck },
      { key: 'po-assistant', path: '/po-assistant', icon: UserRoundCheck },
      { key: 'onboarding', path: '/onboarding', icon: Rocket },
      { key: 'notifications', path: '/notifications', icon: Bell },
      { key: 'settings', path: '/settings', icon: Settings },
    ],
  },
];

/** Lista plana (ordem de exibição = ordem dos grupos), usada por rotas e mobile. */
export const NAV_ITEMS: NavItem[] = NAV_GROUPS.flatMap((group) => group.items);

/** Itens fixos da barra inferior mobile (o restante fica no drawer "Mais"). */
export const MOBILE_PRIMARY_KEYS = ['cockpit', 'chat', 'board', 'notifications'] as const;

export const MOBILE_PRIMARY_ITEMS: NavItem[] = MOBILE_PRIMARY_KEYS.map(
  (key) => NAV_ITEMS.find((item) => item.key === key)!,
);
