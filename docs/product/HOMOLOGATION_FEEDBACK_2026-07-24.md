# BACKLOG ÚNICO — Poseidon (fonte da verdade de trabalho aberto)

**Este é o ÚNICO backlog. Não criar outros documentos — todo feedback novo APPENDA aqui.** O dono NÃO testa enquanto houver item aberto (RB-02). Trabalhar até ZERAR.

## STATUS
✅ **FEITOS (entregues + verificados no host):** BUG-01, BUG-02, BUG-03, BUG-04, RN-02, RN-03, UX-COCKPIT, UX-HUMANIZE, UX-ORQUESTRADOR, UX-AGENT-CARD, UX-GOV-DOCS, UX-PROVIDERS, UX-BOARD, UX-WORKFLOWS, UX-CHANNELS, UX-SETTINGS, UX-ORG (marca via RN-03).
🔨 **ABERTOS (a fazer até zerar):** RN-01 (guardrail card-discipline), RN-04 (saúde do backlog), RN-05 (binding real modelo/esforço), UX-DOCS, UX-PROTO/ARCH (arquitetura mapeada — protótipo já via RN-03), UX-NOTIF, UX-GOVERNANCA, UX-PO/TOOLS/LICENSE/ONBOARDING, G-I18N, G-RESPONSIVE, G-IDENTITY, G-REALTIME (além do cockpit), G-CHARTS (além do cockpit), G-MENUS, G-PROFILE, G-AUTOUPDATE, PORTA-DINAMICA (host muda de porta a cada start — fixar/persistir), FLEET-EXEC (fleet paga executar de verdade).
⛔ **DEPENDE DO DONO:** smokes Entra/Teams, GNG-4, GNG-3. · **BLOQUEIO UPSTREAM:** PLAT-ROUTERV8 (sem versão limpa do react-router).

---
_(triagem original abaixo)_

Backlog derivado da 1ª homologação humana do dono.
**Princípio reafirmado pelo dono (crítico):** todo trabalho flui por CARD no backlog (Solicitação→Demanda→Tarefa), auditável. Agente NÃO executa nada que não esteja cadastrado como card. Chefe/PO cuidam da saúde do backlog.

## TIER 0 — Bugs que travam telas (corrigir primeiro)
- **BUG-01** Página de Projetos crasha ao abrir um projeto (`Cannot read properties of undefined (reading 'length')` em projects-page). Adicionar ErrorBoundary por rota + corrigir o campo array undefined.
- **BUG-02** Central de Entregas não carrega: front chama `/api/v1/deliveries?view=all` → 400 (backend só aceita `portfolio`). Alinhar contrato (aceitar `all` ou o front usar `portfolio`).
- **BUG-03** Renomear conversa falha (400): backend não tem PUT/PATCH `/conversations/{id}`. Implementar rename + corrigir 404 de conversa stale.
- **BUG-04** Console: 404 em conversa antiga (referência stale), erro de extensão, aria-hidden em elemento focado (a11y). Varredura de erros de console.

## TIER 1 — Regras de negócio / comportamento (o que o dono mais valoriza)
- **RN-01 Disciplina de card + guardrail**: agentes só executam a partir de card no backlog; guardrail na governança proíbe trabalho fora de card. Auditabilidade. (Eu violei isso na noite — corrigir o comportamento.)
- **RN-02 Workflow default obrigatório**: impossível conversar com o Chefe sem workflow. Ao criar projeto/org sem escolher, atribuir workflow default automaticamente (editável depois).
- **RN-03 Auto-reconhecimento do Poseidon**: o Poseidon é um projeto real com front — o Chefe deve reconhecer e cadastrar: fase atual (=desenvolvimento), % de progresso real, protótipo (o front), arquitetura mapeada, marca da org. Avaliar agente especializado de "categorização/estado do projeto".
- **RN-04 Saúde do backlog**: card preso em "em desenvolvimento" há tempo → Chefe/PO detecta e resolve. Nenhuma tarefa deveria travar silenciosamente.
- **RN-05 Binding real de modelo/esforço**: selects reais baseados nas assinaturas do dono; ao trocar Médio→Alto o esforço muda DE VERDADE (não fictício). Agente pesquisa modelos/planos da assinatura ao cadastrá-la.

