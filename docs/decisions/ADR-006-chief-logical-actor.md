# ADR-006 — Chefe como ator lógico persistido

- Status: aceito
- Data: 2026-07-18

## Decisão

Há um ator lógico por `(tenant, projeto)`, nunca processo permanente. Definição, estado, mailbox, plano, digest, lease e fencing são persistidos. Mensagens do projeto são serializadas; projetos distintos podem avançar em paralelo.

## Consequências

Cada turno segue o pipeline fixo da missão. Contexto é reconstituído de estado, Git e artefatos; sessão de provider é apenas otimização.
