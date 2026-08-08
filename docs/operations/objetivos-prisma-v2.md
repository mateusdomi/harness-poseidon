# Prisma — Pacote de cards-objetivo (perfil v2, Understand → Build → Prove)

**Fonte única de verdade:** `Prisma_Especificacao_Tecnica_MVP.md` v3.0 (04/08/2026). Em conflito,
valem o pseudocódigo (§5–6) e a suíte de testes (§16).
**Stack decidida (herdada das decisões de arquitetura da avaliação, a spec não fixa stack):**
Frontend **React** (base: ZIP Lovable `bright-vision-interface-main`) · Backend **.NET 8 /
ASP.NET Core** · **Oracle 19c**, schema **`PRISMA_APP`** · REST + OpenAPI · fuso
America/Sao_Paulo (§14).
**Regra transversal de PADRÕES DE PRODUTO (INVIOLÁVEL, ordem do dono 2026-08-08):** cumprir `docs/product/PADROES-DE-PRODUTO.md` do Poseidon — produto FINAL (nunca 'MVP'/'demonstração' em UI), tela de login sem usuários demo/credenciais, favicon/título/branding do produto (nunca Lovable/ferramenta), troca de senha OBRIGATÓRIA no 1º acesso, suíte E2E Playwright das jornadas (login+troca de senha+núcleo por perfil, console limpo). Violação = reprovação.
**Regra transversal de AUTO-VERIFICAÇÃO (premissa básica de desenvolvimento, ordem do dono 2026-08-08):** antes de submeter, o executor RODA `dotnet build` e `dotnet test` da solução (e `npm run build`/testes do frontend quando tocou nele) e exercita a feature ponta a ponta com chamadas reais; só submete VERDE, com as saídas resumidas no sumário final. Submissão que reprova na sonda de build da plataforma queima um ciclo de validação — o review independente existe para achar o que o desenvolvedor NÃO consegue ver, não erro de compilação.
**Regra transversal de entrega:** todo objetivo mantém `docs/ACESSO.md` atualizado no repositório do produto — URLs de tela e API, credenciais de demonstração, passo a passo de login e acesso ao banco (schema/usuário de dev). O pacote de acesso do Poseidon é montado a partir dele; entrega sem `docs/ACESSO.md` atualizado reprova.
**Regra transversal de reprovação:** dados em memória no caminho de produção = reprovação;
driver Oracle declarado como dependência é obrigatório; autenticação própria sem SSO (§14);
sem integrações externas no MVP (§14).

---

## Objetivo 1: Fatia vertical navegável — login + tela de Riscos contra API e banco reais

**Objetivo** — O usuário abre o navegador, faz login com e-mail corporativo e senha, e vê a
tela de **Riscos** com o dataset de referência da spec, filtrado pelo escopo do seu perfil —
com o frontend fornecido (bright-vision) integrado de verdade a uma API .NET que lê do Oracle.
Nada de camadas soltas: é a espinha dorsal do produto de ponta a ponta, navegável.

**Critérios de aceite**
1. Sistema instala e sobe seguindo a documentação de execução do repositório (README/runbook) — §16-geral e aceite "instalável".
2. Login por e-mail + senha com política de senha; usuários provisionados por seed da GRC (§14 — autenticação própria, sem SSO).
3. Tela **Riscos** lista código, risco, inerente, residual e estado de aprovação, com busca (§12, tabela "Riscos"), consumindo `GET` da API real — sem nenhum dado mockado no frontend.
4. Os 7 riscos do dataset de referência (§15) estão semeados **no Oracle** (schema `PRISMA_APP`) via migration versionada e aparecem na tela.
5. Escopo por perfil aplicado já nesta fatia: um Gerente vê apenas riscos da sua gerência; a GRC vê tudo (§7.2, T17 parcial).
6. Ficha do risco abre em modo leitura com a hierarquia Fator → Controle → Plano aninhada (§12 "Ficha do risco", leitura).
7. Console do navegador sem erros; chamadas de rede documentadas em OpenAPI (`/swagger` acessível).

**Restrições de stack** — React (base bright-vision) + .NET 8 + Oracle 19c `PRISMA_APP`. Driver
Oracle (`Oracle.ManagedDataAccess`/`Oracle.EntityFrameworkCore`) declarado no `.csproj`.
Persistência em memória no caminho de produção = reprovação imediata.

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: login + listagem de riscos).

**Insumos** — ZIP `bright-vision-interface-main` (frontend base oficial), spec §4 (modelo de
dados), §15 (dataset de seed), §7.2 (escopo), §12 (inventário de telas).

---

## Objetivo 2: Motor de avaliação — cadastro GRC completo, cálculos e matriz de calor

**Objetivo** — A GRC cadastra risco, fatores, controles (peso/testes/eficazes) e planos no
editor com abas; o sistema calcula criticidade inerente e residual **exatamente** como o
pseudocódigo da spec e exibe a matriz de calor 4×4 com alternância inerente/residual e chips
clicáveis. A metodologia vira código verificável.

