# Progresso auditável do roadmap — Harness Poseidon backend

Atualizado em: 2026-07-19. Fonte: `git` (`develop`), evidências em
`docs/backend/execution/evidence/**`, `PROGRESS.md`, e execução verde de `tools/backend/verify.sh`
(245/245 testes backend, build Release 0 warnings/0 erros, 331/331 frontend) reproduzida nesta sessão.

Este documento é **recomputável**: cada fração vem de entregáveis documentados com evidência
executada, nunca de "arquivo criado". Pesos das fases são fixos (v3 §6) e não podem ser alterados.

## 1. Definição das quatro dimensões (rubrica auditável)

Para cada fase, mede-se a fração dos entregáveis documentados que atinge cada estado:

- **Implementado (I)**: código + migrations + contratos existem e compilam para o entregável.
- **Validado (V)**: exercitado por teste automatizado verde com evidência registrada (régua 4.17).
- **Integrado (G)**: fiado ponta-a-ponta no Host/pipeline real e exercitado por HTTP/evento
  (não apenas store isolado). Para fases com UI, inclui frontend servido + contrato reconciliado
  + E2E contra API real.
- **Homologado (H)**: **aceite humano/operacional registrado com evidência**. O projeto só define
  aceite humano nos GNGs de homologação (GNG-3 dogfood visual; GNG-4 instalação limpa; GNG-6 DoD).
  Gates automatizados (GNG-1, GNG-2, GNG-5) **não** são homologação humana.

## 2. Pesos fixos das fases (v3 §6)

| Fase | Peso | Fase | Peso |
|---|---|---|---|
| F0 | 5 | F6 | 8 |
| F1 | 12 | F7 | 7 |
| F2 | 25 | F8 | 6 |
| F3 | 8 | F9 | 5 |
| F4 | 6 | F10 | 8 |
| F5 | 5 | F11 | 5 |

Total: **100**.

## 3. Frações por fase (com justificativa)

| Fase | I | V | G | H | Base da fração |
|---|---:|---:|---:|---:|---|
| F0 | 100 | 100 | 100 | 0 | 9 PoCs verdes com evidência; GNG-1 (automático) verde; mecanismos integrados na F1. Sem homologação humana. |
| F1 | 100 | 100 | 100 | 0 | Fundação dual-provider + motor durável + GNG-2 (SIGKILL reconciliado) verde e fiado no Host. Sem homologação humana. |
| F2 | 100 | 100 | 92 | 0 | Toda a API/SignalR + integração frontend (bundle FR-1/FR-2 servido, 270 FE verdes, drift reconciliado). Blocker técnico da homologação resolvido: adoção de sessão no modo pessoal (cockpit carrega em navegador novo). Lacuna restante: E2E navegado + resync visual e o **aceite humano do GNG-3 → H=0**. |
| F3 | 100 | 100 | 100 | 0 | Templates canônicos, guarda de ações invioláveis, verificação em 3 camadas e run semiautônomo completo provados via API/anti-burla. Gate da fase (automático) verde. |
| F4 | 100 | 100 | 100 | 0 | Segurança de upload (allowlist/magic bytes/anti zip-bomb/traversal/quarentena) + demanda de documento real, via API e migration 0029. |
| F5 | 100 | 100 | 100 | 0 | Upload inspecionado de referências (PNG/JPEG/ZIP), galeria, waiver; migration 0030. |
| F6 | 100 | 100 | 96 | 0 | .NET/Node/Python via API, Java, Dockerfile/Compose reais e **fallback por agente** policy-gated/JSON fechado, com execução real e probes adversariais. Lacuna de integração: smoke com executor/modelo real (externo); composição Host e fake tipado estão verdes. |
| F7 | 100 | 100 | 98 | 0 | Launcher/Host/Runner/SPA self-contained, manifesto íntegro, instalação idempotente, update com backup+swap/rollback e uninstall preservando dados; pacote macOS real passou start/health/shutdown. Lacuna externa: Developer ID/notarização e aceite em macOS limpo (GNG-4). |
| F8 | 100 | 100 | 100 | 0 | Licença Ed25519 assinada, ativação/validação offline, revogação idempotente, dados legíveis pós-expiração; migration 0031. |
| F9 | 100 | 100 | 97 | 0 | Gateway, Telegram e Teams verdes. Teams cobre webhook autenticado, linking AAD, dedupe durável, resposta na origem, anexos por metadados, retry e defesa SSRF contra fake local. Telegram também tem smoke real. Lacuna externa: smoke com credenciais Microsoft/Azure Bot reais. |
| F10 | 100 | 97 | 95 | 0 | Paridade PG completa (31 stores duais, 34 migrations), modo servidor, multiusuário, rate limit, carga 30 usuários, RBAC/ABAC e maquinaria OIDC com IdP fake. **Lacuna**: smoke OIDC com Entra ID **real** (externo, não validável sem credenciais). |
| F11 | 75 | 75 | 75 | 0 | F11-1..5 verdes e F11-6 fecha publish servidor, instalação/operação, backup/upgrade, resposta a incidentes e gate operacional real. Dependências auditadas via `NuGetAudit=all`. **Faltam**: a11y/E2E real e DoD global/GNG-6. |

