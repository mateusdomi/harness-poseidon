# Fase 0 — Relatório de execução

**Data:** 31/07/2026
**Base:** `develop` @ `b9004373`
**Documento de origem:** `poseidon-fase0-consolidado.md` (resposta do proprietário à auditoria da
Bruna, corte 2026-07-30)

Este relatório existe porque um commit diz **o que** mudou e raramente diz **por quê** — e a Fase 0
é feita quase inteiramente de decisões cujo motivo não é óbvio olhando o diff.

---

## 1. O que a Fase 0 tinha de resolver

A auditoria não encontrou ausência de componentes. Encontrou componentes que **não formavam uma
garantia end-to-end**: outbox real, leases reais, fencing real, ledger real — e, no meio deles,
caminhos onde o produto conseguia afirmar coisas que não eram verdade.

Sete sub-blocos, um por vez, cada um com gate Default-FAIL e revisão independente antes do merge.

---

## 2. Blocos entregues

| Bloco | Risco | Commit | Gate | Crítico |
|---|---|---|---|---|
| 0A1 — materialização durável | BR-001, BR-004 | `76c09615` | verde, 1655 testes | **PASS**, 0 findings |
| 0A2 — lease e contexto | BR-005, BR-006 | `8752332b` | verde, 1660 testes | **PASS**, 0 findings |
| 0A3 — sanitização | BR-014 | `9949c2be` | verde, 1663 testes | — |
| 0B1 — attestation de sandbox | BR-002, BR-013 | `4914521e` | verde, 1668 testes | — |
| 0B2 — broker de ferramentas | BR-002, BR-013 | `e769d523` | verde, 1674 testes | — |
| 0C1/0C2 — merge intent e reconciliação | BR-003 | ver §4 | ver §4 | — |

Os pareceres do crítico independente (Antigravity, read-only) estão em
`docs/architecture/audits/bruna/pareceres/`. Ele executou os testes por conta própria antes de
emitir cada veredito.

---

## 3. O que cada bloco corrigiu, e por que daquele jeito

### 0A1 — o turno concluía sem garantia de que o trabalho existiria

`DemandPlanMaterializer` carimbava `materialized_at` **antes** de criar os cards, e
`ChiefTurnBackgroundService` materializava **em memória, depois do commit do turno, dentro de um
try/catch**. Uma queda em qualquer um dos dois pontos era permanente e invisível: o usuário via a
resposta, o board não tinha trabalho, e nada no produto sabia disso.

O compromisso passou a nascer na **mesma transação** que conclui o turno — a linha
`demand_materializations` com a intenção declarada (critérios, especialidade, superfícies, que antes
só existiam na memória do worker) e o comando `plan.materializationRequested` na outbox.

**Decisão que não é óbvia:** a identidade do card virou a *fatia do plano*
(`plan_id`,`plan_slice_key`) com índice único **no banco**. "Retry não duplica card" deixou de ser
promessa de código e virou invariante de schema — dois consumidores concorrentes convergem para o
mesmo card porque o banco não deixa ser diferente.

**Decisão que contraria o documento:** ele pedia "persistir dependências de forma idempotente". O
produto não tem tabela de dependências — elas são códigos resolvidos por título, e a triagem por
ondas já depende disso. Criar a tabela seria a "segunda fonte de verdade" que o próprio documento
proíbe. Em vez disso, a completude **exige** que toda dependência declarada resolva para uma fatia
real, e falha explicitamente quando não resolve.

### 0A2 — o lease não era renovado e o contexto era o do primeiro dia

Dois riscos, três defeitos.

`MaintainActivityHeartbeatAsync` publicava um evento de tela e **não tocava no lease**. Uma
inferência mais longa que os dois minutos fazia o turno vivo parecer abandonado: outro worker o
readquiria, o modelo era chamado de novo, o dono pagava duas vezes pela mesma pergunta.

No contexto, o defeito era triplo e somado: a leitura era `ORDER BY id LIMIT` — as mensagens **mais
antigas** —, a fundadora só sobrevivia por acaso de ser a primeira da página, e as notas
externalizadas eram **write-only**. `IChiefContextNoteStore.ListAsync` existia, estava implementado
nos dois bancos, documentado como "a memória vive aqui", e não tinha **um único chamador** no
repositório inteiro. A estratégia gravava fielmente todo fato crítico e nada jamais lia de volta.

