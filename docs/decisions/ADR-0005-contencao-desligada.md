# ADR-0005 — Contenção desligada: decisão reversível para uso interno

## Contexto

O Poseidon prevê quatro fronteiras de contenção para cada execução de agente:

1. **Worktree isolada por tentativa** — o código trabalhado é um clone efêmero, não o repositório principal.
2. **Claims de path por card** — o agente só vê e toca nos caminhos declarados.
3. **Allowlist de ferramentas da persona** — ferramentas de risco só são liberadas quando a persona e o contexto autorizam.
4. **Contêiner Docker com proxy de egresso** — rootfs somente-leitura, rede restrita, limites de CPU/memória/PIDs.

Na instalação auditada em 2026-08-03, as três primeiras fronteiras estavam ativas. A quarta — o contêiner — estava **desligada por decisão registrada do proprietário**. A causa imediata é técnica e local: as credenciais das contas de ator (Claude Code, Codex, Antigravity) vivem no Keychain do macOS, que não existe dentro do contêiner Docker nesta máquina. Com a mesma conta, mesmo config home e mesma chave, a CLI responde no host e retorna `Invalid API key · Please run /login` dentro do contêiner. Ligá-lo não aumentaria a segurança — pararia todo o trabalho autônomo.

O achado `F-19` da auditoria macro apontou o risco: para uso interno essa decisão é justificável e reversível, mas para um produto vendido a terceiro a contenção desligada é um risco de segurança e compliance aberto.

## Decisão

**A execução sem contêiner é aceita apenas em instalações pessoais/internal, com declaração explícita do operador, e nunca em instalações de cliente ou modo servidor multiusuário.**

1. O modo padrão do produto continua sendo `IsolatedExecutionMode.Docker`. Não se nasce desprotegido.
2. Um operador pode desligar a contenção (`Mode = Disabled`) ou deixá-la ligada mas sem credenciais válidas no contêiner, desde que:
   - registre o motivo em `UncontainedExecutionReason`;
   - ative `UncontainedExecutionAcknowledged` como declaração consciente;
   - entenda que o proxy de egresso, rootfs somente-leitura e limites de recurso não se aplicam.
3. Cada execução sem contêiner gera uma `sandbox_attestation` com provider `none`, código `allowed_uncontained` e o motivo declarado. A ausência de sandbox é um fato gravado, não uma omissão.
4. A política de ferramentas continua fail-closed: ferramentas de alto risco exigem attestation verificada OU explícita permissão de execução descontida. Não há bypass silencioso.
5. **Para produto vendido a terceiro ou modo servidor, a contenção Docker deve estar operacional antes da assinatura do contrato.** A instalação de cliente não deve permitir `Mode = Disabled` sem violar o Terms of Use ou a política de segurança do tenant.

## Consequências

- **Uso interno:** o proprietário mantém a autonomia de desligar a contenção quando a infraestrutura local a impede, sem recompilar o produto.
- **Produto de terceiro:** a configuração de instalação deve bloquear ou alertar quando `Mode != Docker` ou quando `UncontainedExecutionAcknowledged` estiver ativo. A maturidade de segurança só é alcançada com as quatro fronteiras de pé.
- **Atestação é autoridade:** qualquer decisão de política (ferramentas, merge, liberação de artefato) consulta a attestation da tentativa, não o valor de configuração. Isso impede que uma instalação com Docker desligado minta para o resto do sistema.
- **Risco consciente aceito:** executar sem contêiner em um laptop pessoal, com worktree, claims e allowlist ativos, é uma postura defensável. Executar assim numa máquina de cliente não é.
- **Trigger para revisão:** se o produto passar a oferecer instalação gerenciada, SaaS ou multi-tenant, este ADR deve ser revisado para tornar a contenção obrigatória e auditar a configuração automaticamente.

## Alternativas descartadas

- **Reabrir o contêiner sem resolver o Keychain do macOS.** Rejeitado porque foi medido que todas as contas de ator falham dentro do contêiner. Forçar o modo containerizado cegaria a frota.
- **Mover credenciais para dentro do contêiner (env, volume de secrets).** Rejeitado porque quebraria o modelo de segurança atual — segredos só no Keychain do SO — e criaria uma superfície de ataque nova sem ganho real.
- **Deixar a execução sem contêiner como padrão.** Rejeitado após o incidente da fase 0B1: o orquestrador afirmava `SandboxActive: true` por literal, a política de ferramentas acreditava na fronteira e liberou execução de risco. O padrão hoje é Docker, e a ausência só ocorre por declaração explícita.
- **Permitir execução descontida em modo servidor.** Rejeitado categoricamente: o isolamento de tenant e o cumprimento de SLAs dependem da contenção operacional.

## Relacionado

- Achado `F-19` da auditoria macro de 2026-08-03.
- `docs/security/isolation.md` — dimensões de isolamento do produto.
- `src/Harness.Host/Execution/IsolatedExecutionSettings.cs` — configuração de execução isolada.
- `src/Harness.Host/Execution/SandboxAttestationService.cs` — emissão e registro da attestation.
- `src/Modules/Harness.Modules.Execution/Application/Sandbox/SandboxAttestation.cs` — definição de sandbox efetiva.
- `docs/decisions/ADR-0001-arquitetura-definitiva-v3.md` — arquitetura geral e modos pessoal/servidor.
