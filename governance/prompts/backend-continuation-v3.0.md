---
id: prompt-backend-continuation-v3-0
version: "3.0"
status: historical
createdAt: 2026-07-19T13:19:10-03:00
source: legacy-external-import
supersedes: [prompt-backend-continuation-v2-0]
checksum: sha256:68338ebc7cfec3c57d6547a60d997268c326ba1595a0fad79ae4d89ca0221141
originalChecksum: sha256:efea9b5b2ebc15bdbf64b10412447246945188e5489a3aec7aeac99cc465b6cb
---

> Histórico sanitizado e nunca carregado automaticamente. Procedência: arquivo legado `PROMPT_CONTINUACAO_CLAUDE_FABLE_BACKEND_POSEIDON_v3.0.md`; hash do original registrado no frontmatter. Caminhos locais foram substituídos por `$REPO_ROOT`, `$POSEIDON_FRONTEND_CLONE` ou caminhos relativos versionados. Este prompt foi substituído pela governança canônica vigente.

# POSEIDON — CONTINUAÇÃO BACKEND E INTEGRAÇÃO
## Handoff incremental para Claude/Fable v3.0

Você está assumindo ou continuando um projeto já avançado. Não reconstrua o sistema, não repita fases verdes e não interrompa por dependências externas enquanto existir trabalho independente.

## 1. Documentos de entrada

Leia integralmente:

1. `governance/prompts/backend-mission-v1.3.md`
2. `governance/prompts/backend-continuation-v2.0.md`
3. este adendo

Precedência:

- o prompt original define missão e arquitetura;
- v2 define continuidade e padrão de C#;
- este arquivo define estado atual, refinamentos e medição de progresso;
- código, testes, migrations e Git comprovam o estado executado.

## 2. Repositório

Repositório:

`https://github.com/mateusdomi/harness-poseidon.git`

Clone backend:

`$REPO_ROOT`

Branch:

`develop`

Somente `main` e `develop`. Não criar branches adicionais, não usar force push e não fazer merge em `main`.

Preservar:

- `frontend/**`
- `docs/frontend/**`

## 3. Estado de referência

Audite o remote.

Registro recente indica:

- F0 e F1 concluídas;
- F2 e principais fatias F3–F9 verdes;
- F10 com paridade PostgreSQL, Host servidor, multiusuário, rate limit, carga de 30 usuários, RBAC/ABAC e maquinaria OIDC com IdP fake;
- próximo trabalho independente: F11 hardening;
- smoke real Entra ID permanece externo;
- GNG-3 humano pendente;
- baseline de pelo menos 222 testes backend e zero warnings.

Se `origin/develop` estiver mais avançado, retome do primeiro item incompleto real.

## 4. Autonomia

Não pare para solicitar:

- Entra ID enquanto houver hardening/trabalho independente;
- Telegram antes do adapter e testes fake;
- homologação antes de corrigir defeitos e concluir gates;
- microdecisões ou refatorações necessárias.

OIDC:

- concluir com IdP fake, config tipada, RBAC/ABAC, testes e docs;
- pedir Tenant ID/Client ID somente no smoke real;
- não bloquear F11.

Telegram:

- implementar adapter, idempotência, linking, anexos, resposta na origem, retries, rate limit e testes fake;
- pedir token só no smoke real;
- secret store/config local segura;
- nunca Git, banco em texto puro, docs ou logs.

## 5. Auditoria inicial

Execute:

```bash
cd "$REPO_ROOT"
git status --short
git branch --show-current
git remote get-url origin
git fetch origin
git log --oneline --decorate -20
git worktree list
tools/backend/verify.sh
```

Leia CURRENT_STATE, PROGRESS, MASTER_PLAN, RISKS, DECISIONS_PENDING, contratos e estado frontend.

Registre matriz:

- concluído;
- parcial;
- pendente;
- bloqueado externamente;
- depende de homologação humana.

## 6. Medição objetiva de progresso

Crie/atualize:

`docs/backend/execution/ROADMAP_PROGRESS.md`

Reporte:

1. **Implementado**
2. **Validado**
3. **Integrado**
4. **Homologado**

Mantenha separados:

- Roadmap base F0–F11
- Backlog de refinamentos desta rodada

Pesos fixos do roadmap base:

- F0: 5
- F1: 12
- F2: 25
- F3: 8
- F4: 6
- F5: 5
- F6: 8
- F7: 7
- F8: 6
- F9: 5
- F10: 8
- F11: 5

Total: 100.

Distribua o peso da fase pelos entregáveis documentados. Não conte arquivo criado como comportamento concluído.

Progresso geral:

`Geral = 50% Validado + 30% Integrado + 20% Homologado`

Mostre fórmula, numerador, denominador e itens que impedem 100%. Não altere pesos.

Entregue a estimativa auditável antes de continuar a implementação.

## 7. Homologação

Antes de solicitar aceite:

- sincronizar frontend final;
- HTTP real;
- navegar rotas;
- zero erro não tratado no console;
- zero asset quebrado;
- SignalR subscribe/reconnect/resync;
- E2E contra API real;
- corrigir drift;
- favicon/estáticos válidos;
- roteiro atualizado.

Não declarar GNG-3 sem aceite explícito.

## 8. Backend para refinamentos