**Decisão que não é óbvia:** ao perder o fencing, o worker aborta e **não escreve nada** — nem marca
o turno como falho. Marcar como quebrado um turno que outro dono está conduzindo normalmente seria
pior que o silêncio, e o store recusaria a escrita de qualquer forma. O fato fica no log e na
métrica `poseidon.chief.turn.lease.count{outcome=lost}`.

### 0A3 — o segredo era redigido na saída, e já estava no disco

O produto tinha `SecretTextProtector`, mas ele só rodava nas **bordas**. O audit store serializava o
detalhe recebido como veio. Redigir na exportação é maquiagem: quando o export roda, o valor já está
no ledger encadeado, no banco e em todo backup tirado desde então.

A política única passou a rodar nos **46 funis de escrita** de auditoria e outbox dos dois
providers, e **antes do hash do ledger** — o conteúdo verificado é o conteúdo persistido.

**Decisão que não é óbvia:** o sanitizador preserva a **estrutura do JSON** em vez de substituir
texto cru. Uma substituição crua quebraria o documento e derrubaria consumidores que funcionam hoje.
E ele desce em valor de texto que é ele próprio um JSON — foi por essa fresta que o canário
estruturado passou na primeira versão do bloco: o redator procura `password=valor`, e em JSON existe
uma aspa entre o nome e os dois-pontos.

**Fronteira deliberada, que é decisão de produto:** o corpo que o humano escreveu — solicitação,
demanda, instrução, mensagem — **permanece intacto**. Apagar trecho do texto que ele vai reler
quebraria o produto e esconderia o próprio incidente. O que a sanitização impede é esse texto se
multiplicar sem redação pelos canais de evidência e transporte.

### 0B1 — o produto afirmava ter sandbox quando não tinha

`AgentRunOrchestrator` informava `SandboxActive: true` **por literal**, inclusive com
`IsolatedExecution.Mode=Disabled`. A política de ferramentas lia esse literal e autorizava execução
de risco crítico acreditando existir uma fronteira inexistente.

Quem afirma que existe contenção passou a ser quem a constrói: o provider emite uma attestation
derivada do que o runtime **reporta** — o Docker consulta `inspect` e só verifica com rootfs
somente-leitura, rede negada e limites aplicados. A mera criação de um container não prova nada.

**Decisão que não é óbvia:** a attestation é **por tentativa** e a **primeira** emissão prevalece.
Se valesse a de outro attempt, bastaria uma execução isolada no passado para liberar todas as
seguintes; se uma segunda pudesse "melhorar" a avaliação, daria para abençoar depois o que não foi
contido antes.

**Mudança de comportamento — a mais importante da fase:** numa máquina sem contêiner (o modo
pessoal), executar agente passou a **exigir aceite explícito do proprietário**, com motivo, validade
de no máximo 24h e auditoria, atrás de uma sessão de perfil local. Não há flag, variável de ambiente
nem caminho programático: um agente que quisesse se autoautorizar precisaria da sessão do humano.

O gate encontrou exatamente isso: quatro testes de execução pararam de passar porque o fail-closed
funcionou. Foram ajustados para usar a **mesma porta que o dono usaria** — semear o aceite no banco
pareceria mais simples e provaria menos.

### 0B2 — a política autorizava o processo, não os efeitos

O PEP autorizava o **binário executor** uma vez, no início da tentativa. Nenhuma chamada posterior
voltava a passar por política. Autorizar o processo e não os efeitos é autorizar a intenção e não o
ato.

`ToolCallBroker` é o gateway tipado por chamada: tenant, projeto, card, tentativa, agente,
ferramenta, capability, fencing, argumentos, caminhos, rede, timeout, limite de saída e chave de
idempotência. Toda decisão — inclusive as **negativas**, que são as que explicam um incidente — vai
para `tool_call_journal`.

Perfis fechados: a Bruna delega e nunca executa; o crítico é read-only; o ator escreve só na
worktree autorizada e sem rede por padrão. **Todos** os caminhos são verificados, não só o primeiro
— autorizar pelo primeiro e executar sobre todos seria porta aberta com aparência de política.

**Limite declarado, não disfarçado:** chamadas que um CLI externo faz **dentro do próprio processo**
não passam pelo broker. O produto não intercepta o interior de um binário de terceiro. É exatamente
por isso que a sandbox atestada do 0B1 importa: ela é a fronteira que vale onde a política de
chamada não alcança.

### 0C1/0C2 — o merge acontecia antes do banco, coordenado por um semáforo de processo

