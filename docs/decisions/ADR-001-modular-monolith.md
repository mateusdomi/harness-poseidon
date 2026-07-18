# ADR-001 — Monólito modular e Runner separado

- Status: aceito
- Data: 2026-07-18

## Decisão

O control plane será um monólito modular ASP.NET Core. `Harness.Runner` e `Harness.Launcher` são processos separados. Cada módulo mantém `Domain`, `Application`, `Infrastructure` e `Contracts` no mesmo projeto; módulos dependem apenas do SharedKernel e de contratos autorizados.

## Consequências

Transações e operação local permanecem simples, enquanto limites são verificados automaticamente. Microsserviços ficam fora deste ciclo.