## 4. Cálculo por dimensão

`Dimensão = Σ (peso_fase × fração_fase) / 100`

### Implementado
`(5·1.00)+(12·1.00)+(25·1.00)+(8·1.00)+(6·1.00)+(5·1.00)+(8·1.00)+(7·1.00)+(6·1.00)+(5·1.00)+(8·1.00)+(5·0.75)`
`= 5+12+25+8+6+5+8+7+6+5+8+3.75 = 98.75` → **98.8% (num 98.75 / den 100)**

### Validado
`5+12+25+8+6+5+8+7+6+5+(8·0.97=7.76)+3.75 = 98.51` → **98.5% (num 98.51 / den 100)**

### Integrado
`5+12+(25·0.92=23.00)+8+6+5+(8·0.96=7.68)+(7·0.98=6.86)+6+(5·0.97=4.85)+(8·0.95=7.60)+3.75 = 95.74` → **95.7% (num 95.74 / den 100)**

### Homologado
Nenhum aceite humano registrado: GNG-3 aguarda homologação visual; GNG-4/GNG-6 não alcançados
operacionalmente. → **0.0% (num 0 / den 100)**

### Geral
`Geral = 50%·Validado + 30%·Integrado + 20%·Homologado`
`= 0.50·98.51 + 0.30·95.74 + 0.20·0 = 49.255 + 28.722 + 0.00 = 77.977`
→ **≈ 78.0%**

## 5. Itens que impedem 100% (denominador restante)

Independentes (trabalho técnico que prossegue sem terceiros):

1. **F11** (25% aberto): a11y/E2E real e DoD global/GNG-6. Publicação servidor,
   instalação/operação, runbook, headers/cookies/CORS, scanning, auditoria de dependências,
   migração SQLite→PostgreSQL e matriz de resiliência já estão verdes.
2. **Refinamentos v3 §8** (backlog desta rodada, fora do peso do roadmap base): paginação
   `page/pageSize`, arquivar/CSV do quadro, workflows por projeto, CRUD de definições de agentes,
   organograma read-model, providers/contas/modelos/esforço.

Dependentes de terceiros/credenciais (não bloqueiam o trabalho acima):

3. **Homologação humana GNG-3** (visual, em navegador) — desbloqueia os 20% de Homologado do F2 e
   dependentes.
4. **GNG-4 em macOS limpo + Developer ID/notarização** — exige identidade/certificado Apple e aceite operacional; pacote ad-hoc local já passou o fluxo completo.
5. **Smoke OIDC com Entra ID real** — Tenant ID + Client ID da app registration.
6. **Smoke Teams com Azure Bot/Microsoft 365 real** — credenciais e tenant externos; a integração fake já está verde.
7. ~~Smoke real do bot Telegram~~ — **feito** (`@SystemPoseidon_bot`, F9-3); só o Host precisa estar no ar com o token em env var.
8. **Smoke do fallback F6 com executor/modelo real que consuma cota**, se exigido na homologação.

## 6. Backlog de refinamentos desta rodada (separado do roadmap base)

Peso não incluído nos 100 pontos do roadmap base (v3 §6 manda manter separados). Estado atual:
todos **pendentes**, exceto o que já existir no código e for confirmado por auditoria dirigida
antes de implementar (evita duplicar comportamento pronto). Ver v3 §8.1–§8.13.
