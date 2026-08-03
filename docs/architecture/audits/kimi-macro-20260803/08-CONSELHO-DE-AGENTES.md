# 08 — O Conselho de Agentes, e por que os 6 pareceres travaram

> **A pergunta:** o Conselho parou porque não havia crítico que satisfizesse as invariantes
> (comportamento correto), ou porque o roteamento falhou (bug)?
>
> **Resposta: as duas coisas, em sequência. A invariante actor≠critic foi aplicada
> corretamente sobre um elenco que ficou com uma conta só — e aí um defeito real
> transformou uma espera legítima em impedimento permanente.**

---

## 1. O que o Conselho é

`src/Modules/Harness.Modules.Coordination/Application/AgentCouncilPolicy.cs` (política pura).

| Item | Valor |
|---|---|
| Gatilho | **Política, não julgamento**: só na saída da fase `4-Planejamento`, e só se houver documento produzido (`ShouldConvene`) |
| Por quê ali | É a última fase em que corrigir ainda é barato; depois o erro custa código escrito e refeito |
| Assentos obrigatórios | `playbook-po`, `playbook-arquiteto`, `playbook-tech-lead` |
| Assentos condicionais | `playbook-security`, `playbook-dba-dados`, `playbook-sre-devops`, `playbook-qa` — cada um com um predicado sobre o `CouncilContext` |
| Piso | `MinimumCouncil = 3` — abaixo disso o veredito é `council.incomplete` |
| Ciclos | `MaximumReviewCycles = 3` |
| Consolidação | **Um** veredito bloqueante segura a transição. Não é maioria: *"maioria decide preferência; evidência decide risco"* |
| Dissenso | Registrado sempre, bloqueante ou não |
| Override da Bruna | Pode **ampliar** a mesa, nunca reduzir o núcleo |
| O Conselho decide? | **Não. Ele critica.** A decisão continua da Chefe |

A modelagem é boa e o raciocínio está escrito no próprio código. **IMPL + TEST.**

---

## 2. O que aconteceu de verdade — reconstituição por id

Projeto: `QA-PROVA-LIMPA-20260802-EMPRESTIMOS` (`01KZ24JCFRHN2RGP8NHGP75JMK`).

### 17:37:34Z — a fase 4 fecha os 3 documentos e o Conselho abre 6 assentos

```
01KZ4B7K34KMCSYNH0T36GF5NN  parecer de playbook-po
01KZ4B7K3AB3NRXWJTYSZR8XHF  parecer de playbook-arquiteto
01KZ4B7K3GWWKX3JD9F5TYJCZ1  parecer de playbook-tech-lead
01KZ4B7K3P5BQTGASVA0R45486  parecer de playbook-security
01KZ4B7K3W2SA9FQC27QGTBAHC  parecer de playbook-dba-dados
01KZ4B7K42QRGDEZTM8GVEG6W8  parecer de playbook-qa
```

3 do núcleo + 3 condicionais (security, dba, qa). `sre-devops` não foi convocado.

### 17:37 e 17:39 — as 6 primeiras e segundas tentativas morrem em milissegundos

Causa: **OPS-058**. `AgentAccountConfiguration.CriticDefaultScopes` declarava que o conselheiro
escreve `docs/conselho/**`, mas `AgentPathScopePolicy` só conhecia dois papéis, e a tradução
papel→escopo estava copiada como ternário `frontend ? Frontend : Backend` em **seis arquivos**.
O crítico caía calado em `Backend`, e `docs/conselho` não é raiz de backend →
`agent_path_scope_denied`.

Efeito: a esteira respondia `council.incomplete` — que descreve o sintoma e esconde a causa.
**O Conselho não podia acontecer NUNCA.**

Corrigido e commitado em `520dd71e` (`AgentPathScopeKind.Critic`, `AgentPathScopePolicy.KindForRole`
num lugar só, e a recusa passou a dizer qual caminho caiu fora).

