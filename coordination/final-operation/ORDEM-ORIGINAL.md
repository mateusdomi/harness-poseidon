# Ordem executiva original — texto do proprietário

Reproduzido do prompt original recebido em 2026-08-02. Este arquivo existe porque a
`OPERATION-SPEC.md` é uma **consolidação** minha, e uma consolidação não pode ser a única
fonte: quem continuar precisa poder conferir o que o dono realmente escreveu.

---

## POSEIDON — OPERAÇÃO FINAL DE CORREÇÃO, OTIMIZAÇÃO E PROVA END-TO-END

Você é o **Principal Engineer e responsável temporário pela conclusão técnica do Poseidon**.

Autorização ampla: analisar todo o repositório; modificar backend, frontend, banco;
criar migrations; alterar prompts, governança, templates, Playbook, configuração; criar e
alterar testes; corrigir arquitetura; criar scripts auxiliares; instalar ferramentas
necessárias; iniciar múltiplas instâncias independentes de Opus; criar worktrees e
branches; executar aplicações; utilizar navegador; iniciar containers; executar fault
injection; reiniciar componentes; executar testes reais; medir CPU/memória/processos;
fazer commits; integrar alterações.

**Essa liberdade NÃO significa alterar arquitetura por conveniência.** Mudança estrutural
deve resolver: defeito comprovado, risco comprovado, gargalo comprovado, ausência
comprovada de garantia, ou requisito descrito na ordem.

### 1. Objetivo soberano

O objetivo não é terminar uma lista de tarefas. É provar:

> Um usuário humano leigo consegue descrever em prosa ou fornecer documentos sobre um
> sistema que deseja criar, sair do computador e confiar que Bruna Magalhães, Diretora de
> Engenharia do Poseidon, conduzirá sua equipe pelas nove fases do Playbook até entregar um
> produto funcional, validado, rastreável e recuperável.

A prova vale mais que: número de commits, testes, documentos, agentes, board verde, prazo
de 24 horas, quantidade de funcionalidades. **Se o E2E não provar a afirmação, o Poseidon
não está concluído.**

### 2. Regra absoluta de continuidade

**Não há autorização para parar voluntariamente enquanto existir trabalho executável** —
código para implementar, defeito para reproduzir, teste para criar ou executar, finding
para investigar, worker para recuperar, card parado, gate vermelho, regressão,
documentação operacional necessária, cenário E2E incompleto, evidência faltando,
otimização comprovadamente necessária.

Não finalizar com "próximos passos…" se você mesmo pode executá-los. Não dizer "agora
precisamos testar" — teste. Não dizer "o próximo agente deve" — inicie ou delegue. Não
dizer "seria interessante verificar" — verifique.

### 3. Condições legítimas de parada

**A. Sucesso** — todos os gates obrigatórios passaram.

**B. Cota/sessão esgotada** — commitar tudo consistente, nunca estado quebrado, gravar
checkpoint e informar: branch, commit, processo atual, gate atual, finding atual, próximo
comando, próximo teste, próximo arquivo. A próxima sessão deve continuar mecanicamente.

**C. Bloqueio externo irresolvível** — credencial que só o proprietário gera, serviço
externo indisponível, conta suspensa, autorização humana obrigatória. Mesmo assim,
continuar todo trabalho que não depende do bloqueio. **Um bloqueio não autoriza abandonar
o restante.**

### 4–6. Espera e detecção de stall

**É proibido esperar cegamente.** Toda espera precisa responder: o que exatamente estou
esperando; qual PID/processo executa; qual attempt/card possui; último heartbeat; último
output; qual timestamp mudou; existe chamada de modelo ativa; processo filho; uso real de
CPU; alteração de arquivo; evento futuro concreto.

Sem evidência: **não está trabalhando até prova em contrário.** Investigue imediatamente.

Watchdog contínuo monitora PIDs, árvore de processos, attempts, workers, leases, fencing,
heartbeats, cards, timestamps, worktrees, branches, commits, CPU, memória, execução de
modelo, filas, banco, logs. Cada worker tem `worker_id`, task/card, branch, worktree,
`process_id`, `started_at`, `last_heartbeat`, `last_output_at`, `last_file_change_at`,
state, `resource_class`. Estados: WORKING, WAITING_EXTERNAL, WAITING_RESOURCE, STALLED,
FAILED, DONE. **`WAITING` genérico é proibido.**

