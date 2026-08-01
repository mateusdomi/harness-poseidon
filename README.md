# Poseidon

Poseidon é uma fábrica autônoma de software com plano de controle .NET, execução
durável de agentes e persistência compatível com modo pessoal e modo servidor.

As regras do repositório começam em `governance/core.md`. O catálogo e a política
de carga de contexto estão em `governance/manifest.yaml`.

## Pré-requisito: Docker

O Poseidon **exige Docker** nos dois modos. Não é recomendação: é a fronteira que separa o agente
do seu computador. Sem contêiner, o produto recusa executar agentes — não existe aceite de risco
que dispense a contenção, porque uma exceção "temporária" aceita pelo produto vira permanente na
prática.

Checklist do operador antes do primeiro uso:

- [ ] Docker instalado (Docker Desktop no macOS/Windows; Docker Engine no Linux).
- [ ] Daemon **em execução** — `docker version` precisa responder a versão do *Server*, não só a do
      cliente. O cliente responde mesmo com o daemon parado, e é o servidor que cria o contêiner.
- [ ] `./poseidon doctor` verde no item de runtime de contêiner.

Se faltar, o launcher, o `doctor` e a API respondem a mesma frase, com o que fazer:

> Preciso do Docker para trabalhar com segurança — instale ou inicie o Docker e me chame de novo.

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
