# Estado atual do backend

Atualizado em: 2026-07-18T11:47:21Z

## Retomada rápida

- Fase atual: Fase 0 — Bootstrap e PoCs.
- Épico atual: EP-02 — SharedKernel e contratos primitivos; bootstrap EP-01 validado.
- Branch obrigatória: `develop`.
- Último commit remoto validado: `dae3315` (`develop`); a fatia de bootstrap da solução está validada e aguardando o commit que conterá este estado.
- Próximo passo exato: implementar IDs ULID tipados, `Result`, clock, erros e primitivas no SharedKernel com testes unitários; depois iniciar a PoC-1 SQLite/dispatcher.
- Bloqueios: nenhum.

## Suposições ativas

- O conteúdo integral v1.3 fornecido pelo usuário é a única fonte de verdade. O arquivo `PROMPT_CODEX_BACKEND_HARNESS_POSEIDON_v1.3.md` não foi encontrado no filesystem.
- O clone `/Users/mateus/Documents/harness-poseidon` está ativo com alterações não commitadas da Kimi e é somente leitura para o trabalho backend.
- O trabalho Codex ocorre exclusivamente em `/Users/mateus/Documents/harness-poseidon-backend`.
- Contratos em `frontend/src/api/contracts/**` e `docs/frontend/HANDOFF_API.md` são provisórios até reconciliação; não serão editados pelo backend.

## Estado persistido e operacional

- Banco de dados: inexistente.
- Migrations SQLite/PostgreSQL: inexistentes; 28 lockfiles NuGet foram materializados, um por projeto.
- Worktrees vinculadas a este clone: somente a raiz em `develop`; nenhuma worktree adicional.
- Branches locais/remotas observadas: somente `main` e `develop`.
- Processos `Harness.Host`, `Harness.Runner` ou `Harness.Launcher`: nenhum.
- Containers em execução: nenhum.
- Recursos Docker com `com.harness.managed=true`: nenhum container, volume ou network.
- Solução: 22 projetos de produção (Host, Runner, Launcher, SharedKernel, persistência e 15 módulos) e 6 projetos de teste em `Harness.sln`.
- Pipeline: `tools/backend/verify.sh` verde em Release, zero warnings/erros, 10 testes verdes nas seis suítes.
- Host smoke: `/health` respondeu `{"status":"healthy"}` em porta loopback dinâmica 53906; processo finalizado com exit code 0.
- Evidências: bootstrap e testes de arquitetura comprovados; ainda não há PoC 1–9 concluída e GNG-1 permanece fechado.

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

O SDK local esperado é 10.0.302. Se estiver ausente, executar `tools/backend/install-dotnet.sh`; se estiver válido, executar `tools/backend/verify.sh` e retomar pelo SharedKernel.
