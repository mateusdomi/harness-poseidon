# ADR-0004 — Maturidade honesta das fases 6–9 do playbook

## Contexto

O playbook padrão do Poseidon tem nove fases:

1. Triagem
2. Descoberta
3. Arquitetura
4. Planejamento
5. Desenvolvimento
6. Testes
7. Homologação
8. Release
9. Sustentação

Até a auditoria macro de 2026-08-03, as fases 1–3 foram executadas de ponta a
ponta em um projeto de validação; a fase 4 ficou travada no Conselho de Agentes;
as fases 5–9 nunca foram executadas end-to-end.

A auditoria classificou isso como `F-16` — risco arquitetural alto — não porque
as fases estejam ausentes ou mal implementadas, mas porque a documentação e as
apresentações corriam o risco de sugerir que a "fábrica" testa, homologa e faz
release de forma autônoma, quando, na prática, as fases 6–9 ainda dependem de
documentos e de gates humanos.

## Decisão

**As fases 6–9 são, hoje, fases documentais com gate humano. Não serão
apresentadas como execução autônoma de release até que exista evidência de
execução medida.**

Tabela de maturidade real por fase:

| Fase | Gate factual | Estado de maturidade |
|---|---|---|
| 1–3 | Documentos produzidos, revisados e aprovados por agente distinto | **Executadas E2E** |
| 4 | Conselho de Agentes + `PhaseGateEvidence` | Implementada; **travada por configuração de críticos** na instalação auditada |
| 5 | Cards em `Merged` com integração verde (`CodeDiagnosticsGate`, review independente, merge) | Implementada e testada; **nunca executada sobre código de produto** |
| 6 | Quality gate verde, cobertura ≥ perfil, zero P0/P1, parecer Go | **Documental** — o gate existe, mas não executa suíte real de produto |
| 7 | UAT executado, Termo de Aceite aprovado pelo humano | **HITL obrigatório** — o documento é produzido, a aprovação é humana |
| 8 | GMUD aprovada, rollback testado, janela cumprida | **HITL obrigatório** — o plano é documentado, a decisão é humana |
| 9 | Revisão mensal de chamados, incidentes, SLOs | **Documental** — não há execução contínua medida de sustentação |

## Consequências

- **Honestidade de comunicação:** apresentações, propostas comerciais e
  documentos voltados a gestores devem refletir a tabela acima. Não usar
  expressões como "fábrica autônoma de release" ou "o Poseidon testa e homologa
  sozinho" enquanto as fases 6–9 não tiverem execução medida.
- **Marco técnico real:** o marco que transforma o Poseidon de "diretoria de
  engenharia que planeja" em "fábrica que constrói" é a **fase 5 produzindo
  código de produto pela primeira vez**, não a existência dos documentos das
  fases 6–9.
- **Caminho de evolução:** quando `CodeDiagnosticsGate`, execução de suíte,
  pentest, UAT automatizada, rollback medido e métricas de sustentação forem
  realmente exercitados por projetos, este ADR deve ser atualizado para
  refletir a nova maturidade.
- **Não é defeito:** a implementação das fases 6–9 como documentais com gate
  humano é adequada ao estágio atual do produto. O risco está em *vender* ou
  *reportar* um nível de autonomia que a medição ainda não comprova.

## Alternativas descartadas

- **Remover as fases 6–9 do playbook até ter execução.** Rejeitado porque o
  processo precisa delas como alvo e contrato, mesmo que hoje sejam majoritariamente
  documentais.
- **Automatizar artificialmente os gates (ex.: aceitar documento como evidência
  suficiente).** Rejeitado porque enfraqueceria o `PhaseGatePolicy`, que é
  Default-FAIL por design.

## Relacionado

- Achado `F-16` da auditoria macro de 2026-08-03.
- `docs/architecture/workflows/standard-workflow.md` — descrição do playbook de
  nove fases.
- `docs/decisions/ADR-0001-arquitetura-definitiva-v3.md` — arquitetura geral.
