# Runbook do piloto real (Fase 2C)

O piloto é o gate de prontidão do Poseidon: o produto conduz um projeto real do zero à entrega, em
modo autônomo, com o dono apenas nos papéis que a própria lei reserva a ele. Nenhum teste sintético
substitui esta prova — a fase termina nela.

**Este runbook mede o PRODUTO, não o projeto.** Um projeto que sai perfeito com dez intervenções do
dono é um piloto reprovado; um projeto modesto que sai sozinho é um piloto aprovado.

## Pré-requisitos (verificados antes de começar)

| Item | Como conferir | Estado em 2026-08-01 |
| --- | --- | --- |
| Docker ativo | `docker version --format "{{.Server.Version}}"` | ✅ 29.5.2 |
| Contas autenticadas | `./poseidon agent doctor` | ✅ 7 contas em `~/.harness` |
| Instrumentação | spans, MAST, `model_invocations`, `chief_turn_intents`, orçamentos | ✅ ligadas |
| Painéis | `/reliability` (modo Técnico), Cockpit, Central de Entregas | ✅ publicados |

Contas cujo executor não tem adapter (hoje, a de Kimi) são **ignoradas pelo escalonador**, não
causam falha: `AgentAccountScheduler` filtra por `ExternalAgentExecutorFactory.IsImplemented`.

### Navegadores da estação macOS de QA

Esta estação mantém navegadores, drivers e os motores do Playwright prontos para os testes
humanos da Bruna e do produto gerado. Antes de concluir que não há navegador, verifique o `PATH`
e estes locais conhecidos:

| Recurso | Comandos no `PATH` | Local conhecido |
| --- | --- | --- |
| Google Chrome | `google-chrome`, `chrome` | `/Applications/Google Chrome.app/Contents/MacOS/Google Chrome` |
| Chromium | `chromium`, `chromium-browser` | `/Applications/Chromium.app/Contents/MacOS/Chromium` |
| Firefox | `firefox` | `/Applications/Firefox.app/Contents/MacOS/firefox` |
| Microsoft Edge | `msedge`, `edge` | `/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge` |
| WebDrivers | `chromedriver`, `geckodriver` | `/opt/homebrew/bin/` |
| Motores Playwright | — | `~/Library/Caches/ms-playwright` |

O gate versionado continua sendo `tools/backend/verify-screens.sh`. Para uma reprodução focada,
use o Playwright instalado pelo projeto (`cd frontend && npx playwright test`); Python e Node.js
também podem iniciar `chromium`, `firefox` ou `webkit` em modo headless. Um smoke direto pode usar
`google-chrome --headless --screenshot=<arquivo> <url>`. Registre URL, navegador, comando, resultado
e evidência; a presença do binário, sozinha, não comprova o comportamento da tela.

## Insumo do dono — o único

Nome e objetivo do projeto, **em prosa, pelo chat**, exatamente como um cliente leigo faria. Nada
de card, nada de especificação, nada de vocabulário técnico. O intake existe para transformar isso
em trabalho; adiantar a tradução destrói justamente o que o piloto quer medir.

Perfil recomendado: full-stack pequeno e completo (frontend + API + banco), no máximo uma
integração externa, prazo-alvo de ~1 semana, e **valor real** — projeto de brinquedo não gera
incentivo genuíno de aceite, e sem isso o Termo de Aceite vira formalidade.

## Regras durante o piloto

1. **Modo `autonomous` desde a criação.** É o padrão desde a Fase 1E; não é preciso configurar.
2. **O dono só interage em três lugares:** no intake (respondendo o que a Bruna perguntar), nos
   gates HITL (Termo de Aceite na Fase 7, aprovação de mudança na Fase 8) e em barreiras Classe A.
3. **Toda tentação de intervir fora disso é ANOTADA, não atendida.** Cada intervenção extra que se
   mostrar necessária é um defeito de produto — e é exatamente o achado que o piloto existe para
   produzir. Intervir "só desta vez" apaga o achado e falsifica o resultado.
4. **Não interromper o piloto para melhorar algo que não o bloqueia.** Melhoria identificada vira
   linha na retro, não commit no meio da prova.

## Se o piloto travar

A barreira **é** o achado. Registrar com precisão — bloco, card, modo de falha — corrigir apenas o
mínimo que destrave (com parecer do crítico) e continuar. Piloto abandonado é fase reprovada; um
piloto que travou três vezes, foi destravado e chegou ao fim vale mais que um piloto que nunca
começou.

## Encerramento

* Entrega acessível por **Executar Projeto** (modo Negócio);
* **Termo de Aceite** aprovado pelo dono;
* ZIP de documentos baixado da **Central de Entregas**.

## Critérios de aceite (medem o produto)

| # | Critério | Onde verificar |
| --- | --- | --- |
| 1 | Zero intervenção do dono fora de intake, HITL e Classe A | Conversas do projeto |
| 2 | Todas as 9 fases (ou a variante aplicável) percorridas com gates registrados | Ledger |
| 3 | Zero violação de invariante: publicação só pela Bruna, revisor≠implementador, guard de ações protegidas, nenhum segredo fora do lugar | Ledger + `scan-secrets` |
| 4 | Custo total dentro do orçamento estimado pela `EffortPolicy` no plano — desvio acima de 30% é achado | `/reliability` + `model_invocations` |
| 5 | Nenhuma mensagem ao dono com termo técnico | Auditoria do léxico sobre as conversas |
| 6 | Retro publicada em `docs/architecture/audits/bruna/execucao/RETRO-PILOTO.md` | O próprio arquivo |

## Diário de bordo

Não há relatório manual. A retro usa o que o sistema já registra: ledger, Cockpit, `/reliability`
(produtividade por assinatura, pass@k, distribuição MAST e conversa por assunto). Se algum desses
números não existir ao final do piloto, isso também é achado — significa que a instrumentação não
cobre o caminho real.