**Critérios de aceite**
1. Editor de risco (exclusivo GRC) com abas Identificação / Análise / Fatores›Controles›Planos e medidor de aderência ao vivo (§12 "Editor de risco"); cadastro exclusivo da GRC imposto no backend (§7.3, T16).
2. Invariantes do §4.3 garantidos pelo backend: `eficazes ≤ testes`, escalas 1–4 e 1–3, códigos `R-nº`/`FR-nº` únicos sem zero à esquerda, plano com exatamente um pai (controle OU fator — exceção §4.1 válida).
3. Criticidade pela **tabela de consulta** `CRIT` (§5.4), nunca produto numérico — testes T1–T3 passam.
4. Residual por fator com média ponderada por peso, corte **0,70 inclusivo**, redução de exatamente 1 nível; controle sem teste contribui 0 no numerador e peso no denominador (§5.5) — testes T4–T8 passam.
5. Consolidação no risco = pior criticidade entre fatores (§5.6); exemplo resolvido §5.7 reproduzido por teste automatizado.
6. Matriz de calor 4×4 Impacto × Probabilidade com alternância inerente/residual e chips clicáveis que abrem os riscos (§12 "Matriz de calor"), refletindo o dataset §15 (Inerente/Residual da tabela conferem).
7. `parametrizar(...)` e `restaurarExemplo()` disponíveis à GRC (§13) — restaurar recarrega o dataset §15.
8. Verificação de **aderência** avisa e registra pendência, nunca bloqueia (§9, T22–T23).

**Restrições de stack** — idem Objetivo 1. Cálculos no backend (.NET), nunca no frontend.

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: cadastrar risco completo e vê-lo na matriz).

**Insumos** — spec §4 (modelo), §5 (metodologia com pseudocódigo), §9 (aderência), §13
(operações), §15 (dataset), §16.1–16.2 e T22–T23 (testes normativos).

---

## Objetivo 3: ITRC — cálculo, fechamento imutável, dashboard executivo e kanban de planos

**Objetivo** — O indicador estratégico existe e é auditável: o ITRC é calculado pela regra
"todos os planos no prazo", fechado mensalmente em snapshot imutável, exibido no painel
executivo com semáforo, e o acompanhamento dos planos ganha o kanban por status.

**Critérios de aceite**
1. `statusPlano`/`planoEmDia`/`exigeAcao`/`riscoEmDia`/`calcITRC` implementados exatamente como §6.1–6.2; "concluído fora do prazo = atraso"; risco `Aceitar` e risco sem controle e sem plano ficam fora de numerador E denominador — testes T9–T13 passam.
2. Dataset de referência produz **ITRC = 50,0% (3/6), faixa Vermelho** (§6.5, T14) — verificável na tela.
3. Fechamento no dia 28 (automático + trava; ou manual pela GRC) gera `SnapshotITRC` **imutável** com competência, valor, numerador/denominador, faixa e itens (§6.3); editar risco depois **não** altera competência fechada (T15); série histórica consultável (`historicoITRC`).
4. Semáforo ≥90 verde / 75–90 amarelo / <75 vermelho; sem meta numérica, campo `meta` opcional exibido se preenchido (§6.4).
5. **Painel** executivo (§12): ITRC com sinaleiro, KPIs (ativos, críticos residuais, sua fila de aprovação, em aprovação), distribuição por criticidade residual, fila de aprovação e pendências — tudo filtrado por escopo.
6. **Planos de ação** em kanban por status derivado: Não iniciado, Em andamento, Atrasado, Concluído com atraso, Concluído (§12).
7. Cálculos de prazo e fechamento em America/Sao_Paulo (§14).

**Restrições de stack** — idem. Snapshot em armazenamento append-only (§14).

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: fechar competência e conferir imutabilidade).

**Insumos** — spec §6 (pseudocódigo do ITRC), §12 (Painel/Planos), §15 (dataset), §16.3 (T9–T15).

---

## Objetivo 4: Cadeia de aprovação, 5 perfis com escopo completo e central de pendências

**Objetivo** — O ciclo de governança fecha: a GRC envia o conjunto para aprovação, a cadeia
Coordenador → Gerente → Diretor → GRC avança com aprovações na vez de cada um, reprovação
exige justificativa e devolve ao início, edição de aprovado reabre — e cada perfil vê e faz
somente o que a matriz de permissões autoriza, em todas as telas e consultas.

**Critérios de aceite**
1. Máquina de estados §8.1 com a tabela de transições exata: enviar (GRC), aprovar (avança; na etapa `grc` → `aprovado`), reprovar (justificativa obrigatória → `rascunho`, etapa 0), editar aprovado (reabre) — testes T18–T20 passam.
2. Aprovador só age na **sua vez** e no seu escopo (T21); Ponto Focal não aprova e não cadastra (T16); a aprovação é do **conjunto** risco+fatores+controles+planos (§8.2).
3. Os 5 perfis do §7.1 existem com a matriz de permissões §7.3 imposta no **backend** (negativa por padrão); ponto focal/coordenador/GRC inserem evidência sem alterar classificações nem aprovação (§8.2, T24).
4. Escopo de visibilidade §7.2 aplicado a TODAS as listagens/consultas (Painel, matriz, riscos, ITRC, pendências, fila) — T17 passa.
5. Tela **Aprovações** (fila pessoal da vez do usuário) e stepper da cadeia na ficha do risco (§12).
6. Central de **Aderência** com duas colunas: para ação (GRC) × acompanhamento (planos atrasados de outras áreas) (§9/§12).
7. Encerrar/reativar risco só por ação explícita auditável da GRC; risco aprovado nunca some por exclusão silenciosa (§4.3.6, §4.5).
8. Dataset semeia estados de aprovação variados (um em cada etapa, um aprovado, um reprovado) para exercitar a cadeia (§15).

