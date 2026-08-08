# Plano de conclusão — Prisma e Indicadores (2026-08-08)

Estado e trabalho restante para terminar os dois produtos **neste fim de semana**, com a fleet
paga (Claude B, 2 Codex, GLM) executando e o chief/Bruna apenas orquestrando. Documento de
handoff: qualquer sessão de supervisão retoma daqui.

## Regra de cota (ordem do dono, cota do chief em ~7%)

- **A conta do chief (`chief-claude-primary` = Claude A / Bruna) NÃO executa.** Reforço do chefe
  desligado (`Harness__AgentRuns__AllowChiefReinforcement=false`). O chief só orquestra (decisão
  determinística — não chama o modelo).
- **Executores (cota própria):** `worker-claude-secondary` (Claude B), `worker-codex-frontend` e
  `worker-codex-critic` (Codex ×2, resetam a tarde de 08/08), `worker-glm-general` (GLM, habilitado).
- **Kimi É EXECUTOR** (adapter implementado 08/08, commit 1a43bb7e): `kimi -p <prompt> --output-format text`. Autenticação por symlink da config home da conta ao `~/.kimi-code` do operador (uma assinatura só, sem isolamento).
- **Verificação por shell (E2E, Docker, curl) é GRÁTIS** de cota de modelo — o supervisor pode
  rodar E2E como gate manual sem gastar cota.

## Estado dos produtos (medido por navegador, não por árvore)

| Produto | E2E de navegador | Observação |
|---|---|---|
| **Indicadores** | **25 pass / 0 fail** (desktop) | VERDE comprovado. Suíte `tests/e2e/` (7 specs). |
| **Prisma** | 6 pass / 21 fail (última medição) | 3 defeitos achados e em correção; ver abaixo. |

## Prisma — defeitos restantes (E2E `frontend/e2e/`, 4 specs)

Progresso: NULL (5.4 ✔), botão de login travado (5.5 ✔), **rótulos de senha ambíguos (5.6, em curso)**.
- **Defeito vivo:** na tela de troca de senha do 1º acesso, `getByLabel('Nova senha')` casa 2
  campos ("Repita a nova senha" colide). Rótulos/aria-labels têm que ser inequívocos. Isso trava
  o primeiro-acesso e cascateia (~21 falhas).
- **Meta:** `frontend/e2e` 100% verde contra ambiente vivo. Objetivo 5.6 já despachado.

### Como o supervisor prova o Prisma (shell, grátis)
```
cd ~/.harness-poseidon/repositories/01KY36ED34GHB0NGGX718NSVK3/prismav2
docker compose --env-file infra/.env -f infra/docker-compose.yml up -d oracle   # espere healthy
# API: dotnet run --project src/Prisma.Api -- --urls http://127.0.0.1:5199, com env de infra/.env
#   PRISMA_ORACLE_CONEXAO=User Id=PRISMA_APP;Password=$PRISMA_ORACLE_SENHA_DEV;Data Source=localhost:1521/XEPDB1
cd frontend && npm install && PRISMA_E2E_API=http://127.0.0.1:5199 \
  PRISMA_E2E_SENHA_INICIAL=$PRISMA_SENHA_INICIAL npx playwright test
```
Credenciais: e-mails semeados em `docs/ACESSO.md`; senha inicial em `infra/.env`
(`PRISMA_SENHA_INICIAL`). Indicadores: logins admin/gestor/operador/consulta, senha em
`indicv2/infra/.env` (`SENHA_INICIAL`).

## Plataforma — trabalho de código restante (para sessão com cota de código)

Estas são tarefas do REPO da plataforma (não dos produtos), para um supervisor com cota:

1. **Gate de E2E FUNCIONAL (Task #10)** — o gate existe, dispara e é seguro (nunca falso verde),
   mas devolve `unavailable` porque não injeta segredos no ambiente. Falta: o runner gerar
   **segredos efêmeros** (ler `infra/.env.example`, gerar valores, escrever `.env` temporário),
   subir o compose em **porta de banco aleatória** e derivar o env da API dos mesmos valores.
   Manifesto `.harness/e2e.json` precisa ganhar `dbEnvFile`, `dbPortVar`, `apiEnvTemplate`
   (`${DB_PASSWORD}`/`${DB_PORT}`/`${SERVICE}`). Testar contra Indicadores (já verde à mão) antes
   de declarar. `ProductE2ERunner` e `ProductE2EHarness` já existem como base.
2. **Cobertura como portão de conclusão (Task #11)** — o grafo de requisitos está religado (26+22
   nós, auto-mantido). Falta: ligar objetivo→critério e o sequenciador só declarar projeto
   pronto quando `RequirementCoverageAnalyzer` = cobertura total, com a prova sendo a suíte E2E.

## Por que a fleet não se auto-verifica em E2E (limitação conhecida)

Os executores têm .NET SDK + Node no sandbox, mas **não sobem Oracle (sem docker-in-docker)** —
então não conseguem rodar a suíte E2E de verdade. Já aconteceu 3× de um executor declarar "E2E
verde" com falhas reais. Enquanto o gate de E2E da plataforma não é funcional (item 1), a prova
de navegador depende do supervisor rodar por shell. É a peça que fecha a autonomia.

## Ordem de trabalho recomendada para o fim de semana

1. Fleet fecha o Prisma 5.6 (rótulos) — Claude B/Codex quando resetarem.
2. Supervisor roda o E2E do Prisma por shell (grátis) a cada entrega; se vermelho, despacha o
   defeito específico; repete até 100% verde.
3. Com os dois produtos verdes, rodar a auditoria de rastreabilidade (matriz critério→prova) já
   despachada e conferir cobertura.
4. Homologação humana do dono.
5. Trabalho de plataforma (itens 1–2) numa sessão com cota de código, para a autonomia fechar.
