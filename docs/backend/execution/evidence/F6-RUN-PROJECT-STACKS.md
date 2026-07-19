# Evidência F6-1 — três stacks reais com health e camadas de detecção

Data: 2026-07-19.

O recurso "rodar projeto" avançou para o escopo da Fase 6 sobre a fundação F2:

- **Detecção em camadas ampliada**: além de `*.csproj` (.NET) e `package.json` com entry `main` (Node), o detector agora reconhece `package.json` com script `start` (fluxo npm — cobre apps React/Vite com `npm start`) e entradas Python (`main.py`/`app.py`, executadas com `python3` e `PYTHONUNBUFFERED`), sempre com porta livre alocada dinamicamente, fingerprint SHA-256 por runtime+path e ambiente mínimo controlado.
- **Health check real**: `GET /api/v1/run-targets/{id}/health` devolve contrato tipado (`healthy`, `statusCode`, `detail` fechado, `checkedAt`): processo morto → `process_not_running`; vivo sem probe HTTP → `process_alive_without_http_probe`; alvo http vivo → probe real na URL com timeout de 3s (`http_endpoint_responded`/`unreachable`/`timeout`), atualizando `lastCheckAt` via `MarkCheckedAsync`. O contrato público `RunTargetContract` do frontend permaneceu intocado (sem drift); o health é endpoint aditivo com OpenAPI republicado.

O teste de integração executa **três stacks diferentes reais** — HttpListener .NET, http server Node e `http.server` Python — de ponta a ponta via API: detecção das 3, start das 3 com corpo HTTP real verificado, health `200/http_endpoint_responded` nas 3 em execução, health `process_not_running` após stop, restart, cleanup parando as 2 restantes, eventos `run.logAppended` sequenciados e `run.environmentCleaned` auditado, e persistência dos 3 alvos após restart do Host.

Gate: format sem mudanças; build Release zero warnings/erros; backend 198/198 (`Unit 107`, `Integration 49`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`).

Camadas restantes da detecção F6 (Dockerfile/Compose como alvos gerenciados sob as regras Docker 1.2, scripts registrados pelo usuário e agente como último recurso) permanecem no backlog da fase, registradas aqui como pendência consciente — exigem política de execução containerizada de projetos de usuário que conversa com o modo docker da execução isolada.

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
