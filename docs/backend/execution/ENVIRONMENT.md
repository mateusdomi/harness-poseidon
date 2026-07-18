# Ambiente de execução

Inventário capturado em 2026-07-18T11:32:35Z, antes de criar qualquer recurso Docker do Harness.

## Máquina e ferramentas

| Item | Valor | Uso/observação |
|---|---|---|
| SO | macOS 26.5.2 (build 25F84), arm64 | host de desenvolvimento |
| Hardware | Mac17,3; 10 CPUs; 16 GiB RAM | dimensionamento de testes e sandbox |
| Shell | zsh | nenhuma configuração global será alterada |
| Git | 2.50.1 (Apple Git-155) | repositório e fixtures descartáveis |
| .NET instalado globalmente | SDK 8.0.421; runtime 8.0.27 | insuficiente para o alvo `net10.0` |
| Node.js | 26.0.0 | build do frontend, sem alterar seus fontes |
| npm | 11.12.1 | instalação reproduzível pelo lockfile |
| Docker CLI/Engine | 29.5.2 | sandbox e PostgreSQL |
| Docker Desktop | 4.75.0 | provider Docker detectado; Colima ausente |
| Codex CLI | 0.144.5 | PoC-4 e executor real futuro |
| SQLite CLI | 3.51.0 | diagnóstico; aplicação usará provider EF Core |
| PostgreSQL CLI | ausente | testes usarão container gerenciado |

## Instalações controladas pelo projeto

| Tecnologia | Versão | Local | Motivo | Estado |
|---|---|---|---|---|
| .NET SDK | 10.0.302 (runtime 10.0.10) | `tools/backend/.tooling/dotnet` | alvo congelado pela missão sem alterar instalação global | instalado e verificado |

Instalação concluída em 2026-07-18 pelo script oficial `dotnet-install.sh`, cujo SHA-256 observado e fixado pelo instalador idempotente é `082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e`. O diretório de tooling local é ignorado apenas neste clone por `.git/info/exclude`; nenhum PATH, perfil de shell ou instalação global foi modificado. A primeira execução do template informou geração automática de certificado de desenvolvimento do SDK (não confiado); o wrapper passou então a isolar `DOTNET_CLI_HOME`, NuGet, first-run e telemetria dentro do tooling do clone.

Na PoC-4, schemas e manual oficial da versão instalada do Codex foram consultados em cache efêmero sob `tools/backend/.tooling/`, já ignorado localmente. O subprocesso de teste usou estado Codex exclusivo dentro de seus artefatos e não leu configuração, sessões ou credenciais do estado pessoal. Nenhum modelo foi invocado e nenhuma tecnologia adicional foi instalada.

## Re-inventário Docker antes da PoC-6

Capturado em 2026-07-18T12:33:08Z, antes de criar o primeiro recurso: 11 containers parados, 14 volumes, 7 networks e 9 imagens; zero recursos com `com.harness.managed=true`. O driver reportado foi `overlayfs` com seccomp/cgroup namespace. Portas adicionais em uso desde o inventário inicial: `127.0.0.1:53517`, `127.0.0.1:53518` e `[::1]:5173`; nenhuma foi usada pelo Harness.

A imagem final da PoC foi construída de `python:3.13-alpine`, sempre taggeada `harness-sandbox-poc6:<attempt>` e labelada. A base foi obtida pelo BuildKit como dependência de build; não ficou como imagem taggeada em `docker image ls`. Depois do cleanup, as contagens preexistentes permaneceram 11 containers, 14 volumes, 7 networks e 9 imagens.

Na PoC-7 foram adicionados `Microsoft.AspNetCore.SignalR.Client 10.0.10` (cliente de integração) e `Microsoft.AspNetCore.OpenApi 10.0.10` (geração canônica). O audit rejeitou a dependência transitiva vulnerável `Microsoft.OpenApi 2.0.0`; `Microsoft.OpenApi 2.7.5` foi pinado por central transitive pinning por ser a primeira versão 2.x corrigida para GHSA-v5pm-xwqc-g5wc. Nenhum warning de audit foi suprimido.

## Inventário Docker preexistente

Contexto: `desktop-linux`. Foram encontrados 11 containers, todos parados, 14 volumes e 7 networks. Nenhum possui a label `com.harness.managed=true`; portanto, são propriedade de outros projetos e são intocáveis.

### Containers

| Nome | Estado no inventário | Portas publicadas registradas |
|---|---|---|
| `approve-mssql` | exited | 14333 (configuração, não ativa) |
| `s2-avaliacao-seq` | exited | 5341 (configuração, não ativa) |
| `s2-avaliacao-sqlserver` | exited | 1433 (configuração, não ativa) |
| `sorc-sonarqube-local` | exited | 9001 (configuração, não ativa) |
| `sorc-oracle-local` | exited | 1521 |
| `sonarqube` | exited | 9000 |
| `smartshelf-sql` | exited | 14333 (configuração, não ativa) |
| `centralflow-db` | exited | 1523 (configuração, não ativa) |
| `supervia_seq` | exited | 5341 (configuração, não ativa) |
| `supervia_mailpit` | exited | 1025 e 8025 (configuração, não ativa) |
| `supervia_sqlserver` | exited | 1433 (configuração, não ativa) |

### Volumes

Preexistentes e intocáveis: seis volumes anônimos observados, `app_s2_seq_data`, `app_s2_sqlserver_data`, `approve-mssql-data`, `centralflow_centralflow_oracle_data`, `infra_seq_data`, `infra_sqlserver_data` e dois outros volumes anônimos. A contagem canônica no inventário foi 14.

### Networks

Preexistentes e intocáveis: `app_default`, `approve_default`, `bridge`, `centralflow_default`, `host`, `infra_default`, `none`.

### Imagens preexistentes

Foram observadas imagens de SonarQube, Oracle, SQL Server, Seq, Mailpit, Sonar Scanner e Testcontainers/Ryuk. Nenhuma imagem foi criada ou removida.

## Portas TCP em uso no host

No instante do inventário: `5000`, `7000`, `50942` e `59869`. As portas 5000 e 7000 pertencem ao Control Center do macOS. O Harness deve solicitar porta dinâmica ao SO; nenhuma dessas portas será presumida disponível.

## Política operacional Docker

- Todo recurso criado pelo Harness terá prefixo `harness-` e label `com.harness.managed=true`.
- Não parar, remover, recriar ou alterar recursos sem essa label.
- Nunca executar `docker system prune`, `docker volume prune` ou equivalentes.
- Publicar somente em porta host livre/dinâmica e registrar a porta escolhida.
- Cleanup e detecção de órfãos filtram simultaneamente por prefixo e label.