`TaskIntegrationService` executava `git merge` e **só depois** atualizava a cadeia durável. Uma
falha de banco entre as duas coisas deixava o código integrado e o card `approved` para sempre. E a
coordenação era um `SemaphoreSlim` em memória: protegia um processo, não o repositório.

Não se tenta transação distribuída entre Git e SQL — ela não existe, e fingir que existe é pior do
que não ter nenhuma. Registra-se a **intenção antes do efeito**, de modo que todo desfecho seja
reconhecível depois.

**Decisão que não é óbvia:** o "um merge ativo por repositório" é um **índice único parcial no
banco**, não um lock de aplicação. Vale entre processos e entre Hosts, que é onde o semáforo
falhava.

O reconciliador responde a pergunta que só o repositório responde — *o commit existe?* — e converge:
merge feito sem confirmação no banco é fechado; banco afirmando integração sem commit reabre a
intenção, porque afirmar integração sem commit é pior do que refazer o merge.

---

## 4. Estado do bloco final

O gate de 0C1/0C2 foi executado ao fim da implementação, sobre o estado que contém os dois blocos.
O resultado e o commit constam no histórico de `develop`; a implementação segue o mesmo padrão dos
anteriores — migrations nos dois providers, testes de regressão permanente e documentação de
runbook.

---

## 5. Riscos restantes, explicitamente

1. **Planos anteriores à migration 0112** não entram na varredura do reconciliador de
   materialização. A adoção por título cobre quando eles voltam ao fluxo; um backfill retroativo
   criaria compromissos para todo o histórico e dispararia materialização de backlog antigo.
2. **Custo da varredura** dos reconciliadores é O(n) por ciclo. Correto no modo pessoal; no modo
   servidor com muitos projetos pede filtro por status ou cursor persistente. É otimização, não
   correção — e o bloco proíbe alargar escopo.
3. **O interior de um CLI externo** continua fora do broker (§0B2). A mitigação é a sandbox
   atestada, e ela depende de o dono ter contêiner disponível.
4. **O modo pessoal sem Docker** agora exige aceite de risco de 24h para executar agentes. É a
   intenção do BR-002, mas muda a operação do dia a dia e merece decisão consciente do proprietário.
5. **Um único Host escritor no SQLite** não foi implementado como lock de processo. A coordenação de
   merge é garantida pelo banco (§0C1), que cobre o caso crítico; a rejeição de uma segunda
   instância de Host inteira fica para quando o modo servidor for exercitado de fato.

---

## 6. O que NÃO foi feito, por decisão

Nada da lista de itens proibidos do documento foi implementado: sem memória episódica, sem pgvector,
sem GraphRAG, sem MCP runtime, sem consenso, sem novos agentes, sem redesign, sem EffortPolicy no
caminho principal, sem scheduler multiprojeto, sem workflow durável da Fase 1.

O advisory `GHSA-qwww-vcr4-c8h2` do `react-router` foi avaliado e **não é aplicável** — é falha do
modo RSC, e o frontend é SPA com `createBrowserRouter`, sem qualquer runtime RSC. A única correção
publicada é o major 8.3.0, que a Fase 0 proíbe. Nenhuma dependência foi alterada. A análise, com
reprodução, está em `docs/architecture/audits/bruna/ADVISORY-REACT-ROUTER-GHSA-qwww-vcr4-c8h2.md`.

---

## 7. Continuação: 0-E e Fase 1 (31/07/2026)

### 0-E — Docker obrigatório (`a511c115`)

Decisão do proprietário: o contêiner é pré-requisito nos DOIS modos. O aceite de modo inseguro —
criado no 0B1 como válvula "até configurar uma sandbox" — foi **extinto do código e dos dados**.

Removido: `UnsafeModeAccepted` do contexto de política; o método do serviço de attestation; o uso no
orquestrador; os endpoints; os métodos dos dois stores. E também o aceite ANTERIOR, por perfil
(`settings.UnsafeModeAcceptedAt`), que o `RunTargetEndpoints` usava para liberar execução local.

**Por que a migration 0116 apaga o dado, e não só para de escrever:** enquanto a linha existir, um
upgrade futuro pode voltar a lê-la e reabrir o caminho em silêncio. Uma exceção "temporária" que o
produto aceita vira permanente na prática.

A sonda de runtime checa o **Server** do Docker, não o cliente — o cliente responde mesmo com o
daemon parado, e é o servidor que cria o contêiner. Launcher falha antes de subir o Host; `doctor`
reporta o runtime antes das contas, porque conta saudável sem contêiner não executa nada.

### Auditoria retroativa (0A3, 0B1, 0B2, 0C)

