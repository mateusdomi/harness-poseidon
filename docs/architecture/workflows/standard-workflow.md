# Workflow padrão de nove fases

Uma iniciativa percorre as fases macro abaixo. Cada transição exige card `gate`
aprovado, evidências válidas, condutor atual, próximo condutor, registro no ledger e
comunicação da Bruna. Gates são Default-FAIL.

## 1. Triagem

O `product-owner` qualifica valor, criticidade e viabilidade e registra a decisão
Build, Buy, Integrate, Reuse ou Reject. O gate exige valor explícito, criticidade,
caminho viável e o porquê. Reject termina em `Arquivada`; demanda operacional pode
ir a `Roteada-para-Sustentação`.

## 2. Descoberta

O `product-owner` conduz entrevistas, PRD, story map, histórias INVEST com Gherkin,
NFRs e riscos. O gate exige backlog refinável, NFRs medíveis, mitigação e aprovação
humana do PRD.

## 3. Arquitetura

`arquiteto`, `security` e `dba-dados` produzem SAD ideal e restrito, ADRs, C4, modelo
de dados, threat model, trade-offs e planos de testes e observabilidade. O gate
exige ADRs revisados, constraint profile atendido, OWASP 2025 coberto e DER
revisado.

## 4. Planejamento

`product-owner` e `tech-lead` refinam cards, DoR, estimativas, releases, riscos e o
grafo `provides` e `consumes`. O gate exige cards prontos, estimados, com risco e
dependências resolvidas ou mapeadas.

## 5. Desenvolvimento

`dev-executor`s trabalham em paralelo em worktrees isoladas; `tech-lead` revisa.
Cada card inclui briefing, código, testes e decisões emergentes. O gate por card
exige revisor distinto, testes verdes, scan de segredos e aderência arquitetural. O
gate da fase exige todos os cards da release em `Merged` e integração verde.

## 6. Testes

`qa` conduz testes funcionais e não funcionais com apoio de segurança e dados. O
gate exige quality gate verde, cobertura do constraint profile, zero P0 e P1
abertos e parecer Go.

## 7. Homologação

Usuários-chave executam UAT em ambiente espelho, apoiados por `product-owner` e
`qa`. O gate exige roteiro executado, zero P0 e P1 e Termo de Aceite aprovado pelo
humano.

## 8. Release

`devops` prepara mudança, notas, SBOM, runbook e rollback testado. O gate exige
aprovação humana da mudança e janela, rollback comprovado, saúde estável e
documentação atualizada.

## 9. Sustentação

`sre-sustentacao` mantém SLA, SLO, error budget, incidentes, runbooks, postmortems,
capacity planning, game days e DR drills. O gate contínuo mensal exige chamados no
SLA, causas tratadas e SLOs controlados.

## Papéis e HITL

Bruna é a única voz com o usuário e nunca executa. Especialidades: `product-owner`,
`arquiteto`, `tech-lead`, `qa`, `devops`, `sre-sustentacao`, `security`,
`dba-dados` e `dev-executor`. Revisor difere do implementador. Aceite UAT, mudança
de produção e risco residual dependem de humano.
