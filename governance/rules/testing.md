# Regra canônica — testes, gates e evidência

Owner: Quality Engineering. Versão: 1.0.0.

- Concluído significa executado com evidência. Arquivo criado, compilação
  presumida ou afirmação do agente não comprovam comportamento.
- Toda feature de produção inclui testes, documentação, evidência e flag de
  rollout/rollback quando exigida. Flags não dispensam completude.
- Use testes unitários para invariantes, integração para bordas/persistência,
  contratos para API/eventos, arquitetura para regras estruturais, recovery para
  retomada e concorrência para OCC/fencing. Persistência exige comportamento
  equivalente SQLite/PostgreSQL quando aplicável.
- Build usa nullable e warnings como erro. Gate termina com zero warnings, zero
  Docker Harness órfão e frontend protegido intacto.
- Critérios nascem Default-FAIL. Risco médio/alto exige evaluator independente;
  achado P0/P1 bloqueia até correção ou decisão humana autorizada.
- Falha repetida pela mesma hipótese exige instrumentação ou caso mínimo antes de
  nova edição. Evidências nunca contêm segredos.

Enforcement: `tools/backend/verify.sh`, release candidate, CI e runtime gates.
