# Evidência F6-1 — três stacks reais com health e camadas de detecção

Data: 2026-07-19.

O recurso "rodar projeto" avançou para o escopo da Fase 6 sobre a fundação F2:

- **Detecção em camadas ampliada**: além de `*.csproj` (.NET) e `package.json` com entry `main` (Node), o detector agora reconhece `package.json` com script `start` (fluxo npm — cobre apps React/Vite com `npm start`) e entradas Python (`main.py`/`app.py`, executadas com `python3` e `PYTHONUNBUFFERED`), sempre com porta livre alocada dinamicamente, fingerprint SHA-256 por runtime+path e ambiente mínimo controlado.
- **Health check real**: `GET /api/v1/run-targets/{id}/health` devolve contrato tipado (`healthy`, `statusCode`, `detail` fechado, `checkedAt`): processo morto → `process_not_running`; vivo sem probe HTTP → `process_alive_without_http_probe`; alvo http vivo → probe real na URL com timeout de 3s (`http_endpoint_responded`/`unreachable`/`timeout`), atualizando `lastCheckAt` via `MarkCheckedAsync`. O contrato público `RunTargetContract` do frontend permaneceu intocado (sem drift); o health é endpoint aditivo com OpenAPI republicado.

O teste de integração executa **três stacks diferentes reais** — HttpListener .NET, http server Node e `http.server` Python — de ponta a ponta via API: detecção das 3, start das 3 com corpo HTTP real verificado, health `200/http_endpoint_responded` nas 3 em execução, health `process_not_running` após stop, restart, cleanup parando as 2 restantes, eventos `run.logAppended` sequenciados e `run.environmentCleaned` auditado, e persistência dos 3 alvos após restart do Host.

Gate: format sem mudanças; build Release zero warnings/erros; backend 198/198 (`Unit 107`, `Integration 49`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`).

As camadas de scripts registrados/heurísticas e agente como último recurso permanecem no backlog;
Dockerfile/Compose foram fechados em F6-3 abaixo.

## F6-2 — camada de manifesto Java (Maven/Gradle Spring Boot)

Data: 2026-07-19.

O detector passou a reconhecer a stack **Java** (item explícito da Fase 6), fechando essa lacuna:

- **Maven** (`pom.xml`) e **Gradle** (`build.gradle`/`build.gradle.kts`) — registrado **apenas** quando o
  manifesto declara Spring Boot (heurística: `spring-boot`/`org.springframework.boot`), para não criar
  alvo que não sobe com URL/health. Porta livre alocada e injetada tanto por `SERVER_PORT` quanto pelo
  argumento de execução (`-Dspring-boot.run.arguments=--server.port=` no Maven, `--args=--server.port=`
  no Gradle). Prefere os wrappers `mvnw`/`gradlew` quando presentes (reprodutibilidade); senão usa
  `mvn`/`gradle` via `/usr/bin/env`. Fingerprint SHA-256 por runtime+path como as demais camadas.
- Teste `RunTargetJavaDetectionTests`: fixtures temporárias comprovam detecção de um alvo Maven e um
  Gradle Spring Boot (porta, `SERVER_PORT`, arg de porta e URL loopback corretos) e que um `pom.xml`
  **sem** Spring Boot **não** vira alvo executável.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 232/232.

## F6-3 — alvos Dockerfile e Compose gerenciados

Data: 2026-07-19.

- `RunTargetDetector` reconhece `Dockerfile*` com `EXPOSE` numérico e os quatro nomes canônicos
  de Compose. Compose é normalizado por `docker compose config --format json`; só entra no catálogo
  quando possui um serviço com porta exclusivamente interna (`expose`). Publicações de host já
  declaradas são recusadas para impedir colisão com portas de outros projetos.
- `DockerRunTargetLifecycle` gera nomes `harness-run-*`, aplica
  `com.harness.managed=true` e ownership `com.harness.run-target=<ULID>` a containers, imagens
  construídas, networks e volumes. Dockerfile é buildado com imagem taggeada `harness-*`; Compose
  recebe override temporário gerado sem alterar o repositório do projeto e publica somente uma
  porta loopback livre.
- A validação é fail-closed: `container_name`, imagem construída, network ou volume fora do prefixo
  são recusados; portas fixas e volumes anônimos também. Cleanup inventaria pelo label da execução,
  reinspeciona prefixo + os dois labels antes de cada remoção e nunca usa prune.
- O supervisor usa o lifecycle tanto no stop/restart/cleanup quanto em saída inesperada ou falha de
  startup. Dockerfile/Compose preservam o `kind=http` do contrato público; o modo de lifecycle fica
  somente no metadata privado de lançamento, sem drift com o frontend protegido.
- `RunTargetDockerLifecycleTests` usa o Docker Engine real: build/run de Dockerfile, build/up de
  Compose, respostas HTTP distintas, porta dinâmica, stop e inventário final vazio de containers,
  images, networks e volumes; um Compose com `18080:8080` comprova a recusa de porta fixa.

Gate focado: 2/2 testes reais verdes em 3 s. Gate integral: 236/236 backend
(`Unit 121`, `Integration 72`, `Contract 28`, `Recovery 5`, `Architecture 7`, `Concurrency 3`),
331/331 frontend, build Release e format verdes, zero warning/erro e zero recurso Docker Harness
órfão.