### 17:40–17:50 — as terceiras tentativas EXECUTAM e entregam

```
task_id                     attempt                     state            invoked_at
01KZ4B7K34...  01KZ4BD7SJHZNBH640GBEY4P42  awaiting_review  17:43:01Z
01KZ4B7K3A...  01KZ4BHNMJ6904CSAPBAS9CM13  awaiting_review  17:44:01Z
01KZ4B7K3G...  01KZ4BKJG2JA34CGCVSYBSK17H  awaiting_review  17:45:37Z
01KZ4B7K3P...  01KZ4BPDFRQRV2F0BNEEA9MF0V  awaiting_review  17:46:42Z
01KZ4B7K3W...  01KZ4BRM5AR0WNYM5AHPYRW7ED  awaiting_review  17:47:52Z
01KZ4B7K42...  01KZ4BTH11YHPGCSJDNWTXYK51  awaiting_review  17:50:43Z
```

**A correção funcionou.** Os seis pareceres foram produzidos.

### O ponto decisivo — quem os produziu

```sql
select attempt_id, account_alias, provider, output_tokens, outcome
from model_invocations where attempt_id like '01KZ4B%';
```

| attempt | account_alias | provider | output_tokens | outcome |
|---|---|---|---|---|
| todos os 6 | **`worker-antigravity-review`** | antigravity | 0 | `completed\|usage_unknown` |

**Os seis assentos do Conselho foram executados pela MESMA conta.**
E essa conta é a única critic operacional.

### 17:50 → 18:06 — a revisão independente não encontra ninguém

```
attempt_events (01KZ4BTH11YHPGCSJDNWTXYK51):
17:47:54Z  log    Attempt started.
17:50:49Z  log    Attempt submitted for review.
18:06:03Z  note   ERROR  "Revisão independente indisponível após 4 tentativas
                          (critic.none_available). O trabalho entregue está preservado..."
```

15 minutos = 4 adiamentos × `ReviewRetryBackoff` de 5 min → `MaximumReviewInfrastructureFailures = 4`
→ `EscalateUnreviewableTaskAsync` → card `escalated` / `blocked`.

---

## 3. A cadeia de decisão exata (o que a ordem de auditoria pediu no §22)

Card `01KZ4B7K42QRGDEZTM8GVEG6W8` — parecer de QA, tentativa `01KZ4BTH11YHPGCSJDNWTXYK51`,
produtor `worker-antigravity-review`. Filtro aplicado: `SelectCriticAliases(producerAlias, now)`.

```
Papel exigido: critic

chief-claude-primary
  → INELEGÍVEL — account.role_not_allowed
     allowedRoles = ["chief-orchestrator"]

worker-claude-secondary
  → INELEGÍVEL — account.role_not_allowed
     allowedRoles = ["backend-specialist","frontend-specialist"]
     (a conta tem cota e faz review; simplesmente não declara o papel)

worker-codex-frontend
  → INELEGÍVEL — account.role_not_allowed

worker-kimi-ui
  → INELEGÍVEL — account.role_not_allowed
     E TAMBÉM account.adapter_not_implemented (executor kimi-code sem adapter)
     E TAMBÉM capability "review" não declarada no ExecutorCatalog

worker-glm-general
  → INELEGÍVEL — account.disabled + quota até 2026-08-06T10:11Z

worker-antigravity-review
  → INELEGÍVEL — account.actor_cannot_be_critic
     (ELA PRODUZIU o parecer; a invariante está correta)

worker-codex-critic
  → ELEGÍVEL POR PAPEL, indisponível no instante
     account-availability.json: ConsecutiveFailures=1,
     "availability.recovered" só às 19:15:37Z — depois da escalação das 18:06

RESULTADO: lista vazia → critic.none_available
```

### Então: bug ou comportamento correto?

**Ambos, em camadas. Separe as três:**