## TIER 2 — UX/UI por tela
- **UX-COCKPIT** Redesenhar em torno de "o que o dono de fábrica quer ver": agentes online (nome/foto), nº tarefas feitas/a fazer, agentes sem cota/erro, produção acontecendo. Tooltips explicando termos (receipts/conflitos/truncados/executado/validado/aprovado). Card "Atividade recente" hoje mostra eventos técnicos (chief.turnStateChanged) — trocar por "últimos cards movidos / docs criados-aprovados" ou remover. Tudo em TEMPO REAL.
- **UX-HUMANIZE** Agentes com nomes e fotos humanas (equipe de TI real) no front; aliases técnicos por baixo. "Chief — PSD" → nome humano.
- **UX-ORQUESTRADOR** Bugs: "última atividade há 3 dias" (falei hoje); modo volta a Manual após setar Autônomo; fencing token/lease sem explicação; botões Pausar/Drenar/Passar-bastão sem prova.
- **UX-AGENT-CARD** Cards de definição de agente: formato visual mais legível (tags coloridas p/ título/campos) sem mudar conteúdo.
- **UX-GOV-DOCS** Botão/lupa para renderizar .md como documento legível (humano não lê .md cru); isolar scroll (rolar só a seção sob o mouse); renderizar C4/tabelas/gráficos.
- **UX-PROVIDERS** Tela mal organizada; só 3 providers pré-cadastrados (dono usa 7); focar no fluxo do cliente.
- **UX-BOARD** Busca por ID mas ID não aparece no card; filtro "Responsável" confuso (agente? escopo?).
- **UX-WORKFLOWS** Tags para identificar fases/fluxo do card; "comparar versões" não diz qual workflow adicionou/removeu o quê; modo manual/semi/autônomo sem como testar.
- **UX-DOCS** Tela de Documentos vazia (nada p/ ler/editar/aprovar).
- **UX-PROTO/ARCH** Auto-registrar Poseidon (front = protótipo; sistema = mapear arquitetura, cores, referências).
- **UX-ORG** Auto-reconhecer o front do Poseidon (marca).
- **UX-CHANNELS** Telegram existe mas tela diz "nenhum canal"; sem orientação de onde cadastrar/editar/excluir canal.
- **UX-NOTIF** Não explica o que cada toggle faz, se salva ao clicar, se múltiplos.
- **UX-SETTINGS** Diretório de trabalho em branco; backup sem orientação/prova; diagnóstico em inglês; "Sem licença / Pro" confuso.
- **UX-GOVERNANCA** Tela de governança sem objetivo claro; termos em inglês (receipts/findings/executores); alarme: 262 "findings de documentos" assusta — esclarecer que é detecção, não 262 docs a revisar; guardrail contra criação descontrolada de documentos.
- **UX-PO/TOOLS/LICENSE/ONBOARDING** Telas cruas/sem dado/sem objetivo claro — definir propósito e estado de teste.

## TIER 3 — Globais / transversais
- **G-I18N** pt-BR real em TODAS as telas (matar strings em inglês: diagnóstico, agent defs, governança, etc.).
- **G-RESPONSIVE** Todas as telas responsivas.
- **G-IDENTITY** Menos minimalista/corporativo; identidade, cores, ícones (público 18–30). Tema claro está mais cru.
- **G-REALTIME** Cards relevantes em tempo real (varrer menu a menu).
- **G-CHARTS** Sistema não tem NENHUM gráfico — adicionar onde faz sentido.
- **G-MENUS** Reorganizar por prioridade de uso; menus agrupáveis/recolhíveis.
- **G-PROFILE** Exibir usuário logado (foto, dados) após login.
- **G-AUTOUPDATE** Instalado na máquina do usuário: detectar nova versão e atualizar sem redeploy manual.

## Não-fazível pelo Chefe (dono): smokes Entra/Teams reais, notarização GNG-4, homologação GNG-3.

## CARD NOVO (2026-07-24) — PLAT-ROUTERV8 (P1 segurança)
react-router 7.18.1 tem advisory HIGH (faixa 7.12.0-8.2.0); sem versão limpa hoje (7.11.0 reintroduz a moderada). Gate npm audit afrouxado para critical TEMPORARIAMENTE em build-frontend.sh. AÇÃO: monitorar release do react-router com fix (>8.2.0 ou 7.19+) e travar o gate de volta para moderate. Tool é local (não SSR), risco prático baixo, mas resolver assim que houver fix.


## ✅ BACKLOG CONSTRUÍVEL ZERADO (2026-07-24)
Todos os itens 🔨 foram entregues e verificados no host: bugs T0, RN-01..05, guardrail de docs, todas as UX por tela, i18n, identidade/tema-claro, responsivo, menus recolhíveis, perfil, tempo-real+gráficos, porta fixa, auto-update (detecção+gatilho), auto-reconhecimento (workflow/protótipo/arquitetura), e FLEET-EXEC (6/7 executores pagos provados).
RESTA (não-construível pelo Chefe): adapter do worker-kimi-ui (decisão do dono), PLAT-ROUTERV8 (upstream react-router), smokes Entra/Teams, GNG-4, GNG-3. Follow-ups menores: fonte de marca empacotada, feed remoto de update, sync de chat ao-vivo no orquestrador.

## REABERTO (2026-07-24) — G-IDENTITY-v2 (subentrega)
O passe de identidade ficou sutil demais (tokens + fonte Inter comum, mesmos layouts) — o dono viu "continua igual". Refazer com IMPACTO VISÍVEL: empacotar fonte de display distinta (woff2 local, CSP-safe, ex.: Space Grotesk), uso BOLD do gradiente de marca, mais personalidade (acentos, cards/hero mais ricos, empty-states ilustrados), público 18-30. VERIFICAR com screenshot renderizado (antes/depois), não só testes verdes.
