# DECISIONS — Frontend Harness Poseidon

Registro de decisões de engenharia e suposições não bloqueadoras, conforme o prompt de missão v1.1.

## D-001 — Monorepo único
- **Decisão:** o frontend vive em `frontend/` dentro do repositório `harness-poseidon`, sem `git init` próprio.
- **Justificativa:** exigência explícita da seção 0 do prompt de missão.

## D-002 — Branches permanentes: apenas `main` e `develop`
- **Decisão:** todo o desenvolvimento ocorre em `develop`; `main` só recebe merge com autorização humana explícita. Nenhuma branch adicional será criada.
- **Justificativa:** convenção Git obrigatória da seção 0.

## D-003 — Nome de produto
- **Decisão:** `product.name` = "Poseidon" (mock inicial); "Harness" permanece como codinome técnico.
- **Justificativa:** seção 5 do prompt de missão.

## D-004 — Bootstrap do repositório vazio
- **Decisão:** o remote estava vazio; criado commit mínimo de bootstrap em `main` (README, .gitignore), `main` e `develop` publicadas, desenvolvimento iniciado em `develop`.
- **Justificativa:** procedimento obrigatório da seção 0 para remoto vazio.
