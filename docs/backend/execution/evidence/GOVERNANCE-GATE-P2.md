# Evidência — Governance Gate P2

Data: 2026-07-20. Gate P1 de entrada: aprovado pelo usuário.

O pipeline persistido cobre `observation → evidence → candidate → deduplication → review →
evaluation → shadow → approval → promotion → monitoring → rollback → deprecation`. Os sete tipos
iniciais possuem payload fechado e fingerprint SHA-256 determinístico. Nenhuma transição promove
automaticamente, a avaliação exige ator/provider/modelo independentes, shadow não troca a versão
normativa e rollback restaura a versão anterior.

SQLite e PostgreSQL implementam o mesmo contrato com tenant/organização/projeto, OCC, Inbox,
Outbox, ledger e histórico append-only. A API `/api/v1/governance-runtime/learning-candidates`
publica listagem/filtros/paginação, evidência, comparação, review, avaliação, shadow, decisão,
promoção, rollback, depreciação, histórico e métricas. O OpenAPI e o catálogo realtime 1.1 estão
protegidos pelo teste de drift. As quatro ações de domínio P2 trafegam pelo evento canônico
`audit.eventAppended`, estendendo a governança existente sem criar navegação ou canal paralelo.
Exemplos fechados de candidate, métricas e envelope realtime estão em
`docs/contracts/examples/governance-learning.json` e são lidos pelo contract test.

Evidência automatizada: testes unitários cobrem política/tipos; o cenário provider-neutral percorre
o lifecycle completo nos dois bancos e prova replay, dedupe, independência, RBAC, rollback,
histórico e métricas. O cenário HTTP usa Host e SQLite reais e percorre criação, review, avaliador
independente, shadow, decisão humana, promoção, métricas, rollback, depreciação e histórico. O gate
integral e os números finais são registrados no `TEST_REPORT.md` gerado pela Release Candidate.