**PASS nos quatro, zero correções exigidas.** Parecer em
`docs/architecture/audits/bruna/pareceres/PARECER-RETROATIVO-0A3-0B1-0B2-0C-ANTIGRAVITY.md`.

### 1A — Retomada real por checkpoint (`10edb30d`)

`ExecutionCheckpointService` existia completo — captura, política, consumo, store nos dois providers
— e **não tinha um único chamador**. Mesmo padrão do `ListAsync` das notas no 0A2: capacidade
construída, documentada e desligada. Toda queda voltava à estaca zero com a branch cheia de trabalho
aproveitável, e o agente seguinte refazia ou desfazia o que o anterior deixou.

Captura na falha do executor e na recuperação de órfã pelo Host — este último é o caminho da queda
de energia e de rede. Retomada na montagem do prompt, ao lado da continuação por patch: os dois
lados da continuidade deixaram de ser mecanismos separados.

**O que se recupera não é a sessão do agente** — ela morreu com o processo. É o que ele deixou:
branch, arquivos alterados, onde parou, o que faltava. Sem checkpoint, a tentativa começa do zero
explicitamente, o que é preferível a afirmar uma continuidade inexistente.

### 1B — Orçamento governando o despacho

`EffortPolicy` estava pronta, determinística, testada — e **não governava nada**. Terceira vez que
o mesmo padrão aparece nesta auditoria (depois do `ListAsync` das notas no 0A2 e do
`ExecutionCheckpointService` no 1A): capacidade construída com cuidado e nunca ligada.

O orçamento nasce no planner, é **copiado** no plano (recalcular no despacho deixaria o card
sujeito a mudanças de política feitas depois, e o plano deixaria de ser reproduzível) e governa: card
que esgota as rodadas orçadas **escala com evento auditado**, em vez de ser redespachado em silêncio.

**Assimetria de modelo:** a prioridade da conta expressa capacidade e custo. Preferir sempre a mais
capaz gasta o modelo caro em troca de rótulo; preferir sempre a mais barata entrega revisão de
mudança estrutural a quem tem menos condição de julgá-la. Quem decide é o TRABALHO — revisão e risco
alto vão para a mais capaz, execução comum para a mais barata que atenda. A inversão altera apenas a
ORDEM de preferência: nenhuma conta inapta é promovida por ser barata.

### 1C — Veredito composto nomeia a camada

As três camadas e a regra de não-compensação já existiam. Faltava o parecer **dizer qual camada
reprovou**: "reprovado" sozinho não informa a quem corrige, e build quebrado, critério de aceite não
atendido e objetivo não cumprido exigem ações completamente diferentes.

### 1D — Aprendizado consumido

A taxonomia MAST classificava desde a F16 e morria como telemetria. O ponto dela nunca foi medir — é
que **cada categoria pede uma correção diferente**, e aplicar a errada é pior que não corrigir:
especificação recorrente fatia menor e exige critério explícito (revisar mais fundo não conserta
enunciado ambíguo — o revisor reprova de novo pelo mesmo motivo e o custo dobra); desalinhamento
fatia menor (profundidade de revisão não arbitra conflito entre agentes); verificação — e só ela —
revisa mais fundo.

Uma ocorrência isolada é acidente, não padrão. A decisão entra no planejamento **auditada com a
evidência**: uma decisão de máquina que não pode ser citada é indistinguível de capricho.

### 1E — Multi-tenant real

O documento apontava `ChiefBacklogLoopService:230`. Havia **dois**: o mesmo `[0]` estava também no
`AttemptRecoveryBackgroundService` — e ali era pior, porque órfã presa mantém claim de path viva e
trava o escopo do card para sempre. Com dois perfis, o segundo dono nunca era atendido, e nada no
produto dizia isso: o laço não falhava, trabalhava para um só em silêncio.

Ambos passaram a iterar todos os tenants com isolamento **por iteração**. E
`ChiefContextStrategyEnabled` passou a ligada por padrão: nasceu desligada como default seguro e o
"temporário" durou até ela estar completa, testada e sem governar nada. Depois do 0A2, mantê-la
desligada seria preservar o defeito.

## 8. Fechamento das pendências da Fase 1

As quatro pendências declaradas acima foram resolvidas — três implementadas, uma recusada com
motivo. Cada bloco fechou com `verify.sh` verde.

### 1E item (3) — autonomia pelo modo do projeto — `1abf58d3`

