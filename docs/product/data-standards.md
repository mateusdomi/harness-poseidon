# Dados e banco do produto entregue

Aplica-se ao software construído para o usuário. A persistência do próprio Poseidon
(SQLite e PostgreSQL, com paridade de migrations) é regida por
[`governance/rules/testing.md`](../../governance/rules/testing.md).

## Default

Banco relacional padrão: **SQL Server**, único, salvo override registrado no perfil
efetivo.

## Modelagem

Modelar a partir do domínio e dos casos de uso, **não da tela**. Cada tabela com chave
primária, tipos adequados, nulabilidade intencional, constraints relevantes e nomes
consistentes. Evitar a tabela genérica com dezenas de colunas opcionais servindo a
vários conceitos: ela transfere para a aplicação uma integridade que o banco deveria
garantir.

## Integridade

A integridade que o banco pode garantir fica no banco: `PRIMARY KEY`, `FOREIGN KEY`,
`UNIQUE`, `CHECK`, `NOT NULL`. Não depender exclusivamente da aplicação para impedir
dado estruturalmente inválido — a aplicação erra, é reescrita e roda em várias
instâncias; a constraint não.

## Tipos

Dinheiro em `decimal(p,s)` adequado ao domínio, **nunca `float`**. Texto com tamanho
definido quando o domínio conhece o limite — `nvarchar(max)` não é default. Datas com
precisão suficiente, em UTC ou offset conforme a estratégia declarada uma vez e seguida
em toda parte. Booleano em `bit`.

## Nomenclatura

Convenção única para tabelas, colunas, PK, FK, índices e constraints, escolhida no
início e mantida. Não misturar português e inglês sem regra preexistente no projeto.

## Índices

Todo índice tem motivo: filtros, joins, ordenação, seletividade, cobertura e custo de
escrita. Não criar índice por coluna "por precaução" — cada índice é custo em toda
gravação.

## Consultas

Parametrização obrigatória; **concatenação de input em SQL é proibida**; evitar
`SELECT *` em produção; limitar conjuntos grandes; analisar N+1 no ORM; medir antes de
otimização complexa.

## Transações e concorrência

A transação reflete a unidade real de consistência. Não manter transação aberta
enquanto se chama serviço remoto, salvo justificativa arquitetural registrada. Havendo
risco de *lost update*, avaliar `rowversion`/concorrência otimista ou outra estratégia
explícita — a escolha é declarada, não implícita.

## Migrations

Toda alteração de schema é versionada, revisável, reproduzível, com idempotência
considerada quando aplicável e plano de rollback ou forward-fix proporcional ao risco.
**Não alterar o banco manualmente e depois "ajustar a migration"**: o schema
reproduzível a partir do repositório é o que permite recriar o ambiente.

## Dados iniciais

Seed automático para dados de referência, desenvolvimento e demonstração controlada.
**Não inserir dado fictício em produção.**

## Auditoria

Quando o negócio exigir rastreabilidade: quem, quando, qual operação, qual entidade,
valor anterior e novo quando necessário, e a correlação. Auditoria de negócio não se
implementa com log textual informal — log é diagnóstico, não registro de negócio.

## Backup, restore e continuidade

O projeto documenta RPO, RTO, política de backup, retenção, **teste de restore** e
responsabilidade operacional. Backup nunca testado é suposição de backup.

## Dados pessoais e retenção

Dado pessoal tem finalidade declarada, minimização, prazo de retenção, controle de
acesso e estratégia de exclusão ou anonimização quando aplicável, conforme a LGPD e a
regulação específica do domínio. Volumetria esperada é estimada quando influencia
modelagem, índice ou infraestrutura.

## Relacionado

- [`backend-standards.md`](backend-standards.md) · [`security-and-operability.md`](security-and-operability.md) · [`baseline.md`](baseline.md)
