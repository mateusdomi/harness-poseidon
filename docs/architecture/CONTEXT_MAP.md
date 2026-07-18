# Mapa de contexto

```text
Browser / canais
       |
       v
Harness.Host  <---- HTTP loopback autenticado ---- Harness.Runner
  |  API + SignalR                               (sem acesso ao banco)
  |  application services
  |  dispatcher único de escrita (SQLite)
  v
IPersistence abstractions
  |---------------------|
  v                     v
SQLite pessoal       PostgreSQL servidor

Harness.Launcher -> porta/token/processos -> Host + Runner -> abre browser
```

O Host é a única autoridade sobre estado de domínio. Módulos colaboram por contratos tipados e eventos internos; nenhuma integração ganha autoridade de escrita direta.