O laço tinha um interruptor global de auto-dispatch, e ele nascia **desligado**. O dono escolhia
entre automatizar tudo ou nada, e numa instalação limpa um projeto declarado autônomo não andava
até alguém descobrir o toggle.

O projeto passa a nascer `autonomous` e o laço pula os que estão em `manual`. `AutoDispatchEnabled`
vira `true` e assume o papel que sempre deveria ter tido: **kill switch do operador**, não a
decisão de autonomia. O `AutonomousActionGuard` fica intocado — ele decide o que a autonomia pode
fazer depois de permitida; isto decide apenas se ela está.

Dois achados que o documento não previa:

* **Duas fontes para o mesmo modo, divergindo.** A esteira de fases lia
  `workflow_bindings.operation_mode` (o que a tela altera, com aceite de risco registrado) e o
  campo `projects.operation_mode` só era escrito na criação — nenhum caminho o atualizava depois.
  Quem lesse o campo veria `autonomous` para sempre, mesmo com o projeto já posto em manual pelo
  dono. Um `ProjectOperationModeResolver` passa a ser a fonte única: o vínculo manda; o campo do
  projeto só responde enquanto não existe vínculo.
* **O vínculo de workflow forçava `manual`.** As versões canônicas publicadas pelo seeder não
  declaram modo, e o linker caía num `?? "manual"` — então **todo** projeto nascia vinculado em
  manual, inclusive um criado como autônomo. O vínculo passa a herdar o modo do projeto.

Falha de leitura do modo não vira autonomia presumida: o projeto fica de fora do ciclo, com log
próprio, e o próximo ciclo tenta de novo.

### 1D item (3) — skills promovidas nos bundles — `e6f6309b`

Quinta ocorrência do mesmo padrão desta auditoria: capacidade completa, testada, auditada — e sem
nenhum leitor. O ciclo de aprendizado ia inteiro até `Promoted` e parava ali. Nada do que a fábrica
aprendia voltava para quem executa; o mesmo erro podia ser corrigido, virar skill aprovada e ser
cometido de novo na tarefa seguinte.

`ContextSkillSlice` entra no Context Builder com citação auditável, e o escopo vem do
`AllowedScopes` da persona que originou a skill (B8/F17) — casamento por **segmento** de caminho,
para que `src/api` não arraste `src/apiary` junto. Skill cujo texto contenha o que parece um
segredo é descartada inteira, não redigida: ela vai direto para o prompt de outro agente, e
instrução pela metade é pior do que instrução nenhuma.

Não há fallback pelo papel da persona. Cheguei a escrevê-lo e ele era um caminho morto:
`AgentRoles.PathScopesFor` conhece papéis de **conta**, e o papel de uma definição
(`chief`/`specialist`) devolveria lista vazia sempre — um filtro que parece existir e nunca filtra.

### 1D item (2) — produtividade por assinatura no modo Técnico

A tela existe agora em `/reliability` (menu do modo Técnico), e o endpoint que a alimenta teve de
ser corrigido antes: `pass@k` percorria **apenas os cards com classificação MAST**, isto é, apenas
aqueles em que a equipe já havia falhado. Um projeto que executou bem aparecia com capacidade
vazia, e um com poucas falhas exibia uma taxa que não era a do projeto. Chamar isso de
"produtividade" mente para quem decide com base nela.

`GetProjectInvocationsAsync` (SQLite e Postgres, paridade semântica) passa a alimentar a medida com
o histórico do projeto inteiro, e a resposta ganhou `subscriptions` — execuções, cards tocados,
acerto, tokens e custo por assinatura, porque `pass@k` sozinho esconde consumo: uma conta pode
acertar muito e gastar desproporcionadamente. Quando a amostra é recortada, a tela **diz**: um
recorte silencioso se lê como "é tudo o que existe".

`reliability` foi declarado namespace técnico no gate de vocabulário, e os dois rótulos de menu
entraram nas isenções com motivo — o gate continua varrendo o restante.

### 1E item (4) — transporte do judge e factory do Kimi — **recusado**

Não implementei, e a recusa é deliberada. O documento do dono lista `LLM-as-a-judge` entre as
proibições explícitas desta fase, e restringe o escopo do Kimi ao advisory de frontend
(`GHSA-qwww-vcr4-c8h2`), fora do core. Implementar o adapter seria contrariar as duas coisas ao
mesmo tempo — e um executor sem adapter hoje falha de forma explícita
(`executor.adapter_not_implemented`), que é o comportamento correto enquanto ele não existe.

Fica registrado como decisão, não como esquecimento: quando o dono liberar o judge, o item volta.