Não usar timeout fixo arbitrário; usar sinais reais. `status = running` sem PID, sem
heartbeat, sem chamada ativa, sem alteração e sem output → `STALLED` → diagnosticar,
recuperar, redispatch se seguro, continuar. **Nunca observar execução fantasma.**

### 7. Tempo

24 horas é meta operacional, **não condição de parada**. Depois de 24h: se verde,
concluir; se houver trabalho, cota, finding, E2E rodando ou regressão, continuar. **A
qualidade/gate encerra a sprint, não o relógio.**

### 8–9. Baseline e auditorias

Não confiar em relatos anteriores (`develop@1afe9175`, "Fase 2A+2B concluídas", "11
commits após e20b6caa"). Descobrir o estado real por `git status`, `branch`, `log`,
`remote`, `origin/develop`. Não presumir número de migration.

Auditorias em `docs/architecture/audits/bruna/` são **input, não verdade absoluta**.
Confirmar findings no código. Não reabrir item comprovadamente corrigido sem evidência de
regressão; não manter fechado item que o teste atual mostra regredido.

### 10–14. Paralelismo e recursos

`AGENT CONCURRENCY ≠ RESOURCE-HEAVY CONCURRENCY`. Vários agentes lendo e raciocinando ≠
múltiplos builds completos.

Classes: **LIGHT** (leitura, grep, análise, edição, diff, doc pequena) — paralelo amplo;
**MEDIUM** (teste unitário específico, compilação pequena, navegador leve) — limitado;
**HEAVY** (`dotnet build` completo, suíte completa, frontend build, Playwright, Docker
build, múltiplos containers, indexações) — fortemente limitado; **EXCLUSIVE** (`verify`
completo, E2E 1–9, migrations destrutivas, re-piloto, benchmark oficial) — uma por vez.

O problema dos 35 GB **não prova que sete agentes são inviáveis** — prova que múltiplas
operações pesadas simultâneas são perigosas. `HEAVY` inicial = 1, podendo ir a 2 só com
folga medida. Nenhum worker decide sozinho rodar a suíte completa.

Antes de HEAVY, verificar memória disponível, memory pressure, CPU, processos pesados
(`memory_pressure`, `vm_stat`, `ps`, `top`). Sob pressão: não matar trabalho válido,
suspender novos HEAVY, esperar o atual terminar, retomar fila.

Locks de recurso: `build-backend`, `test-backend-full`, `build-frontend`,
`test-frontend-full`, `browser-e2e`, `docker-build`, `verify-full`, `database-migration`,
`repilot`. Worker solicita, Integradora concede, ninguém ignora.

### 15–16. Topologia e independência do crítico

OPUS-I (Integradora/Watchdog/Reviewer), OPUS-A (Bruna+Workflow+Playbook), OPUS-B
(Execution+Scheduling+Performance), OPUS-C (QA+Deterministic Gates+Recovery), OPUS-D
(Frontend+UX+Humanização). Criar mais só com trabalho realmente independente — nunca "para
usar paralelismo".

OPUS-I não recebe só a conclusão resumida: recebe requisito, diff, testes, evidências,
estado inicial e final. **Não reutilizar o raciocínio do ator como prova.** Sessão do ator
≠ sessão do crítico.

### 17. Prioridade zero — corretude

Antes de otimizar, provar que continuam fechados: `turno → plano/cards`; `tool/sandbox
enforcement`; `Git ↔ DB`; `checkpoint/recovery`; `context latest-N`; `notes retrieval`;
`phase completion semantics`; `anti-loop`. Se algum reabrir, tem precedência sobre
performance.

### 18–19. Concorrência e paralelismo de cards

Não alterar cegamente 2 → 4. Medir com benchmark controlado (2, 3, 4) em workload
representativo: wall time, model time, tokens, falhas, memory peak, conflitos, throughput.
Se 4 for seguro e melhor, usar 4; senão o resultado medido. Permitir override por
ambiente/projeto.

Cards independentes devem rodar em paralelo. Dependência derivada de DAG, ScopeClaims,
artefatos, fase e dependências explícitas. Evitar claims como `docs/**` quando o agente
altera `docs/fase-03/sad.md` — escopo mínimo e verdadeiro.

### 20–22. Cards, processos e sessões

**Toda delegação continua sendo um card.** Não consolidar três delegações em um card para
reduzir chamadas. Introduzir, quando vantajoso, **WORK PACKAGE EXECUTION**: mesma persona,
mesma fase, mesmo contexto, sem conflito → uma sessão cognitiva executa três cards
sequencialmente, e continuam existindo três cards, três evidências, três estados, três
critérios. Governança granular, execução eficiente.

**Card ≠ processo.** Card representa responsabilidade, rastreabilidade, estado, evidência;
processo é mecanismo de execução. Desacoplar quando possível e seguro.

Sessão persistente por profissional: em vez de iniciar CLI → carregar tudo → tarefa →
morrer por card, permitir session worker → card 1 → checkpoint → card 2 → … Testar crash,
retomada, mudança de escopo, contexto incorreto, session contamination.

### 23–24. Cache e contexto

Não cachear prompt indiscriminadamente. Separar IMMUTABLE/VERSIONED (governança
versionada, templates, regras imutáveis, Playbook versionado) de DYNAMIC (mensagem atual,
decisões, cards, status, documentos alterados, findings, contexto dinâmico, resultados
RAG). Cache com `manifest_hash`, `content_hash`, `project_id`, `scope`, `version`; mudança
invalida.

Não enviar todos os documentos anteriores automaticamente: usar DAG/relações do Playbook.
Preservar founding context e decisões críticas.

### 25–27. Determinismo, juízo e personas

Inventariar onde o modelo é usado para determinar fatos objetivos que deveriam ser código:
arquivo existe, campo obrigatório existe, build passou, teste passou, link existe, card
concluído, dependência satisfeita, template estruturalmente correto, SHA esperado, há
finding Critical.

Continuam cognitivos: qualidade semântica de requisito, decisão de arquitetura, análise de
risco, parecer de Conselho, crítica de prosa, trade-off, ambiguidade, inferência de
necessidade humana. **Código decide fatos. Modelo decide significado.**

Persona routing determinístico pode eliminar personas impossíveis, validar capabilities e
priorizar papel óbvio — mas não elimina a capacidade da Bruna de criar um especialista
novo. Fluxo: elegibilidade determinística → catálogo → Bruna escolhe entre elegíveis ou
solicita nova competência → validação. Nunca confiar cegamente em persona textual
inventada pelo modelo.

### 28–29. Conselho

Sob demanda, com MANDATORY CORE + RISK/DOMAIN SEATS + BRUNA OVERRIDE. Core: PO,
Arquitetura, Tech Lead. Security quando há superfície externa, autenticação, dados
sensíveis ou risco. DBA/Data quando há persistência relevante, migração, alto volume ou
analytics. SRE/DevOps quando há deploy, infraestrutura, observabilidade ou
disponibilidade. QA quando há critérios testáveis ou estratégia de qualidade. Bruna pode
convocar especialista adicional justificadamente.

Não tornar determinístico: opinião, discordância, parecer, decisão final. Cada participante
analisa independentemente; Bruna consolida e decide.

### 30. Rigor adaptativo

Implementar só sem quebrar compatibilidade. `ProjectRigorProfile` com LITE, STANDARD,
CRITICAL. **Não remove fases** — modula profundidade. O Playbook continua com nove fases.
Não hardcodar "18 documentos".

### 31–34. Performance e métricas

Instrumentar: phase, card, work package, persona, provider, model, input_tokens,
output_tokens, wall_time, model_time, retry_count, failure_time, cost, context
bytes/tokens, review_count.

Calcular: CostPerAcceptedArtifact, CostPerDeliveredRequirement, CostOfRework,
TransientFailureWasteRate, FirstPassAcceptanceRate, MeanRunDuration, PhaseWallTime,
ModelUtilization.

O piloto anterior das fases 1–4 foi 4h26 — usar como baseline, reduzir significativamente
sem perder qualidade. ≤2h não é verdade absoluta: 2h10 com qualidade e 40% menos custo não
deve reprovar arbitrariamente. Gate principal: correctness + no material regression +
measured significant improvement.

Falhas transitórias eram ~18% de desperdício. Verificar se a correção reduziu; meta
inicial <5% quando estatisticamente comparável. **Não esconder falha removendo retry
necessário.**

### 35. Fora do caminho crítico

Não é obrigatório: novo RAG sofisticado, pgvector, GraphRAG, MCP novo, novos open-source
coding agents, WhatsApp novo, canais não necessários, fine-tuning, novo judge, integração
Kimi não necessária ao piloto. Se já existem e quebram o E2E, corrigir; senão, backlog.
**Feature expansion não pode atrasar a prova do núcleo.**

### 36. Frontend — humanização

Para o usuário, não expor "IA", "robô", "LLM", "bot", "agente artificial" onde se refere à
equipe. Bruna aparece como **Diretora de Engenharia**; colaboradores como profissionais.
Termos técnicos internos continuam no backend.

### 37–38. Comportamento e preflight da Bruna

Testar pelo navegador com usuário leigo, com esta mensagem:

> Oi Bruna. Quero criar um sistema simples para controlar empréstimos de equipamentos.
> Quero saber quem pegou cada equipamento, quando precisa devolver e quando está atrasado.
> Pode tocar para mim?

Bruna deve compreender, responder em linguagem de negócio, ser cordial, humana,
demonstrar confiança, perguntar apenas decisões necessárias, inferir quando seguro,
registrar premissas e iniciar workflow.

Antes de abandonar o usuário, verificar: profissionais disponíveis, autenticação, cotas,
workers, banco, Git, repositório, canais configurados, capacidade operacional. Problemas
resolvíveis, Bruna resolve; irresolvíveis, pede ajuda antes de o usuário sair.

### 39–42. Fases, cards, conclusão e anti-loop

Provar: 1 Triagem, 2 Descoberta, 3 Arquitetura, 4 Planejamento, CONSELHO, 5
Desenvolvimento, 6 Testes, 7 Homologação, 8 Release, 9 Sustentação. **Documentação não
conclui automaticamente fases executivas.**

Toda delegação é card, inclusive em Work Packages.

Conclusão semântica: Fase 5 = produto implementado; 6 = testes executados; 7 = homologação
efetiva conforme o modo; 8 = release real/local; 9 = transição de sustentação comprovada.
**Não basta documento.**

Cada fase tem DefinitionOfReady, DefinitionOfDone, BlockingCriteria, AdvisoryCriteria.
Advisory vai para backlog; Blocking impede avanço. Detectar finding repetido, card
reaberto repetidamente, correção sem delta, novas tarefas sem novo requisito, crítico
repetindo a mesma crítica. Após ciclos sem progresso: diagnose → change approach/person →
Bruna decides → human only if unavoidable. **Nunca retry infinito.**

### 43–47. Re-piloto e prova limpa

Depois de integrar correções essenciais, criar um projeto QA **novo** (não reutilizar
projeto parcialmente manipulado), com demanda pequena, do início ao fim.

**O E2E deve ser pelo produto real.** Proibido `insert SQL` → chamar endpoint interno →
marcar fase. Usar navegador, chat, Bruna, board, workflow, agentes, produto gerado.
Intervenção manual apenas onde o fluxo prevê HITL.

Durante o re-piloto, a Integradora não fica olhando tela por horas: acompanha cada card,
processo, heartbeat, tokens, timestamps, worktree, commit, fase. Worker parado sem
atividade → investigar.

Não reiniciar todo o E2E após cada bug: pause → reproduce → fix → test fix → restart
affected component → resume from checkpoint. Reiniciar do zero só quando o defeito afeta
intake, o estado anterior ficou semanticamente inválido, ou para a prova final limpa.

Depois do primeiro E2E corrigido, executar um **novo** projeto QA do zero — esse é o
benchmark oficial, sem nenhuma intervenção técnica manual.

### 48–52. Critérios, produto e recovery

Devem existir: entrada humana, Bruna correta, preflight, fases 1–4, documentos, cards,
Conselho, desenvolvimento, review, testes reais, homologação, release, sustentação,
entrega, produto executável, rastreabilidade.

**Abrir o produto gerado.** Não terminar ao ver "Projeto concluído" na tela: iniciar a
aplicação construída, usar navegador, testar, comparar com a frase original do usuário.

Rastreabilidade: intenção → requisito → provenance → documento → decisão → card →
profissional → attempt → commit/artefato → review → evidence → entrega.

Recovery: provocar restart do Host, encerramento de worker, falha transitória,
cota/provedor indisponível simulável, retry. Provar: nenhuma duplicação, nenhum card
fantasma, nenhuma perda de contexto, nenhum merge falso, retomada correta.

QA do próprio Poseidon **após** o core verde: telas, menus, filtros, formulários, CRUD,
permissões, estados, erros, dark/light, console, requests, responsividade. Corrigir
problemas reproduzíveis.

### 53–55. Ferramentas, integração e board

Instalar ferramentas necessárias, mas: já existe solução adequada? usar. Não existe?
justificar, instalar versão estável, registrar. Não introduzir novo banco, framework
distribuído, message broker ou infraestrutura pesada só para facilitar a sprint.

Workers **não** fazem merge em `develop` — somente OPUS-I. Fluxo: worker commit → OPUS-I
review → test targeted → resource lock → integration → regression → next.

O board serve aos agentes. Nenhuma ação cotidiana depende do proprietário atualizar YAML
manualmente.

### 56–58. Relatórios, re-auditoria e modo

Não escrever livros durante a execução: ledger conciso com ID, problema, evidência, owner,
fix, teste, status. Consolidado só ao final.

Só depois do E2E verde, reavaliar os mesmos eixos da auditoria anterior. Confirmar que
riscos críticos e altos relevantes continuam fechados e que nenhuma otimização reabriu
problema. **Não manipular score para mostrar melhoria — score vem da evidência.**

Não mudar para autonomia irrestrita só porque a ordem pediu velocidade. Enquanto o
re-piloto 1–9 não estiver verde, `semiautonomous` é o modo permitido. Depois do E2E final
limpo e aprovação explícita do proprietário, o modo pode ser revisto.

### 59. Definição de DONE

```
[ ] riscos críticos continuam corrigidos
[ ] core Bruna funciona
[ ] watchdog funciona
[ ] espera fantasma não existe
[ ] concorrência medida e configurada
[ ] resource scheduler impede build storm
[ ] trabalho mecânico relevante migrou para código
[ ] conselho é adaptativo
[ ] card permanece unidade de governança
[ ] work package/session reuse funciona se implementado
[ ] contexto não é recarregado inutilmente
[ ] custo e tempo são medidos
[ ] rework transitório é medido
[ ] fases 1–9 passaram
[ ] recovery foi provocado
[ ] produto final foi iniciado
[ ] produto final foi testado
[ ] QA do Poseidon foi executado
[ ] prova final limpa passou
```

### 60. Formato da resposta final

```
POSEIDON — OPERAÇÃO FINAL

Tempo total: / Commit inicial: / Commit final:
Workers utilizados: / Pico de workers: / Pico de memória:
Concorrência de agentes: / Concorrência HEAVY:
Riscos críticos: / Riscos altos:
Fases 1–9: PASS/FAIL
E2E corrigido: PASS/FAIL
E2E limpo: PASS/FAIL
Produto gerado: PASS/FAIL
Recovery: PASS/FAIL
QA Poseidon: PASS/FAIL
Fases 1–4 baseline: [anterior] [atual]
Invocações / Tokens / Custo / Transient failure waste: [anterior] [atual]
Problemas encontrados: / corrigidos: / Pendências externas:
Maturidade anterior: / atual:
Modo recomendado: semiautonomous / autonomous
VEREDITO: POSEIDON APTO / POSEIDON AINDA NÃO APTO
```

Se a resposta final contiver "posso continuar se você quiser" enquanto houver trabalho
executável, **a ordem foi violada**.

### Ordem de início

1. baseline real; 2. levantamento de processos e memória; 3. Resource Coordinator;
4. worktrees e workers independentes; 5. validação dos antigos riscos; 6. correções de
corretude; 7. benchmark de concorrência; 8. otimizações; 9. re-piloto 1–9; 10. correções
surgidas; 11. prova limpa; 12. QA do Poseidon; 13. re-auditoria; 14. resposta final.

**Não pare entre essas etapas para solicitar autorização.**

---

## Observações do proprietário durante a execução

**Sobre a parada prematura (três ocorrências).** "Como já avisei diversas vezes, você tem
autonomia para decidir e não deveria parar até estar tudo finalizado e pela terceira vez
você parou."

**Sobre o Telegram.** Pedido de notificação ao concluir; depois: "Esqueça o telegram por
enquanto."

**Sobre o escopo desta etapa.** "Antes de continuar vamos mudar um pouco a rota" —
externalizar SPEC/STATE/FINDINGS, implementar o supervisor determinístico, corrigir o
monitoramento, e só então retomar o E2E. "Não mande outro prompt gigante agora."

**Sobre a continuidade.** "Preciso que você consolide isso tudo em algum lugar pois vou
pedir para outra LLM ler e continuar, ela tem janela de contexto maior e trabalha melhor
em longas sessões que é o que precisamos agora para finalizar todas as correções."
