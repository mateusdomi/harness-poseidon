# Padrões de dados — Oracle

**Escopo:** produto entregue · **Seleção:** projetos cujo perfil efetivo declara `Data.Database = Oracle`

> Este documento **não substitui** [`data-standards.md`](data-standards.md). O banco padrão do
> baseline continua sendo SQL Server; Oracle é **override de projeto**, e vale quando o perfil
> efetivo registrar o override com autoridade adequada. Onde este documento é silencioso, o padrão
> geral vale.

## 0. O que foi verificado nesta máquina, e o que não foi

Um spike descartável rodou contra Oracle real em 2026-08-04. Ele **não** é o produto e não vira
código de produção — existe para que nenhum projeto descubra incompatibilidade de provider no meio
de uma entrega.

| Componente | Versão comprovada |
|---|---|
| SDK / runtime | .NET 8 |
| EF Core | 8.0.11 |
| Provider | `Oracle.EntityFrameworkCore` **8.23.60** |
| Servidor exercitado | Oracle Free (container `gvenzl/oracle-free:slim`, arm64) |

Provas que passaram: conexão, criação de schema pelo provider, insert, query com leitura fiel,
update, concorrência otimista, violação de UNIQUE, transação com rollback efetivo, delete.

**Divergência declarada, e é importante:** o projeto Prisma declara **Oracle 19c**, e não existe
imagem 19c para ARM64 — o registro oficial da Oracle exige credencial e publica 19c apenas para
x86_64. O que roda nesta máquina é Oracle Free em arm64. A mitigação está no provider:

```csharp
options.UseOracle(cs, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19));
```

Com isso o provider **gera SQL compatível com 19c** mesmo executando contra servidor mais novo.
Isto reduz o risco; não o elimina. O que só o 19c real prova continua não provado — e essa distinção
precisa aparecer na evidência, não no otimismo.

## 1. Provider e configuração

- `Oracle.EntityFrameworkCore` na linha **8.x** para EF Core 8. Não misture linhas: provider 9.x
  com EF 8 falha em tempo de execução, não de compilação, que é o pior momento para descobrir.
- **Sempre** declare `UseOracleSQLCompatibility` quando o alvo de produção for 19c.
- Pooling ligado (padrão). `Min Pool Size` só se houver medição que o justifique.
- `Command Timeout` explícito por contexto — o padrão do provider não é o mesmo do SQL Server, e
  herdar sem declarar produz timeout onde ninguém espera.

## 2. Schema, nomes e identidade

- **Schema = usuário.** Um usuário exclusivo por aplicação, com privilégio mínimo: `CREATE SESSION`,
  `CREATE TABLE`, `CREATE SEQUENCE`, `CREATE VIEW` e cota no tablespace. Nada de `DBA`.
- Identificadores acima de 30 caracteres só existem a partir do 12.2; em 19c o teto é **128**. Ainda
  assim, nomes longos gerados por convenção (índices, FKs) estouram com facilidade — nomeie
  explicitamente índices e constraints em vez de deixar o gerador escolher.
- Oracle **dobra maiúsculas** por padrão. O provider gera identificadores entre aspas, o que os torna
  case-sensitive. Escolha um caso e mantenha; misturar produz `ORA-00942` em consultas escritas à mão.

## 3. Mapeamentos que exigem cuidado

| Tipo .NET | Coluna | Observação |
|---|---|---|
| `Guid` | `RAW(16)` | mapeado pelo provider; **não** compare com literal string em SQL manual |
| `bool` | `NUMBER(1)` | 19c não tem tipo booleano nativo (só a partir do 23ai). Verificado: ida e volta fiel |
| `DateTime` | `TIMESTAMP(7)` | sem fuso. Verificado fiel |
| `DateTimeOffset` | `TIMESTAMP(7) WITH TIME ZONE` | verificado com `-03:00` preservado |
| `decimal` | `NUMBER(p,s)` | **declare precisão e escala**. `NUMBER` sem escala vira ponto flutuante e arredonda dinheiro |
| `string` longa | `NVARCHAR2(2000)` até o limite, depois `CLOB` | verificado com 5.000 caracteres |
| `byte[]` | `BLOB` | |
| `[Timestamp]` | `RAW(8)` gerado | concorrência otimista verificada: lança `DbUpdateConcurrencyException` |

**Fuso horário.** O produto opera em `America/Sao_Paulo`. Guarde instantes em UTC e converta na
borda; `TIMESTAMP WITH LOCAL TIME ZONE` depende da sessão e produz resultado diferente conforme quem
consulta.

## 4. Migrations

- Migrations versionadas em Git, aplicadas por `dotnet ef database update` ou por script gerado.
  `EnsureCreated` é aceitável **apenas** em spike e teste; nunca no caminho de release.
- Oracle **não tem DDL transacional**: uma migration que falha no meio deixa objetos criados. Escreva
  cada migration para ser idempotente ou para falhar no primeiro passo.
- Sequences: prefira `IDENTITY` (12c+) a sequence manual. Se usar sequence, declare `CACHE` e aceite
  buracos — pedir numeração contígua custa serialização.

## 5. Consulta

- `AsNoTracking()` em toda leitura que não vai gravar.
- Projete para DTO (`Select`) em vez de materializar entidade inteira para descartar campos.
- **Paginação**: `OFFSET n ROWS FETCH NEXT m ROWS ONLY` (12c+). `ROWNUM` só em código legado.
- N+1 é o defeito mais comum: use `Include` deliberado ou projeção, e verifique o SQL gerado nas
  consultas do caminho quente.
- Analise plano (`EXPLAIN PLAN` / `DBMS_XPLAN`) das consultas de listagem antes do release.

## 6. Transação, concorrência e retentativa

- Fronteira de transação = caso de uso. Uma transação por requisição, aberta o mais tarde possível.
- Ordene o acesso a tabelas de forma consistente entre casos de uso: deadlock em Oracle
  (`ORA-00060`) quase sempre é ordem de bloqueio divergente.
- Retentativa **limitada** (3) e só para erro transitório declarado; nunca laço infinito.
- Operação repetível precisa ser idempotente por chave de negócio, não por sorte.
- Trilha de auditoria é **append-only**: sem `UPDATE`, sem `DELETE`, e a garantia é de schema
  (`CHECK`/trigger ou privilégio), não de disciplina do programador.

## 7. Segurança

- Usuário da aplicação com privilégio mínimo; DDL de migration pode usar usuário separado.
- Cadeia de conexão **nunca** em Git. Secret local, variável de ambiente ou cofre.
- Sem SQL concatenado. Parâmetro sempre — o provider faz bind, e concatenar reintroduz injeção num
  lugar onde ela já estava resolvida.
- Log nunca registra cadeia de conexão, senha nem valor de parâmetro sensível.

## 8. Verificação

Um projeto Oracle é verificado pelos **mesmos** mecanismos de qualquer outro: nenhum verificador
escolhe caminho por fornecedor de banco. A prova de persistência escreve pela API da própria
aplicação, derruba o processo e lê de novo — o que vale para SQL Server vale aqui.

A trava de banco de produção reconhece as formas locais do Oracle (`host:porta/serviço` e descritor
TNS com `HOST=`). Conexão que não seja comprovadamente local interrompe a verificação com
`NotSupported`, e isso é deliberado.
