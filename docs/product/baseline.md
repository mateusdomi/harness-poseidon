# Baseline técnico do produto entregue

Aplica-se ao **software que o Poseidon constrói para o usuário**. As regras do próprio
repositório estão em `governance/`. O detalhe por disciplina está em
`docs/product/backend-standards.md`, `frontend-standards.md`, `data-standards.md` e
`security-and-operability.md`.

Este documento tem duas metades: o **default global** e a **resolução local** dele para
um projeto — o perfil efetivo.

---

# Parte 1 — o default global

Quando o usuário não especifica tecnologia, aplica-se a stack abaixo. Ele não precisa
conhecer framework, banco, arquitetura ou padrão de engenharia para receber um produto
funcional.

## Inferência de modalidade

Antes da stack vem a pergunta que a antecede: **que tipo de coisa foi pedido?**

| O usuário pediu | Modalidade default | Interface obrigatória? |
|---|---|---|
| Sistema, aplicação, portal, gestão, cadastro, controle, painel | Web | **Sim** |
| "Somente API", "backend only", integração máquina-a-máquina | API | Não — mas OpenAPI é obrigatório |
| Worker, serviço, rotina, job, automação | Serviço | Não — mas operação e observabilidade são |
| Executável, desktop, EXE | Desktop | **Sim** |
| Aplicativo, app | Mobile (+ backend quando necessário) | **Sim** |
| Biblioteca, pacote, SDK | Biblioteca | Não — mas API documentada e exemplos são |

**Ausência de stack informada nunca autoriza omitir parte do produto.** Modalidade
ambígua é resolvida com o usuário na Triagem ou na Descoberta e registrada no perfil
efetivo — nunca pelo executor no meio da implementação, e nunca entregando menos.

## Backend

.NET 8 (`net8.0`) e ASP.NET Core 8, em C#; Nullable Reference Types habilitado; injeção
de dependência nativa; configuração por `appsettings` mais variáveis de ambiente ou
secret store; REST quando houver HTTP; **OpenAPI obrigatório** para toda API HTTP, com
Swagger UI navegável em desenvolvimento e homologação; EF Core 8 como ORM transacional
padrão; Dapper permitido para consultas com justificativa de desempenho ou SQL
especializado.

## Frontend

Quando o produto tem interação humana por navegador e o usuário não pediu apenas API,
worker, biblioteca ou CLI: React com TypeScript, build com Vite; roteamento explícito;
camada dedicada de acesso à API com contratos tipados; tratamento consistente de
carregamento, estado vazio e erro; responsividade; acessibilidade WCAG 2.2 AA como
objetivo mínimo. Design system da organização, quando existir, tem precedência. Versões
fixadas: build reprodutível não usa `latest` implícito.

## Dados

SQL Server como banco relacional padrão, único; migrations versionadas; schema
reproduzível a partir do repositório; constraints de integridade no banco; índices
justificados pelas consultas reais; script destrutivo exige autorização explícita.

## Arquitetura

Monólito modular; Clean Architecture como regra de dependência
(`Presentation → Application → Domain`, com Infrastructure implementando as portas
internas); SOLID e Clean Code aplicados pelo efeito, não pela cerimônia; DDD pragmático
proporcional à complexidade do domínio.

Microsserviços, Kafka, Redis, Elasticsearch, Kubernetes e equivalentes **não são
defaults**: cada um exige necessidade demonstrável e ADR.

## O que não é baseline

Boa prática vira má arquitetura quando aplicada sem problema a resolver. **Não** são
obrigatórios e só entram com justificativa: interface para toda classe; CQRS em CRUD;
MediatR como requisito arquitetural; `IGenericRepository<T>` universal (o EF Core já
oferece Repository e Unit of Work); cache distribuído, mensageria, filas ou streaming
sem carga que os exija; microsserviços ou orquestração de contêineres em produto que
cabe num processo.

---

# Parte 2 — o perfil efetivo do projeto

O perfil efetivo é o **constraint profile** que o gate da Fase 3 já cobra (`aderência ao
constraint profile — desvio exige ADR`) e que não tinha definição em documento, schema
ou código.

Ele é a resposta única e resolvida sobre o que vale neste projeto: o que foi herdado do
default acima, o que foi sobrescrito e por quê. Existe para que nenhum agente
redescubra o projeto ao pegar um card.

## Precedência

```
restrição legal/regulatória/segurança  >  ADR aprovado  >  requisito explícito do usuário
   >  restrição da organização/tenant  >  o default global desta página
```

Mesma precedência normativa de `governance/core.md`, aplicada ao produto: a camada mais
alta que se pronuncia sobre um tema vence.

**Preferência do agente não está nessa lista.** Divergência exige causa declarada —
requisito, NFR, restrição, compatibilidade ou necessidade operacional — e vira ADR.

## Override

O usuário pode informar outra stack até o fechamento da Fase 3. *"Frontend em Angular"*
→ Angular passa a valer e React deixa de valer neste projeto; *"banco PostgreSQL"* →
PostgreSQL passa a valer; legado corporativo → a compatibilidade vence o default. Os
demais defaults continuam vigentes.

1. Registrar no perfil efetivo, com motivo.
2. Abrir ADR quando o impacto for arquitetural relevante.
3. **O agente não tenta "corrigir" a escolha de volta para o default.** Escolha
   explícita do usuário é decisão, não erro.

## Conteúdo mínimo do perfil

1. Identificação — projeto, versão do perfil, data, responsável, status.
2. Modalidade do produto e a justificativa; se há interface e qual.
3. Stack efetiva — backend, frontend (ou a declaração explícita de que não há), dados,
   autenticação, infraestrutura e operação.
4. Defaults herdados.
5. Overrides — área, default, override, motivo e ADR. **Override sem ADR é desvio não
   rastreável e reprova o gate da Fase 3.**
6. Restrições específicas — regulatórias, da organização, de legado, de plataforma, de
   licenciamento, de integração.
7. Documentos locais ativos e ADRs ativos.
8. DoD específica — somente o que se soma ao DoD global.
9. Riscos técnicos — risco, impacto, mitigação.
10. Última resolução — quais fontes geraram o perfil e qual precedência foi aplicada,
    para que a resolução seja auditável e não precise ser refeita de cabeça.

## Ciclo de vida e não duplicação

Artefato da **Fase 3**, conduzido pelo arquiteto; entrada obrigatória das fases
seguintes; revisado sempre que um ADR alterar stack, arquitetura, persistência,
autenticação ou modalidade. Perfil desatualizado é pior que ausente: tem aparência de
verdade.

Um projeto **não copia** `docs/product/` para dentro de si — a cópia diverge do original
na primeira evolução. O projeto declara apenas o que é dele e herda o resto por
referência.
