# Progresso auditável do roadmap — Harness Poseidon backend

Atualizado em: 2026-07-19. Fonte: `git` (`develop`), evidências em
`docs/backend/execution/evidence/**`, `PROGRESS.md`, e execução verde de `tools/backend/verify.sh`
(234/234 testes backend, build Release 0 warnings/0 erros, 331/331 frontend) reproduzida nesta sessão.

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
| F6 | 82 | 82 | 80 | 0 | 3 stacks .NET/Node/Python start/stop/health/logs via API + **camada Java (Maven/Gradle Spring Boot)** detectada e testada. **Faltam** camadas Docker/Compose (exigem ciclo §1.2) e fallback por agente. |
| F7 | 85 | 85 | 85 | 0 | Launcher + publish self-contained com frontend embarcado + smoke do binário sem IDE verdes. Backup/restore existe (F2-OPS). **Parciais**: atualização e desinstalação segura. |
| F8 | 100 | 100 | 100 | 0 | Licença Ed25519 assinada, ativação/validação offline, revogação idempotente, dados legíveis pós-expiração; migration 0031. |
| F9 | 72 | 72 | 75 | 0 | Gateway (terminal) + adaptador Telegram (long polling, dedupe, linking, resposta na origem) verdes com fake **e smoke real do bot `@SystemPoseidon_bot`** (getMe ok, link `5774120296`, sendMessage entregue). **Falta** Teams (ordem terminal→Telegram→Teams). |
| F10 | 100 | 97 | 95 | 0 | Paridade PG completa (31 stores duais, 34 migrations), modo servidor, multiusuário, rate limit, carga 30 usuários, RBAC/ABAC e maquinaria OIDC com IdP fake. **Lacuna**: smoke OIDC com Entra ID **real** (externo, não validável sem credenciais). |
| F11 | 50 | 50 | 50 | 0 | F11-1 (threat model STRIDE, SBOM 28/77, upgrade), F11-2 (headers/cookie/CORS), F11-3 (secret scanning) e F11-4 (migração integral SQLite→PostgreSQL com replay idempotente, hash ledger/OCC preservados e conflito fail-closed) verdes. Dependências auditadas via `NuGetAudit=all`. **Faltam**: matriz formal de isolamento/recuperação/backup-restore/carga, runbooks e docs de instalação/operação, a11y/E2E e DoD global. |

## 4. Cálculo por dimensão

`Dimensão = Σ (peso_fase × fração_fase) / 100`

### Implementado
`(5·1.00)+(12·1.00)+(25·1.00)+(8·1.00)+(6·1.00)+(5·1.00)+(8·0.82)+(7·0.85)+(6·1.00)+(5·0.72)+(8·1.00)+(5·0.50)`
`= 5+12+25+8+6+5+6.56+5.95+6+3.60+8+2.50 = 93.61` → **93.6% (num 93.61 / den 100)**

### Validado
`5+12+25+8+6+5+6.56+5.95+6+3.60+(8·0.97=7.76)+2.50 = 93.37` → **93.4% (num 93.37 / den 100)**

### Integrado
`5+12+(25·0.92=23.00)+8+6+5+(8·0.80=6.40)+5.95+6+(5·0.75=3.75)+(8·0.95=7.60)+2.50 = 91.20` → **91.2% (num 91.20 / den 100)**

### Homologado
Nenhum aceite humano registrado: GNG-3 aguarda homologação visual; GNG-4/GNG-6 não alcançados
operacionalmente. → **0.0% (num 0 / den 100)**

### Geral
`Geral = 50%·Validado + 30%·Integrado + 20%·Homologado`
`= 0.50·93.37 + 0.30·91.20 + 0.20·0 = 46.685 + 27.36 + 0.00 = 74.045`
→ **≈ 74.0%**

## 5. Itens que impedem 100% (denominador restante)

Independentes (trabalho técnico que prossegue sem terceiros):

1. **F11** (maior lacuna, 50% aberto): testes formais de isolamento/upgrade/backup-restore/carga/
   regressão, runbook de incidentes, docs de
   instalação/operação, DoD global. (Headers/cookies/CORS, scanning de segredos e auditoria de
   dependências via NuGetAudit e migração SQLite→PostgreSQL já verdes.)
2. **F6**: detecção Docker/Compose (ciclo §1.2) e fallback por agente (Java já detectado).
3. **F9**: adaptador Teams + testes fake (Telegram real já validado).
4. **F7**: fluxo de atualização e desinstalação segura.
5. **Refinamentos v3 §8** (backlog desta rodada, fora do peso do roadmap base): paginação
   `page/pageSize`, arquivar/CSV do quadro, workflows por projeto, CRUD de definições de agentes,
   organograma read-model, providers/contas/modelos/esforço.

Dependentes de terceiros/credenciais (não bloqueiam o trabalho acima):

6. **Homologação humana GNG-3** (visual, em navegador) — desbloqueia os 20% de Homologado do F2 e
   dependentes.
7. **Smoke OIDC com Entra ID real** — Tenant ID + Client ID da app registration.
8. ~~Smoke real do bot Telegram~~ — **feito** (`@SystemPoseidon_bot`, F9-3); só o Host precisa estar no ar com o token em env var.
9. **Smoke de agente real que consuma cota**, se exigido na homologação.

## 6. Backlog de refinamentos desta rodada (separado do roadmap base)

Peso não incluído nos 100 pontos do roadmap base (v3 §6 manda manter separados). Estado atual:
todos **pendentes**, exceto o que já existir no código e for confirmado por auditoria dirigida
antes de implementar (evita duplicar comportamento pronto). Ver v3 §8.1–§8.13.
