# Backend do produto entregue — arquitetura, .NET/C# e API

Aplica-se ao software construído para o usuário. As regras de engenharia do próprio
repositório do Poseidon estão em `governance/rules/`.

## 1. Arquitetura

**Regra de dependência:** `Presentation → Application → Domain`; Infrastructure
implementa as portas de que as camadas internas precisam. Domain nunca referencia
Entity Framework, ASP.NET Core, mensageria, banco, filesystem, HTTP ou SDK externo.
Application não depende de Presentation.

Clean Architecture significa **controle de dependência**, não criar camadas sem
responsabilidade: uma camada que só repassa chamadas é indireção.

**SOLID pelo efeito.** SRP: responsabilidade coesa. OCP: regra nova entra sem espalhar
condicional. LSP: a abstração preserva o contrato comportamental. ISP: interface
pequena, orientada ao consumidor. DIP: Application e Domain não dependem de detalhe de
infraestrutura. SOLID **não** é "uma interface para cada classe" — interface sem
segundo implementador plausível e sem necessidade de teste é ruído.

**DDD proporcional.** Aplicar quando o domínio tem complexidade real: linguagem
ubíqua, entidades, value objects, aggregates com invariantes, domain services, domain
events, bounded contexts. Evitar aggregate gigante, entidade anêmica com toda a regra
em service, repositório genérico universal e DDD decorativo em CRUD trivial.

**Modularidade.** Sistema novo nasce monólito modular, salvo NFR que justifique
distribuição; cada módulo declara fronteira explícita, contratos claros, dependências
controladas e persistência isolável. Microsserviços exigem ADR justificado por escala
independente, ownership independente, disponibilidade distinta, implantação realmente
independente, isolamento regulatório ou topologia incompatível com monólito. "É mais
moderno" não é justificativa.

**Coesão.** Não há limite mecânico de linhas; há sinais que obrigam a refatorar: a
unidade mistura domínio, persistência e HTTP; tem múltiplos motivos de mudança; contém
blocos condicionais de responsabilidades diferentes; exige navegação extensa para
compreender uma única operação.

**Dependências externas.** SDK externo relevante é encapsulado atrás de porta ou
adaptador quando substituição, teste ou isolamento importarem. Regra de negócio não
fica acoplada a fornecedor.

## 2. .NET 8 e C#

**Configuração.** `net8.0`; Nullable Reference Types habilitado; warnings relevantes
tratados; analyzers conforme o baseline do repositório; `async`/`await` ponta a ponta
para I/O; `CancellationToken` propagado em operações canceláveis.

**`dynamic` — proibido** em código novo de Domain e Application: a resolução tardia
elimina a garantia de compilação e transfere o erro para runtime
(`usuario.MetodoQueNaoExiste()` compila). Permitido apenas em adaptador de
interoperabilidade cuja origem realmente não tem contrato estático, com justificativa,
validação de entrada, testes e isolamento.

**`ExpandoObject`** — mesma regra; nunca como modelo de domínio, DTO ou contrato
público. **`object` como sacola de dados** — proibido em contratos públicos e regras de
negócio.

**`record` — permitido, e não é o mesmo caso de `dynamic`.** Não transfere validação
para runtime e não deve ser banido pelo motivo errado.

| Uso | Política |
|---|---|
| DTO imutável, Command, Query, Value Object | permitido |
| Entidade persistida pelo EF Core, Aggregate Root | avaliar com cuidado |

A cautela em entidade e aggregate root é **semântica**: igualdade por valor conflita
com identidade, e imutabilidade conflita com ciclo de vida rastreado pelo ORM. Nunca
escolher `record` por preferência estética.

**Tuples** — permitidas localmente para resultados pequenos e óbvios; em contratos
públicos, fronteiras entre camadas, APIs e eventos persistidos, usar tipo nomeado.

**Null safety.** Evitar o null-forgiving `!`; quando usado, exige condição estrutural
comprovável. Não usar `?.` para esconder invariante quebrada. Validar entrada nas
fronteiras.

**Assincronia — proibido:** `.Result`; `.Wait()`; sync-over-async; `async void` fora de
event handler de UI ou framework; `Task.Run` para mascarar I/O assíncrono em request
ASP.NET.

**Exceções.** Exceção é condição excepcional; erro esperado de negócio prefere
resultado tipado ou erro de domínio, convertido na fronteira. Não capturar `Exception`
e ignorar; não usar exceção como fluxo normal; nunca devolver stack trace ao cliente.

**Dinheiro, tempo e identidade.** Dinheiro em `decimal`, nunca `float`/`double`, com
moeda explícita quando o domínio admitir mais de uma e regra de arredondamento
definida. Instantes em `DateTimeOffset`, UTC internamente, timezone explícito na
apresentação; evitar `DateTime.Now` espalhado e abstrair o relógio quando teste ou
regra dependerem de tempo. IDs em tipo estável e estratégia consistente.

**Reflection.** Somente com ganho claro em infraestrutura ou framework; evitar em hot
path; quando usada, cachear metadata e cobrir por testes.

**Serialização e EF Core.** DTO público com contrato explícito; **nunca serializar
entidade EF diretamente pela API** nem depender de lazy loading para montar resposta
HTTP. `AsNoTracking()` em leitura quando apropriado; evitar N+1; projetar para DTO
quando não for necessário materializar o aggregate inteiro; transação na unidade real
de consistência; migrations versionadas; `EnsureCreated()` não é mecanismo de produção.

**Segurança no código — proibido:** segredo hardcoded; SQL concatenado com input;
desabilitar validação de TLS; aceitar certificado inválido por default; criptografia
caseira; hash inseguro para senha; registrar senha, token, segredo ou dado pessoal
sensível sem mascaramento.

## 3. API HTTP

**Contrato é entregável.** Toda API HTTP nasce documentada: OpenAPI habilitado, Swagger
UI navegável em desenvolvimento e homologação, política explícita para produção. O
agente não espera o usuário pedir Swagger — uma API sem contrato navegável está
incompleta, e a geração sem erro é evidência exigida na conclusão da BUILD.

**REST.** Recursos e verbos com semântica (`GET /api/emprestimos`,
`GET /api/emprestimos/{id}`, `POST`, `PUT`/`PATCH`, `DELETE` quando a exclusão fizer
sentido no domínio). Não modelar tudo como `POST /ExecutarAcaoX`.

**Status.** `200` leitura ou alteração com corpo; `201` criação; `204` sem corpo; `400`
input inválido; `401` não autenticado; `403` não autorizado; `404` inexistente; `409`
conflito ou invariante; `422` quando a política do projeto distinguir erro semântico;
`500` somente falha não tratada.

**Erros.** Problem Details (`application/problem+json`) ou contrato equivalente
padronizado, com correlação suficiente para diagnóstico e **sem** stack trace, SQL,
segredo ou detalhe interno sensível.

**Validação, paginação, idempotência, versionamento.** Toda entrada externa validada na
fronteira, com a regra de domínio também protegida pelo domínio — **frontend não
substitui validação de backend**. Coleção potencialmente grande prevê paginação.
Operação sujeita a retry, pagamento, criação externa ou reprocessamento avalia chave de
idempotência. Não versionar antecipadamente; havendo consumidor independente, adotar
estratégia explícita e documentada.

**Autorização é server-side.** Esconder botão na UI não é autorização.

## Relacionado

- [`baseline.md`](baseline.md) · [`data-standards.md`](data-standards.md) · [`security-and-operability.md`](security-and-operability.md) · [`definition-of-done.md`](definition-of-done.md)
