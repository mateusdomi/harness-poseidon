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
- [ ] Imagem de execução dos agentes construída — uma vez por máquina:

      ```bash
      tools/backend/build-sandbox-image.sh
      ```

- [ ] `./poseidon doctor` verde no item de runtime de contêiner.

Se faltar o Docker, o launcher, o `doctor` e a API respondem a mesma frase, com o que fazer:

> Preciso do Docker para trabalhar com segurança — instale ou inicie o Docker e me chame de novo.

Se o Docker estiver de pé mas a imagem faltar, a frase é outra — porque o estado é outro, e
"Docker disponível" não é o mesmo que "posso executar":

> O Docker está funcionando, mas a imagem de execução dos agentes ainda não existe nesta máquina —
> sem ela eu não consigo trabalhar em ambiente isolado.

### O que a imagem contém, e o que não contém

Contém os executores que o produto sabe acionar: **Claude Code** (que serve às contas Anthropic e
também às GLM, que falam o mesmo protocolo) e **Codex CLI**, ambos em versão fixa — `latest` faria
a mesma imagem se comportar de forma diferente em dias diferentes, e nenhuma execução seria
reproduzível.

**Não contém credencial nenhuma.** Token em camada de imagem fica no histórico para sempre: quem
tem a imagem tem o segredo, ainda que a camada seguinte o apague. A autenticação de cada conta é
montada em tempo de execução, somente leitura, a partir do config home isolado que o Poseidon
mantém por alias no host. Uma conta nunca enxerga o perfil da outra, e revogar acesso é apagar um
diretório — não reconstruir imagem.

Modelos locais e novos provedores entram acrescentando o CLI ao `infra/sandbox/agent/Dockerfile` e
reconstruindo.

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
