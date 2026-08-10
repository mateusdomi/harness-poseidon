# QA do produto entregue — como se testa profissionalmente

**Escopo:** produto entregue · **Seleção:** toda ValidationMission

> Complementa [`definition-of-done.md`](definition-of-done.md) — que diz *quais evidências* a
> entrega precisa — respondendo a pergunta que faltava: **como um agente sabe COMO testar**.
> A estratégia é proporcional ao perfil; nenhum nível é ritual.

## 1. O plano de QA deriva, não se inventa

Todo projeto produz uma estratégia rastreável:

```
Requisito → nível de teste → cenário → ambiente → evidência
```

Um requisito sem cenário em nenhum nível é lacuna de cobertura, e a rastreabilidade de requisito
já a acusa. O inverso também vale: teste que não sustenta requisito nenhum é custo sem cliente.

## 2. Níveis e quando cada um se aplica

| Nível | O que prova | Quando |
|---|---|---|
| **Unitário** | regra de domínio isolada | sempre; é onde mora o cálculo que não pode errar |
| **Integração** | camadas conversando (aplicação ↔ persistência real) | sempre que há banco; SQLite/testcontainer local, nunca banco compartilhado |
| **Contrato** | a API publica o que o consumidor espera | sempre que há API consumida por interface ou terceiro |
| **E2E navegador** | a jornada da pessoa, ponta a ponta | produto com tela; jornada derivada dos critérios de aceite |
| **Acessibilidade** | pessoa real consegue operar | produto com tela (ver §4) |
| **Segurança/autorização** | deny-by-default e escopo valem | produto com autenticação; cada perfil testado contra recurso alheio |
| **Performance/carga** | o alvo declarado se sustenta | **somente** com carga respondida no perfil operacional (§5) |
| **Concorrência/idempotência** | duas ações simultâneas não corrompem | onde o domínio tem disputa real (aprovações, contadores, fechamentos) |
| **Migração/recuperação** | schema reproduzível; dado sobrevive a reinício | sempre que há persistência |
| **Smoke/regressão** | o que já funcionava continua | sempre; regressão nomeia o defeito que a originou |

## 3. E2E com Playwright — as práticas que separam prova de flakiness

- **Seletores resilientes**: papel e texto visível (`getByRole`, `getByLabel`), nunca classe CSS
  nem XPath estrutural. O teste acompanha o usuário, não o DOM.
- **Comportamento visível, não implementação**: o teste afirma o que a pessoa vê; acoplamento a
  detalhe interno quebra a cada refactor sem defeito nenhum.
- **Readiness real**: espera por elemento/estado, jamais `sleep` fixo — o mesmo princípio da
  verificação nativa do Poseidon.
- **Isolamento**: cada teste prepara e limpa o próprio dado; ordem de execução não pode importar.
- **Fixtures** para autenticação e dados repetidos; login pela UI uma vez, não em todo teste.
- **Viewports**: a jornada principal roda em desktop E em viewport estreito (ver §6).
- **Screenshot/trace são evidência adicional**, nunca a asserção.
- **Retry não mascara defeito**: teste flaky é defeito do teste; retry automático para "passar" é
  proibido como política.

## 4. Acessibilidade — WCAG 2.2 AA como objetivo default

Salvo constraint superior no perfil, produto Web valida:

- navegação completa por **teclado**, com **foco visível** em cada parada;
- **labels** programáticos em todo controle; HTML semântico antes de ARIA, ARIA quando necessário;
- **contraste** mínimo AA em texto e componentes;
- **feedback de erro** perceptível sem depender de cor;
- zoom de texto a 200% sem perda de função (**responsive zoom**).

Evidência: verificação automatizada (axe ou equivalente) na jornada principal + os pontos de
teclado/foco na jornada E2E. Automação não cobre tudo; cobre o que impede o pior.

## 5. Performance deriva do perfil operacional

Teste de carga **não é exigido cegamente**. Ele se aplica quando o perfil operacional tem carga
respondida ou assumida de fonte declarada — e o cenário é o do perfil, nunca um número inventado.

Registro obrigatório do resultado: workload, concorrência, duração, thresholds e desfecho. Um
teste de carga sem threshold declarado é um benchmark, não um teste.

## 6. Responsividade como evidência, não como screenshot

Um screenshot bonito de desktop **não aprova frontend**. A prova mínima de um produto Web:

- a jornada principal executa em viewport desktop (≥1280px) e estreito (~390px);
- nada da jornada depende de hover-only nem de ponteiro fino;
- conteúdo não é cortado nem sobreposto nos dois extremos.

## 7. UAT

O aceite do usuário é momento próprio, com roteiro derivado dos critérios de aceite — não uma demo.
O que o UAT encontra vira finding rastreado com o critério violado citado; "não gostei" sem
critério é feedback de produto, e entra como requisito/decisão nova.

## 8. Toolchain local de navegador

Executores devem assumir que a máquina de desenvolvimento pode ter browsers e ferramentas E2E
locais, mas a disponibilidade real é sempre verificada pelo doctor determinístico:

```bash
./poseidon tools e2e
```

Baseline conhecido da instalação local:

- Node/global: `playwright`, `@playwright/test`, `@playwright/mcp`, `puppeteer`, `cypress`,
  `selenium-webdriver`, `webdriver-manager`, `taiko`, `nightwatch`, `codeceptjs`.
- Python: `playwright`, `pytest-playwright`, `selenium`, `selenium-wire`, `pyppeteer`,
  `robotframework`, `robotframework-seleniumlibrary`, `requests-html`, `beautifulsoup4`.
- Browsers:
  - Google Chrome: `/opt/homebrew/bin/google-chrome`
  - Chromium: `/opt/homebrew/bin/chromium`
  - Firefox: `/opt/homebrew/bin/firefox`
- Drivers: `chromedriver`, `geckodriver`.
- Cache Playwright: `~/Library/Caches/ms-playwright/`.
- Cache Puppeteer: `~/.cache/puppeteer/`.

Preferência para validação funcional de UI:

1. Playwright.
2. Puppeteer.
3. Selenium.

Se uma funcionalidade é usada pelo cliente na interface, ela precisa ser validada em navegador real.
API e banco podem diagnosticar defeitos, mas não substituem a prova funcional pela UI.
