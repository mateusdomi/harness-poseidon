# Poseidon

Poseidon é uma fábrica autônoma de software com plano de controle .NET, execução
durável de agentes e persistência compatível com modo pessoal e modo servidor.

As regras do repositório começam em `governance/core.md`. O catálogo e a política
de carga de contexto estão em `governance/manifest.yaml`.

## Modo pessoal

O modo padrão usa SQLite e inicia Host, Runner e interface local:

```bash
./poseidon start
./poseidon status
```

Para encerrar, execute `./poseidon stop`.

## Modo servidor

Com um PostgreSQL 16 disponível, informe a conexão somente por variável de
ambiente e inicie o Host:

```bash
export Harness__Database__Provider=postgres
export Harness__Database__ConnectionString="$POSEIDON_POSTGRES_CONNECTION"
tools/backend/dotnet.sh run --project src/Harness.Host/Harness.Host.csproj
```

O modo servidor executa as migrations PostgreSQL na inicialização. Credenciais não
devem ser gravadas no repositório ou passadas como argumento.

## Desenvolvimento

O gate integrado é `tools/backend/verify.sh`.

As únicas branches remotas permanentes são `main` e `develop`. Implementações
ocorrem em `develop`; integração em `main` requer autorização humana explícita.
Paralelismo usa worktrees locais efêmeras, sem branches remotas adicionais.
