# 04 — O Playbook de 9 fases e os gates

---

## 1. Onde o playbook vive

**Fonte da verdade:** `src/Harness.Host/Workflows/CanonicalWorkflowTemplates.cs`, método
`PlaybookStandard()` — **código C# compilado**, não YAML nem banco.

**Como chega ao runtime:**

```
CanonicalWorkflowTemplates.PlaybookStandard()
  → WorkflowTemplateSeeder (idempotente)
    → workflow_definitions / workflow_phase_definitions
      / workflow_objective_definitions / workflow_gate_definitions
        → ProjectWorkflowLinker liga o projeto ao template
          → workflow_runs / workflow_phase_runs / workflow_objective_runs / workflow_gate_runs
```

**Versionamento:** existe `workflow_definition_versions`. **Checksum de template: não encontrado.**
**Invalidação de cache:** o seeder é idempotente e roda no boot (`WorkflowTemplateSeedHostedService`);
a atualização acontece por nova versão, sem duplicação (o nome é estável de propósito).

**Como atualizar:** editar o C#, recompilar, reiniciar o Host. **Não há caminho de operador.**

**Existem outros 6 templates** além do playbook padrão (entrega padrão de 7 fases, greenfield,
bug, mudança, refactor, documentação) e um de 15 fases (`TechnicalDelivery`). O projeto de prova
usa o **playbook padrão de 9 fases**.

**TrensRJ:** varredura completa do repositório — **0 ocorrências**. Limpo.

---

## 2. As 9 fases, com documentos e gate

| # | Fase | Documentos obrigatórios | Gate (critério objetivo) |
|---|---|---|---|
| 1 | Triagem | Ficha de Demanda Qualificada | Valor de negócio explícito, criticidade, caminho técnico viável, decisão Build/Buy/Integrate/Reuse/Reject registrada com o porquê |
| 2 | Descoberta | PRD, Story Map, NFRs preliminares | Backlog refinável, histórias INVEST em Gherkin, NFRs medíveis, riscos com mitigação, **PRD aprovado pelo usuário** |
| 3 | Arquitetura | SAD Ideal e Restrito, ADRs, C4 (Contexto e Contêiner), DER, Comparativo de trade-off, Threat Model STRIDE, Plano de Observabilidade | ADRs aprovados por **revisor distinto**, NFRs medíveis, aderência ao constraint profile, threat model cobrindo OWASP:2025, DER revisado |
| 4 | Planejamento | DoR e DoD, Cronograma de releases, Mapa de riscos e dependências | Todo card com DoR cumprida, estimativa, dependências resolvidas ou mapeadas, `risk_tier` atribuído — **+ Conselho de Agentes** |
| 5 | Desenvolvimento | Briefing técnico, Code review estruturado, Métricas DORA, Dicionário ubíquo | **Todos os cards em Merged com integração verde**; por card: review por agente distinto, testes verdes, padrão respeitado, sem segredo, docs atualizados |
| 6 | Testes | Plano de Testes, Relatório de Quality Gate, Relatório de Performance, Relatório de Pentest, Parecer Go/No-Go | Quality gate verde, cobertura ≥ perfil, zero P0/P1 abertos, parecer Go |
| 7 | Homologação | Roteiro UAT, Resultados UAT, Defeitos UAT, Termo de Aceite | UAT executado, zero P0/P1, **Termo de Aceite aprovado pelo humano (HITL obrigatório)** |
| 8 | Release | GMUD, Notas de versão, Plano de rollback, SBOM, Runbook revisado | **Aprovação humana** da mudança e da janela, rollback testado, janela cumprida, métricas estáveis, docs atualizadas |
| 9 | Sustentação | Runbooks vivos, Postmortem blameless, Relatório mensal, Capacity planning | Revisão mensal: chamados no SLA, incidentes com causa raiz tratada, SLOs verdes, error budget controlado |