| Camada | Veredito |
|---|---|
| A invariante actor≠critic ter recusado a Antigravity | **CORRETO.** É exatamente a garantia que o produto vende |
| O elenco ter ficado com **um** critic disponível | **RISCO DE CONFIGURAÇÃO**, não bug. Duas contas critic, uma delas obrigatoriamente consumida como ator do próprio Conselho |
| Escalar como impedimento do CARD em 15 minutos | **BUG.** Falta de revisor não é falha da entrega, e 15 min é menos que qualquer janela de cota |
| O card ficar preso **depois** de o crítico voltar | **BUG GRAVE.** `WorkAttemptIsReplannable` não incluía `awaiting_review`, então o replanejamento — único caminho de volta — recusava por estado inválido |

---

## 4. O defeito estrutural: o Conselho consome o próprio elenco de críticos

Este é o achado arquitetural mais interessante da auditoria.

```mermaid
flowchart LR
    C[Card de parecer<br/>do Conselho] -->|papel exigido| CR[critic]
    CR --> A[worker-antigravity-review<br/>ATOR do parecer]
    A --> AW[awaiting_review]
    AW -->|precisa de critic ≠ ator| X{{restam: worker-codex-critic}}
    X -->|sem cota| DEAD[critic.none_available]

    classDef bad fill:#7f1d1d,stroke:#fca5a5,color:#fff
    class DEAD bad
```

O parecer do Conselho é, ele mesmo, um card comum — e todo card comum exige revisão
independente. Como o parecer só pode ser escrito por quem tem papel `critic` (é quem tem o
claim `docs/conselho/**`), **cada assento do Conselho consome uma conta critic como ATOR**,
e a revisão desse assento precisa de **outra** conta critic.

Com N contas critic, o Conselho precisa de **no mínimo 2** para funcionar, e de mais para
funcionar em paralelo. Hoje N=2, e uma delas está sem cota → N efetivo = 1 → **impossível**.

**Pergunta arquitetural aberta (para a próxima decisão, não para esta auditoria):**
o parecer do Conselho *deveria* passar por revisão independente? Ele já é, por construção,
uma opinião crítica sobre trabalho de terceiro. Revisar o parecer do crítico é uma regressão
infinita que a política não resolveu. As opções são: (a) isentar cards `council` da revisão
por par; (b) fazer a revisão do parecer aceitar qualquer papel ≠ ator; (c) exigir 3+ contas
critic. **Recomendação em [17-RECOMENDACOES-ARQUITETURAIS](17-RECOMENDACOES-ARQUITETURAIS.md).**

---

## 5. Diversidade de lentes — o segundo problema

A política existe para garantir que *"três agentes com a mesma lente produzem a mesma cegueira
três vezes e dão a ela aparência de consenso"*. Medido no banco:

| Assento | Persona que efetivamente executou | Conta | Modelo |
|---|---|---|---|
| `playbook-po` | **Software Architect** ⚠️ | antigravity | default da CLI |
| `playbook-arquiteto` | Arquiteto | antigravity | default da CLI |
| `playbook-tech-lead` | Tech Lead | antigravity | default da CLI |
| `playbook-security` | Security | antigravity | default da CLI |
| `playbook-dba-dados` | DBA / Dados | antigravity | default da CLI |
| `playbook-qa` | QA | antigravity | default da CLI |

Dois problemas:

**(a) `playbook-po` não existe no catálogo de personas.**
`AgentCouncilPolicy.CoreSeats` usa a chave `"playbook-po"`; `CanonicalAgentDefinitions` registra
`"playbook-product-owner"`. A resolução falha, cai no fallback de persona inferida, e o assento
do **Product Owner foi ocupado por um Software Architect** — a lente que menos deveria se
repetir numa mesa que já tem um Arquiteto. O mesmo vale para `playbook-sre-devops`
(catálogo tem `playbook-devops` e `playbook-sre-sustentacao`).