Implemente somente o ausente.

### 8.1 Paginação

Coleções crescentes:

- `page`, `pageSize`;
- default 15;
- bounds;
- total;
- ordenação estável;
- filtros;
- tenant scope;
- contratos tipados;
- testes.

Logs/chat/realtime podem usar cursor/sequence.

### 8.2 Busca global

Preferir client-side para rotas. Se server-side:

- somente entidades autorizadas;
- tenant scope;
- paginação;
- resultado tipado;
- sem segredos/payload bruto.

### 8.3 Cockpit

Filtro temporal curto para atividade recente, com default e bounds.

### 8.4 Projeto iniciado

Formalizar:

- metadados seguros editáveis;
- config operacional versionada;
- execução ativa mantém versão;
- nova versão para novas execuções;
- aceite para risco;
- ledger/eventos;
- OCC;
- histórico.

### 8.5 Chat e workflow

Contrato para painel:

- fases ordenadas;
- estado;
- progresso objetivo;
- documentos;
- estados documentais;
- contadores;
- deep links;
- realtime;
- snapshot/delta;
- tenant/project scope.

Percentual não vem do LLM.

### 8.6 Documentos

Garantir:

- leitura;
- append manual;
- autoria/origem;
- edição/save;
- aprovar;
- rejeitar com nota;
- solicitar correção;
- salvar versão manual e aprovar;
- histórico;
- eventos/auditoria;
- OCC.

### 8.7 Quadro

- busca título/ID;
- filtros;
- paginação;
- arquivar/desarquivar;
- arquivadas;
- lote com limites;
- export CSV compatível com Excel;
- estados/transições;
- histórico;
- auditoria/eventos.

Arquivar não é excluir.

### 8.8 Workflows por projeto

- templates;
- draft;
- edição;
- duplicação;
- publicação imutável;
- arquivamento;
- excluir só draft não usado;
- vínculo por projeto;
- versões/comparação;
- fases;
- objetivos/contexto;
- documentos;
- critérios;
- gates;
- agentes/personas;
- skills;
- dependências;
- modos;
- aceite de risco;
- validação.

Execução ativa mantém versão antiga.

### 8.9 Definições de agentes

APIs tipadas:

- criar;
- editar;
- duplicar;
- habilitar/desabilitar;
- arquivar;
- excluir só se nunca usada;
- versões;
- filtros.

Campos:

- nome;
- papel;
- persona;
- missão;
- instruções;
- restrições;
- boas práticas;
- skills/tools/stacks;
- time;
- actor/critic;
- risco;
- provider/account;
- modelo;
- esforço;
- fallback.

Definição e instância runtime são distintas.

### 8.10 Organograma

Read model:

- Chief;
- relações;
- nomes;
- time;
- status;
- tarefa;
- modelo;
- saúde/cota;
- métricas;
- link para definição.

### 8.11 Launcher local

Garantir integração para:

- start;
- navegador;
- status;
- restart;
- shutdown;
- data dir;
- diagnóstico;
- atalho/atualização quando no escopo.

Usuário local não depende de IDE.

### 8.12 Protótipos

Assets por URL válida e confinada. Nenhum hostname inexistente.

### 8.13 Providers/contas/modelos/esforço

Suportar:

- múltiplas contas;
- e-mail/apelido;
- plano;
- autenticação;
- saúde;
- cota/janela/reset;
- capabilities;
- habilitação;
- remoção segura;
- modelos e locais;
- sync;
- routing;
- budgets;
- secret references.

Seleção:

- modelo/esforço padrão por agente;
- account;
- fallback;
- override por invocação;
- decisão automática do Chief;
- capability validation;
- auditoria;
- motivo;
- custo/cota estimados;
- tipos fechados.

Sem strings arbitrárias para esforço sem mapping.

## 9. C# obrigatório

Valem as regras v2:

- classes/agregados para domínio;
- records para DTOs/comandos/eventos/value objects adequados;
- sem `dynamic`;
- sem `object Payload`;
- sem `Dictionary<string, object?>`;
- sem JsonElement em Domain/Application;
- IDs/estados tipados;
- DTOs separados;
- nullable e warnings como erro;
- testes de arquitetura;
- payload concreto;
- migrations separadas;
- OCC;
- tenant scope.

Não refatorar massivamente por estilo.

## 10. F11

Continue sem credenciais externas:

- threat model;
- SAST;
- dependency/secret scanning;
- SBOM;
- headers/cookies/CORS;
- testes de upgrade;
- backup/restore;
- migração;
- recuperação;
- isolamento;
- carga;
- flakiness;
- publish/instalação;
- documentação;
- runbooks;
- permissões;
- auditoria;
- a11y/E2E;
- DoD global.

Dependências finais isoladas:

- smoke Entra real;
- smoke Telegram real;
- homologação humana;
- smoke real de agente que consuma cota, se exigido.

## 11. Encerramento

Antes de parar:

- testes verdes;
- zero warnings;
- zero Docker órfão;
- working tree limpa;
- commit;
- rebase;
- push;
- CURRENT_STATE e ROADMAP_PROGRESS atualizados;
- próximo passo exato;
- não parar só porque falta credencial para smoke independente.

Comece auditando e entregue primeiro a porcentagem auditável. Depois prossiga autonomamente.
