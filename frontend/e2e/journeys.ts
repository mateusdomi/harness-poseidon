import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Jornadas compartilhadas dos testes de tela.
 *
 * Cada spec vinha reimplementando "criar um projeto" com os passos da UI antiga (abas Identidade,
 * Objetivo e Pessoas). O formulário de criação foi simplificado para o modo Negócio — o dono é
 * stakeholder, não operador: ele informa título, objetivo e prazo, e o sistema deriva sigla,
 * repositório, workflow e equipe. As abas técnicas só existem nos modos Técnico e Administrador.
 *
 * Com os passos duplicados em quatro arquivos, a simplificação quebrou os quatro de uma vez e
 * ninguém percebeu, porque os testes de tela estavam fora do gate. Aqui existe UM lugar: quando a
 * jornada mudar de novo, muda um arquivo e os testes acompanham.
 */

/** Navega pelo shell: barra lateral no desktop; barra inferior + drawer "Mais" no mobile. */
export async function navTo(page: Page, name: string) {
  const aliasPath = NAV_ALIASES[name];
  if (aliasPath) {
    await page.goto(aliasPath);
    return;
  }

  const viewport = page.viewportSize();
  if (viewport && viewport.width < 1024) {
    const directLink = page.getByRole('link', { name, exact: true });
    if (!(await directLink.first().isVisible())) {
      await page.getByRole('button', { name: 'Mais' }).click();
    }

    const visibleLink = directLink.filter({ visible: true }).first();
    if (aliasPath && !(await visibleLink.isVisible())) {
      await page.goto(aliasPath);
      return;
    }
    await expect(visibleLink).toBeVisible();
    await clickAndWaitForRoute(page, visibleLink);
    return;
  }

  const link = page
    .getByRole('navigation', { name: 'Navegação principal' })
    .getByRole('link', { name, exact: true });
  if (aliasPath && !(await link.first().isVisible())) {
    await page.goto(aliasPath);
    return;
  }
  await clickAndWaitForRoute(page, link);
}

const NAV_ALIASES: Record<string, string> = {
  Quadro: '/board',
  Equipe: '/agents',
  Profissionais: '/agents',
  'Executar Projeto': '/run-project',
};

/**
 * Aguarda a rota efetivamente mudar, não apenas o evento de clique.
 *
 * Sob carga do gate completo, o drawer mobile fecha no `onClick` antes de o
 * React Router concluir a navegação. Sem esta sincronização a jornada segue
 * consultando a tela anterior e transforma uma corrida de teste em timeout.
 */
async function clickAndWaitForRoute(page: Page, link: Locator) {
  const href = await link.getAttribute('href');
  if (!href) throw new Error('O destino de navegação não possui href.');

  const target = new URL(href, page.url());
  await Promise.all([
    page.waitForURL((url) => url.pathname === target.pathname),
    link.click(),
  ]);
}

/** Adota o perfil local no onboarding e aterrissa no Chat. */
export async function adoptProfile(page: Page, profileName = 'Mateus') {
  await page.goto('/');
  await expect(page).toHaveURL(/\/onboarding$/);
  await page.getByRole('button', { name: new RegExp(profileName) }).click();
  await expect(page).toHaveURL(/\/chat(?:\/[^/]+)?$/);
}

/**
 * Cria um projeto pela jornada REAL do dono: título, objetivo e (opcionalmente) prazo.
 *
 * Não toca em sigla, repositório, workflow nem equipe — quem os define é o sistema. Um teste que
 * preenchesse esses campos estaria provando uma tela que o dono não usa.
 */
export async function createProject(
  page: Page,
  options: { name: string; objective: string; deadline?: string },
) {
  await navTo(page, 'Projetos');
  await page.getByRole('button', { name: 'Novo projeto' }).click();
  await page.getByLabel(/^Título/).fill(options.name);
  await page.getByLabel(/^Objetivo e contexto/).fill(options.objective);
  if (options.deadline) {
    await page.getByLabel(/Prazo desejado/).fill(options.deadline);
  }

  await page.getByRole('button', { name: 'Criar projeto' }).click();

  const destination = await Promise.race([
    page.waitForURL(/\/chat(?:\/[^/]+)?$/).then(() => 'chat' as const),
    page
      .getByRole('heading', { name: 'Editar projeto' })
      .waitFor({ state: 'visible' })
      .then(() => 'edit' as const),
  ]);

  if (destination === 'edit') {
    await page.getByRole('button', { name: 'Voltar para projetos' }).click();
  } else {
    await navTo(page, 'Projetos');
  }

  await expect(page.getByRole('heading', { name: 'Projetos', level: 1 })).toBeVisible();

  // O projeto na lista é um BOTÃO que carrega o nome. Casar por texto solto pegava também o
  // valor do campo do formulário que ainda estava no DOM — no mobile isso resultava em
  // "existe mas está oculto", uma falha que nada tinha a ver com a criação.
  await expect(
    page.getByRole('button', { name: new RegExp(escapeForRegExp(options.name)) }).first(),
  ).toBeVisible();
}

/** Torna um projeto o ativo, pelo seletor global do Dashboard. */
export async function activateProject(page: Page, name: string) {
  await navTo(page, 'Dashboard');
  const selector = page.locator('#global-project-selector');
  await selector.waitFor({ state: 'visible' });
  // O <select> nativo é o controle real; as <option>s ficam ocultas quando ele
  // está fechado, então selecionamos por label sem exigir visibilidade delas.
  await selector.selectOption({ label: name });
}

/** Escapa o que for metacaractere de regex num nome livre digitado pelo dono. */
function escapeForRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Troca o modo de apresentação pelas Configurações.
 *
 * Telas de operação (modelo do chefe, provedores, execução de serviços) só existem nos modos
 * Técnico e Administrador — no modo Negócio elas seriam jargão exposto ao dono. Um teste que
 * exercita operação precisa entrar no modo de quem opera.
 */
export async function setPresentationMode(page: Page, label: 'Negócio' | 'Técnico' | 'Administrador') {
  await page.addInitScript((modeLabel) => {
    const mode =
      modeLabel === 'Técnico' ? 'technical' : modeLabel === 'Administrador' ? 'admin' : 'business';
    const current = JSON.parse(
      localStorage.getItem('poseidon-presentation') ?? '{"state":{"modeByProfile":{}},"version":1}',
    );
    current.state = current.state ?? {};
    current.state.modeByProfile = { ...(current.state.modeByProfile ?? {}), prof1: mode };
    current.version = 1;
    localStorage.setItem('poseidon-presentation', JSON.stringify(current));
  }, label);
}