**(b) as 6 lentes foram 6 prompts para o mesmo modelo, na mesma conta.**
A diversidade é só de instrução. Não há diversidade de provedor, de modelo nem de effort — e
não podia haver, porque só uma conta critic estava viva.

---

## 6. O conserto — commitado DURANTE esta auditoria

Quando comecei a auditar, este conserto estava apenas no working tree. **Outra sessão viva no
mesmo repositório o commitou enquanto eu escrevia** — e foi além do que eu tinha visto. Três
commits, em camadas, cada um revelado pelo anterior:

| Commit | O que corrige |
|---|---|
| `e3c4025a` | **Duas camadas.** (1) O teto de adiamentos conta FALHA, e ausência de revisor não é falha: o elenco passa a ser consultado à parte (`CriticRosterHasCandidate`) e quem decide desistir é o relógio, não um contador de 20 minutos. (2) Escalado, o card só volta por replanejamento, e a precondição enumerava `rejected/cancelled/abandoned` — a tentativa parou em `awaiting_review`. O predicado agora **nega** os dois estados que a regra nomeia |
| `7ea4b60e` | Tirado o estado inválido, os seis assentos **trocaram de sintoma**: conflito de idempotência. A chave era card+versão+hash, mas a carga levava um ULID sorteado a cada rodada — mesma chave, carga diferente. E como o inbox guarda também as mutações recusadas, a primeira recusa trancava todas as rodadas seguintes, inclusive as já corrigidas |
| `75971b37` | A carência fixa de 2h criava o erro seguinte: o único crítico elegível volta às 01:21 e os cards escalariam às 00:24 — **uma hora antes de a resposta poder existir**. Agora a espera é o maior entre a carência mínima e a **janela declarada pelo provedor**, com teto de 12 h |

Testes novos: `tests/Harness.UnitTests/Agents/ReviewerShortageWaitTests.cs`,
`tests/Harness.IntegrationTests/Persistence/WorkChainStoreBehavior.cs`.

**Estado agora: IMPL ✅ · TEST ✅ · commit ✅ · E2E ❌ — os 6 cards continuam `escalated`.**

O último commit é a evidência de que a análise deste documento está certa: mesmo com o conserto,
**o Conselho não anda enquanto `worker-codex-critic` estiver sem cota** (medido durante esta
auditoria: `QuotaLimited` até `2026-08-04T01:21:25Z`). O achado `F-03` — o Conselho consome o
próprio elenco de críticos — **permanece aberto e é o bloqueador real.**

---

## 7. Achados desta seção

| ID | Sev. | Tipo | Título | Bloqueia piloto? | Bloqueia autonomia? |
|---|---|---|---|---|---|
| `F-03` | **CRITICAL** | ARCHITECTURAL RISK | O Conselho consome o próprio elenco de críticos: com 2 contas critic, uma é sempre o ator e a outra é o único revisor possível de todos os 6 assentos | **SIM** | **SIM** |
| `F-12` | **HIGH** | DEFECT | Falta momentânea de revisor escala o CARD em 15 min e o prende para sempre (o replanejamento recusava `awaiting_review`). **Corrigido em `e3c4025a`+`7ea4b60e`+`75971b37` durante esta auditoria; testado, ainda não provado em E2E** | **SIM** | **SIM** |
| `F-13` | **HIGH** | DEFECT | `playbook-po` e `playbook-sre-devops` não existem no catálogo de personas: o assento de Product Owner foi ocupado por um Software Architect, destruindo a diversidade de lente que é a razão de existir do Conselho | Não | **SIM** |
| `F-14` | **MEDIUM** | ARCHITECTURAL RISK | As 6 lentes do Conselho rodaram no mesmo modelo, mesma conta, mesmo effort — a diversidade é só de prompt | Não | Não |
| `F-15` | **LOW** | OBSERVABILITY GAP | `council.incomplete` descreve o sintoma e esconde a causa; a mesma mensagem apareceu para path scope negado e para ausência de revisor | Não | Não |