Transições são **lineares e explícitas** (`transitions`), exceto a fase 1 que pode ir para
`Arquivada` ou `Roteada-para-Sustentação`.

---

## 3. A tabela que a auditoria pediu

| Fase | Implementada | Testada | E2E | Situação |
|---|---|---|---|---|
| 1 Triagem | ✅ | ✅ | ✅ | **Completa** — 12,6 min, projeto de prova |
| 2 Descoberta | ✅ | ✅ | ✅ | **Completa** — 28,0 min |
| 3 Arquitetura | ✅ | ✅ | ✅ | **Completa** — 19 cards, fechada 16:16Z de 03/08 |
| 4 Planejamento | ✅ | ✅ | ⚠️ **parcial** | 3 documentos `validated`; **gate `pending`, travado no Conselho** |
| 5 Desenvolvimento | ✅ | ✅ | ❌ | `pending`. **Nunca executada. Nenhuma linha de código de produto foi escrita pela frota** |
| 6 Testes | ✅ | ✅ | ❌ | `pending` |
| 7 Homologação | ✅ | ✅ | ❌ | `pending` |
| 8 Release | ✅ | ✅ | ❌ | `pending` |
| 9 Sustentação | ✅ | ✅ | ❌ | `pending` |

Evidência (banco, `workflow_phase_runs` do projeto `01KZ24JCFRHN2RGP8NHGP75JMK`):

```
1  1-Triagem          completed  02/08 21:02 → 02/08 21:15
2  2-Descoberta       completed  02/08 21:15 → 02/08 21:43
3  3-Arquitetura      completed  02/08 21:43 → 03/08 16:16
4  4-Planejamento     active     03/08 16:16 → —
5..9                  pending
```

### O ponto crítico: "implementada" significa o quê nas fases 5–9?

Esta é a pergunta do §33 da ordem de auditoria, e a resposta é **mista**:

| Fase | Existe só documentação? | Existe execução factual exigida pelo gate? |
|---|---|---|
| 5 Desenvolvimento | Não — o gate exige `Merged` com integração verde | **SIM.** `ExecutivePhaseGuard` + `TaskIntegrationService` recusam merge de card não aprovado por revisão independente. `MergeReconciliationBackgroundService` existe |
| 6 Testes | Parcial | **Parcial.** `CodeDiagnosticsGate` existe; a exigência de "quality gate verde / cobertura ≥ perfil" depende do documento produzido, não de execução medida de suíte do produto |
| 7 Homologação | Sim, majoritariamente | Gate é **HITL** por construção; a "execução do roteiro UAT" é documento |
| 8 Release | Sim, majoritariamente | Gate é **HITL**; "rollback testado" é documento, não execução medida |
| 9 Sustentação | Sim | Documento |

**Conclusão honesta:** a fase 5 tem mecanismo real de execução e é a única das executivas com
portão factual forte. As fases 6–9 são, hoje, **fases documentais com gate humano**. Isso não é
defeito — é o estado de maturidade. Mas não deve ser apresentado como "a fábrica testa, homologa
e faz release sozinha".

Achado `F-16` — **HIGH, ARCHITECTURAL RISK**.

---

## 4. Os gates do produto, catalogados

