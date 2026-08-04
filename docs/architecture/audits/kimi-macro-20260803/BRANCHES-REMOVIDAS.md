# Referências de branches removidas do remoto em 2026-08-04

Removidas para deixar o remoto com apenas `main` e `develop`, a pedido do proprietário:
outra IA que clone o repositório não deve precisar decidir qual branch é a boa.

NADA foi perdido. O conteúdo de todas elas foi conferido arquivo a arquivo e já está em
`develop`; o que se apaga aqui é o ponteiro, não o trabalho. Os SHAs ficam registrados
para que qualquer uma possa ser recriada com `git branch <nome> <sha>`.

- `audit/kimi-macro-20260803` → `928e0a12e0a6893cfdfd72f589f16eb076cff174`
- `preservado/kimi-board-ux` → `6af978bae1aba0278136299c349d82772a703375`
- `preservado/kimi-chat-round` → `97d25075357eb5dde76c0515703f5dd83d64f220`
- `preservado/kimi-dashboard-round` → `034bac18549c19449cec6dbc70c8f282ab4133d1`

As cópias locais dessas branches permanecem nesta máquina.

## Branches LOCAIS removidas em 2026-08-04

Conferidas uma a uma por dois critérios: se a ponta já está contida em `develop` e se
algum arquivo criado por elas sumiu de lá. As 25 passaram — 16 são ancestrais da
`develop` e 9 não são, mas não têm um único arquivo exclusivo.

O que torna isto reversível não é a conferência: é a lista abaixo. Recriar qualquer uma é
`git branch <nome> <sha>`, e o objeto continua no repositório até a coleta de lixo.

- `agente/claude-b` → `c86ad5d04fe3fd0b1d89d6bec866069be4fb79a0` (ancestral)
- `agente/codex-a` → `bea89a73f814f17d6f0e5a9470f4efecf259fb96` (ancestral)
- `agente/codex-b` → `4720d84f844c612197392bb3e094be6bf4e8e3bd` (ancestral)
- `agente/codex-c` → `3d846f37575957fb255da316f212eaa1103fbc3c` (ancestral)
- `codex-phase10-completion` → `9dfaa828871af9bc4a0a08c5f4399b7fa9cbac95` (ancestral)
- `codex-phase11-completion` → `a43686d6b5be486daf8be83d11b34ee995e0504c` (ancestral)
- `fix/poseidon-phase0a1-plan-materialization` → `76c09615b46ad84305d1bbd02ccf83072b5f140a` (ancestral)
- `fix/poseidon-phase0a2-lease-and-context` → `96c4f9f28ebfd88f1ec1af3d009408cc7e126e8a` (ancestral)
- `fix/poseidon-phase0a3-sanitization` → `9949c2beee0f5081ff773476414876bf5bc2b7de` (ancestral)
- `fix/poseidon-phase0b1-sandbox-attestation` → `4914521eb52bb000af9b992747054cd057758771` (ancestral)
- `fix/poseidon-phase0b2-tool-broker` → `e769d52371df71d52f656b93c98e5d878a6b7ddb` (ancestral)
- `fix/poseidon-phase0c-merge-intent` → `7bc103acfa4a9d45821bd009844f53792de617d5` (ancestral)
- `fix/poseidon-phase0e-docker-required` → `a511c11535e86b73b338cdb254c82da271ae8776` (ancestral)
- `fix/poseidon-phase1a-checkpoint-resume` → `10edb30de9bf3456a322d023f7387bdd17bf4210` (ancestral)
- `fix/poseidon-phase1b-effort-budget` → `77dd5ea188be2ca9851e02df553f8b756227cc4a` (ancestral)
- `fix/poseidon-phase1b-model-asymmetry` → `96e91bd9ee1982c0ea44652c5eeb6dbc90d5a067` (ancestral)
- `kimi-board-ux` → `6af978bae1aba0278136299c349d82772a703375` (conteúdo presente)
- `task/agent-run-01kyjhrp1wqah21v1b8sb20gtx` → `114cf3408dc1684c9ee81f38c0d7d25619f3b636` (ancestral)
- `task/agent-run-01kyjhrp2yx0z3x0dkpechsqqk` → `211fd25525b74050ae9abdacab2cd0a86afec977` (conteúdo presente)
- `task/agent-run-01kyjj92ejkt3e70y3rk5cw4hb` → `e25092e52d1ed714abc63bb63937824692b6e4e5` (ancestral)
- `task/agent-run-01kyjjpgcnh1b5yhv5bepsw59s` → `5389e48dd856ce187f9edbba06cbc308b3ee1de5` (conteúdo presente)
- `task/agent-run-01kyjjtb3c1d2dtrwrf70ak8a4` → `03d3fb6fb3a5c73f5ac4a17b27675930715d52b0` (conteúdo presente)
- `task/agent-run-01kyjk2agtfecnyj7467x24dn9` → `01e8a14538f5151f074f245c9509ea1429017d06` (conteúdo presente)
- `task/kimi-chat-round` → `97d25075357eb5dde76c0515703f5dd83d64f220` (conteúdo presente)
- `task/kimi-dashboard-round` → `034bac18549c19449cec6dbc70c8f282ab4133d1` (conteúdo presente)