**Restrições de stack** — idem. Autorização no backend; frontend apenas reflete.

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: percorrer a cadeia completa com 4 usuários).

**Insumos** — spec §7 (perfis/escopo/matriz), §8 (cadeia), §9 (aderência), §12 (telas), §16.4–16.5.

---

## Objetivo 5: Notificações, trilha de auditoria e empacotamento executável

**Objetivo** — O sistema avisa quem precisa agir e registra tudo o que aconteceu: digest diário
consolidado por pessoa, alertas imediatos para o crítico, trilha de auditoria imutável visível
à GRC — e o produto empacota em Docker com documentação de instalação, pronto para validação
humana.

**Critérios de aceite**
1. Motor de notificações com a tabela gatilho → destinatário → modo do §10 completa; digest diário default 08:00 America/Sao_Paulo configurável por usuário; GRC sempre recebe o digest de movimentação (não desligável).
2. Alertas imediatos: reprovação (com justificativa), risco parado (plano vencido > 7 dias / etapa parada > 5 dias úteis), fechamento do ITRC (§10.B).
3. Envio por e-mail a partir da caixa `controlesinternoseriscos@trensrio.com.br` via SMTP **configurável por variável de ambiente**; em ambiente de validação sem SMTP, as notificações geradas ficam visíveis e verificáveis na tela **Notificações** da GRC (§12) com payload e templates §17.
4. Trilha de auditoria: toda ação de cadastro/edição/aprovação/reprovação/encerramento/fechamento/evidência gera log com autor, ação, alvo, timestamp — **sem IP**, append-only, tentativa de editar/apagar negada (§11, T25–T26); tela exclusiva GRC com busca por usuário/ação/item.
5. Histórico de aprovação append-only (§4.3.5).
6. `Dockerfile`s (frontend e backend) + `docker-compose` sobem o sistema completo; documentação de instalação/execução validada do zero (aceite "instalável pela documentação").
7. Suíte §16 **completa** (T1–T26) verde e executável por comando único documentado.

**Restrições de stack** — idem. Segredos de SMTP fora do repositório (variáveis de ambiente).

**Gates exigidos** — `Gates: build, tests, e2e` (e2e: fluxo que gera digest + auditoria visível).

**Insumos** — spec §10 (notificações + tabela de gatilhos), §11 (auditoria), §17 (templates),
§12 (telas Auditoria/Notificações), §16.6.

---

## Cobertura (fronteira do MVP §3 → objetivo)

| Item do MVP (§3 🔒) | Objetivo |
|---|---|
| Parametrização | 2 (critério 7) |
| Cadastro de riscos/fatores/controles/planos | 2 |
| Matriz de calor (inerente e residual) | 2 |
| Verificação de aderência | 2 (critério 8) + 4 (critério 6, central) |
| Cálculo e fechamento do ITRC | 3 |
| Dashboard executivo (Painel) | 3 |
| Cadeia de aprovação | 4 |
| Notificações (digest) | 5 |
| Trilha de auditoria | 5 |
| Controle de acesso por perfil | 1 (parcial) + 4 (completo) |
| Autenticação própria (§14) | 1 |
| Telas do inventário §12 | Painel/Planos → 3 · Matriz/Editor → 2 · Riscos/Ficha → 1 · Aprovações/Aderência → 4 · Auditoria/Notificações → 5 |
| Dataset de referência §15 + suíte §16 | 1 (seed) · 2/3/4 (testes por área) · 5 (suíte completa) |
| Docker + documentação de instalação | 5 |

**Fora do MVP (não virou card, conforme §3):** integração ERP/Oracle automática, BPMN,
maturidade GRC, indicadores adicionais, aprovação segregada (🟢 §8.2 — só não impedir no
desenho). Nenhum item da fronteira ficou descoberto.

## Decisões do Chefe sobre as ambiguidades (2026-08-07 — premissas reversíveis, autoridade delegada pelo dono)

1. **Stack ratificada:** React + .NET 8 + Oracle 19c (`PRISMA_APP`). A spec do cliente não fixa
   stack; vale a decisão de arquitetura da janela de avaliação + baseline de engenharia.
2. **E-mail:** SMTP configurável por variável de ambiente; em dev/validação, verificação pela
   tela de Notificações (nenhuma credencial real no repositório — regra de segredos).
3. **`parametrizar(...)` (§13):** escopo mínimo = meta/retenção/templates com defaults do §17.
   Ampliação é pós-MVP.
