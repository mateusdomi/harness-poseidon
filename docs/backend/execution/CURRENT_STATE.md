# Estado atual do backend

Atualizado em: 2026-07-18T11:32:35Z

## Retomada rápida

- Fase atual: Fase 0 — Bootstrap e PoCs.
- Épico atual: EP-01 — bootstrap, documentação viva e pipeline local.
- Branch obrigatória: `develop`.
- Último commit validado: `180e1287fb802c93b913d27ad8ac0a6954e4e5ee` (`origin/develop` antes desta fatia).
- Próximo passo exato: criar a solução, projetos da estrutura congelada e pipeline local da Fase 0 usando `tools/backend/dotnet.sh`.
- Bloqueios: nenhum.

## Suposições ativas

- O conteúdo integral v1.3 fornecido pelo usuário é a única fonte de verdade. O arquivo `PROMPT_CODEX_BACKEND_HARNESS_POSEIDON_v1.3.md` não foi encontrado no filesystem.
- O clone `/Users/mateus/Documents/harness-poseidon` está ativo com alterações não commitadas da Kimi e é somente leitura para o trabalho backend.
- O trabalho Codex ocorre exclusivamente em `/Users/mateus/Documents/harness-poseidon-backend`.
- Contratos em `frontend/src/api/contracts/**` e `docs/frontend/HANDOFF_API.md` são provisórios até reconciliação; não serão editados pelo backend.

## Estado persistido e operacional

- Banco de dados: inexistente.
- Migrations SQLite/PostgreSQL: inexistentes.
- Worktrees vinculadas a este clone: somente a raiz em `develop`; nenhuma worktree adicional.
- Branches locais/remotas observadas: somente `main` e `develop`.
- Processos `Harness.Host`, `Harness.Runner` ou `Harness.Launcher`: nenhum.
- Containers em execução: nenhum.
- Recursos Docker com `com.harness.managed=true`: nenhum container, volume ou network.
- Evidências: ainda não há PoC comprovada; GNG-1 permanece fechado.

## Sanidade antes de retomar

```bash
cd /Users/mateus/Documents/harness-poseidon-backend
git status --short
git branch --show-current
git remote get-url origin
git fetch origin
git log --oneline --decorate -5
git worktree list
docker ps -a --filter label=com.harness.managed=true
docker volume ls --filter label=com.harness.managed=true
docker network ls --filter label=com.harness.managed=true
tools/backend/dotnet.sh --info
```

O SDK local esperado é 10.0.302. Se estiver ausente, executar `tools/backend/install-dotnet.sh`; se estiver válido, retomar pelo bootstrap da solução.