| Gate | Onde | Determinístico? | Default-FAIL? | Evidência exigida | Override |
|---|---|---|---|---|---|
| **Política de comunicação** | `ChiefCommunicationPolicy` | ✅ | — | — | contexto do turno |
| **Contrato de saída da Bruna** | `ChiefTurnOutputContract` + parser | ✅ | ✅ | JSON válido | degrada para `unmatched` (não age) |
| **Intent gate** | `ChiefIntentGate` | ✅ | ✅ | intent classificado | não |
| **DoR (prontidão do card)** | `CardReadinessEvaluator` | ✅ | ✅ | tipo despachável + instrução + não bloqueado + dependências entregues | não |
| **Elegibilidade de conta** | `AgentAccountScheduler` | ✅ | ✅ | 10 filtros | não |
| **Escopo de path** | `AgentPathScopePolicy` | ✅ | ✅ | claim dentro do papel | não |
| **Ferramentas** | `SecurityPolicyEnforcementPoint` | ✅ | ✅ | allowlist da persona + atestação de contenção | `UncontainedExecutionAcknowledged` (declaração do operador, registrada em log) |
| **Contrato de template documental** | `ApprovedDocumentCatalogPublisher.ValidateTemplateContract` | ✅ | ✅ | exatamente 1 markdown + seções obrigatórias na ordem | não |
| **Crítico (review)** | `AgentRunOrchestrator` + `WorkChainAggregate` | ❌ LLM, mas **enquadrado** | ✅ | conta ≠ produtora (`IndependentReviewerRequired`) | não |
| **Diagnóstico de código** | `CodeDiagnosticsGate` | ✅ | ✅ | build/análise | não |
| **Merge** | `TaskIntegrationService` | ✅ | ✅ | `InternalState` aprovado pela revisão | não |
| **Gate de fase** | `PhaseGatePolicy` | ✅ | ✅ | `PhaseGateEvidence` medida | modo do projeto decide **quem** aprova |
| **Conselho** | `AgentCouncilPolicy.Consolidate` | ✅ (a consolidação) | ✅ | ≥3 pareceres, nenhum bloqueante | Bruna amplia a mesa, não a reduz |
| **Conclusão da operação** | `Harness.Modules.Operations.CompletionGate` | ✅ | ✅ | eixos objetivos | não |
| **Guarda de ação autônoma** | `AutonomousActionGuard` | ✅ | ✅ | — | modo do projeto |

---

## 5. Quem realmente declara DONE?

Pergunta do §32. Verificação:

| Nível | O modelo consegue declarar sozinho? | O que impede |
|---|---|---|
| **Card done** | **NÃO** | `WorkChainAggregate` exige uma `WorkReview` com `reviewer_agent_id ≠ producer`; `WorkChainErrors.IndependentReviewerRequired` recusa a mutação. O merge exige `InternalState` aprovado (`TaskIntegrationService.cs:96`) |
| **Fase done** | **NÃO** | `PhaseGatePolicy` lê `PhaseGateEvidence` — campos **medidos** do banco (`AllRequiredObligationsAccepted`, `HasBlockingFinding`, `HasOpenBlocker`, `RequiredObligationCount`), nunca texto do modelo |
| **Projeto done** | **NÃO** | `CompletionGate` (módulo Operations) avalia eixos objetivos; o supervisor força a saída da instância a ser sempre `YIELD` |

**Bypasses procurados e não encontrados** no caminho autônomo. Existem endpoints HTTP que
permitem operações manuais (`AgentRunEndpoints`, `WorkflowEndpoints`) — são a porta do **operador
humano**, não do modelo, e exigem perfil autenticado.

**Uma ressalva honesta:** o veredito de qualidade do crítico é probabilístico. O código garante
que *existe* um segundo par de olhos, de conta diferente, com ferramentas somente-leitura. Não
garante que esse segundo par de olhos seja bom. Isso é o limite honesto do desenho, e vale dizer
numa apresentação.

---

## 6. Regras de reabertura

- Card reprovado pelo crítico → instrução corretiva (`LogCorrectionPrepared`) e volta a `ready`,
  **usando a branch da tentativa reprovada como base** (preserva o delta que o crítico mandou corrigir).
- Achado bloqueante do gate documental vira **trabalho visível**, seguido de nova revisão independente
  (`WorkflowPhaseDriver.cs:770`).
- Card escalado → só volta por **replanejamento**; o predicado `WorkAttemptIsReplannable` decide
  se aquela tentativa pode ser reescrita (ver achado `F-12`).
- `MastCorrectionPolicy` e `PassAtKPolicy` medem retrabalho **por card**, não por tentativa.
