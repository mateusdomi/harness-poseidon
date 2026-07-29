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
  Radio,
  Scale,
  ScrollText,
  Settings,
  ShieldCheck,
  FileCog,
  Building2,
  FolderKanban,
  PackageCheck,
  MessageSquare,
  Users,
  Workflow,
  Wrench,
  UserRoundCheck,
  type LucideIcon,
} from 'lucide-react';

import type { PresentationMode } from '@/app/presentation/presentation-policy';

/** Posicao de cada modo na escala cumulativa (maior enxerga mais). */
const MODE_RANK: Record<PresentationMode, number> = {
  business: 0,
  technical: 1,
  admin: 2,
};

/** `true` quando quem esta em `mode` pode ver item exigido no nivel `required`. */
export function modeAllows(mode: PresentationMode, required: PresentationMode): boolean {
  return MODE_RANK[mode] >= MODE_RANK[required];
}

export interface NavItem {
  /** Chave i18n em `nav.<key>` e rota `/<key>`. */
  key: string;
  path: string;
  icon: LucideIcon;
  /**
   * Modo de apresentação mínimo em que o item aparece no menu (D7). A escala é
   * cumulativa: um item `technical` aparece em Técnico e em Administrador.
   * Ausente = `business` (visível para todo mundo).
   */
  mode?: PresentationMode;
}

export interface NavGroup {
  /** Chave i18n do rótulo da seção: `nav.groups.<key>`. */
  key: string;
  items: NavItem[];
}

/**
 * Navegação principal agrupada em seções rotuladas (apenas apresentação:
 * rotas, itens e ordem de registro de feature não mudam).
 *
 * Ordem = a lista homologada do modo Negócio (D7): Chat vem primeiro, porque
 * conversar com a Bruna é o jeito de trabalhar de quem não é técnico; depois o
 * dia a dia (Dashboard, Quadro, Conversas, Projetos, Central de Entregas) e,
 * por último, as telas de configuração de uso esporádico. Dentro de cada
 * grupo os itens de Negócio vêm antes dos que só existem no Técnico e no
 * Administrador, para que a ordem vista pelo cliente leigo seja exatamente a
 * homologada. Cada grupo é recolhível na sidebar (ver `NavMenu`/`ui-store`).
 *
 * Onboarding **não** está no menu: é fluxo de primeiro acesso e de troca de
 * perfil, não destino. Aprovações também não: virou aba de Documentos (D9) e
 * a rota antiga redireciona para lá.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    key: 'operation',
    items: [
      { key: 'chat', path: '/chat', icon: MessageSquare },
      { key: 'cockpit', path: '/cockpit', icon: Gauge },
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
      { key: 'orchestrator', path: '/orchestrator', icon: Users },
      { key: 'run-project', path: '/run-project', icon: Play },
      { key: 'agents', path: '/agents', icon: Bot, mode: 'technical' },
    ],
  },
  {
    key: 'artifacts',
    items: [
      { key: 'documents', path: '/documents', icon: FileText },
      { key: 'prototypes', path: '/prototypes', icon: LayoutGrid },
      { key: 'governance', path: '/governance', icon: Scale, mode: 'technical' },
      { key: 'governance-docs', path: '/governance-docs', icon: FileCog, mode: 'technical' },
      { key: 'architecture', path: '/architecture', icon: Network, mode: 'admin' },
    ],
  },
  {
    key: 'administration',
    items: [
      { key: 'organizations', path: '/organizations', icon: Building2 },
      { key: 'channels', path: '/channels', icon: Radio },
      { key: 'licenses', path: '/licenses', icon: ShieldCheck },
      { key: 'notifications', path: '/notifications', icon: Bell },
      { key: 'settings', path: '/settings', icon: Settings },
      { key: 'providers', path: '/providers', icon: ScrollText, mode: 'technical' },
      { key: 'tools', path: '/tools', icon: Wrench, mode: 'technical' },
      { key: 'po-assistant', path: '/po-assistant', icon: UserRoundCheck, mode: 'admin' },
    ],
  },
];

/**
 * Registro completo (todos os modos), na ordem de exibição. É a fonte das
 * rotas: esconder um item do menu não invalida o deep-link de quem já tem o
 * endereço — quem muda o que aparece é o modo, não o roteador.
 */
export const NAV_ITEMS: NavItem[] = NAV_GROUPS.flatMap((group) => group.items);

/** Nível mínimo de um item (itens sem `mode` são de Negócio). */
export function navItemMode(item: NavItem): PresentationMode {
  return item.mode ?? 'business';
}

/** Grupos visíveis no modo dado — grupos que ficam vazios somem do menu. */
export function navGroupsFor(mode: PresentationMode): NavGroup[] {
  return NAV_GROUPS.map((group) => ({
    ...group,
    items: group.items.filter((item) => modeAllows(mode, navItemMode(item))),
  })).filter((group) => group.items.length > 0);
}

/**
 * Telas que existem para comparar projetos: ignoram a seleção global (o
 * seletor do cabeçalho some nelas, em vez de sugerir um recorte que a tela
 * não aplica).
 */
export const MULTI_PROJECT_PATHS = ['/projects', '/delivery', '/organizations'] as const;

export function isMultiProjectPath(pathname: string): boolean {
  return MULTI_PROJECT_PATHS.some(
    (path) => pathname === path || pathname.startsWith(`${path}/`),
  );
}

/** Itens fixos da barra inferior mobile (o restante fica no drawer "Mais"). */
export const MOBILE_PRIMARY_KEYS = ['chat', 'cockpit', 'board', 'conversations'] as const;

export const MOBILE_PRIMARY_ITEMS: NavItem[] = MOBILE_PRIMARY_KEYS.map(
  (key) => NAV_ITEMS.find((item) => item.key === key)!,
);
