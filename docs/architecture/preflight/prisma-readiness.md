# Prisma — análise de prontidão do runtime

**Documento analisado:** *Prisma — Especificação Técnica do MVP*, v3.0 (build-ready), 04/08/2026
**Analisado em:** 2026-08-04 · **SHA do Poseidon:** `7ba9a502`
**Escopo desta análise:** o que o RUNTIME do Poseidon consegue representar e verificar.
**Fora de escopo:** julgar a especificação. Ela é fonte da verdade do produto; aqui só se pergunta
se a plataforma consegue conduzi-la sem obstruir.

> Nenhum projeto Prisma foi criado. Nada foi despachado. Nenhuma quota de agente foi consumida.

## 1. O que a especificação exige, e o que o runtime faz com isso

| Exigência da spec | Runtime consegue representar? | Como |
|---|---|---|
| **Documento como fonte única de verdade** | 🟡 **parcial** | O canon `provided-artifacts` manda validar/normalizar/rastrear/preencher lacunas em vez de reentrevistar. É norma que o agente lê — **não há mecanismo que impeça** reescrever o que a fonte já responde. Risco de cota, não de corretude |
| **26 critérios de aceite (§16)** | ✅ | `demands.acceptance_criteria_json` carrega critérios; o read model de cobertura liga requisito → card → implementação → evidência, e requisito órfão segura a fase |
| **5 perfis com escopo** | ✅ | São regra de domínio do produto. O canon de autenticação exige RBAC + escopo de recurso decidido no backend e deny-by-default — exatamente o que §7.2/§7.3 pedem |
| **Cadeia de aprovação (§8)** | ✅ | Máquina de estados do produto; nada no runtime a obstrui |
| **Auditoria append-only sem IP (§11)** | ✅ | O canon Oracle exige append-only garantido por schema, não por disciplina; e o de autenticação proíbe registrar credencial |
| **Snapshot ITRC imutável (§6.3)** | ✅ | Idem: append-only por construção |
| **Fuso `America/Sao_Paulo`** | ✅ | O canon Oracle cobre `DateTimeOffset`, `TIMESTAMP WITH TIME ZONE` e adverte contra `WITH LOCAL TIME ZONE`. Verificado no spike com `-03:00` preservado |
| **Oracle 19c (override)** | ✅ com ressalva | Override reconhecido; plano de verificação sem `Unsupported`; spike 9/9. Ressalva de imagem ARM64 no §4 |
| **Protótipo React existente** | 🟡 **parcial** | Canon `provided-artifacts` proíbe substituir e manda integrar; **não há mecanismo** que detecte substituição silenciosa |
| **Autenticação própria, sem SSO** | ✅ | Canon `authentication-standards` decide Identity + cookie e proíbe inventar SSO/reset que ninguém pediu |
| **Sem integrações externas** | ✅ | O ambiente de verificação já roda offline por construção |
| **Prazo 20/08** | 🟡 | Criticidade por prazo é campo do projeto; **não verifiquei** que o escalonador prioriza por prazo |

## 2. Verificadores — nenhum é cego a Oracle

Auditei o registro canônico procurando dependência de fornecedor de banco. **Não há.**

| Verificador | Depende do banco? | Comportamento com Oracle |
|---|---|---|
| `dotnet-build` / `dotnet-test` | não | idêntico |
| `frontend-build` | não | usa o framework do perfil |
| `openapi-native` | não | sobe a API e busca o contrato |
| `persistence-native` | **não** | escreve pela API do produto, reinicia e lê — não conhece tabela nem fornecedor |
| `playwright-journey` | não | dirige o navegador |
| `security-baseline` | não | varre árvore e dependências |
| Inspetor de migrations | não | reconhece `*.sql` versionado **e** `*.Designer.cs` do EF Core |

A única menção a SQL Server em código é o **default do baseline** — que é o comportamento correto:
Oracle é override de projeto, e o padrão global não muda.

**Um bloqueador real foi encontrado e corrigido** nesta preparação: a trava que impede escrita em
banco de produção não reconhecia o EZ-connect do Oracle (`127.0.0.1:1521/FREEPDB1`) nem descritor
TNS. Um banco que **estava** na máquina seria julgado remoto e a verificação de persistência do
Prisma pararia com `NotSupported` — reprovando por motivo que não é do produto.

## 3. Perfil efetivo esperado

```
Modalidade   Web            (pessoa opera pelo navegador)
Backend      .NET 8         (baseline)
Frontend     React + TS     (baseline, e coincide com o protótipo fornecido)
Banco        Oracle         (override com autoridade de requisito do usuário)
API          REST + OpenAPI (baseline)
```

Plano derivado: **13 requisitos, 0 `Unsupported`.**

## 4. Riscos declarados, com o custo de cada um

| # | Risco | Severidade | O que fazer |
|---|---|---|---|
| R1 | **Oracle 19c não tem imagem ARM64.** Verificação local roda contra Oracle Free arm64, com o provider gerando SQL de compatibilidade 19c | **P1** | O que só o 19c real prova continua não provado. Se houver 19c acessível (x86 ou servidor da TrensRJ), rodar o spike contra ele antes do release |
| R2 | **Rich Intake sem mecanismo.** A fábrica pode reescrever o que a spec já responde | **P1** | Custa cota e produz documento divergente. Mitigação: o canon existe; vigiar o consumo nos primeiros cards |
| R3 | **Substituição silenciosa do protótipo.** Nada detecta que o React fornecido foi trocado por design próprio | **P1** | Vigiar o primeiro card de interface |
| R4 | **Prioridade por prazo não verificada** no escalonador | **P2** | Registrar o projeto como crítico por prazo na criação |
| R5 | **Jornada E2E com autenticação.** O verificador sobe tela e API e mede tráfego, mas quem escreve o login na spec de jornada é a entrega | **P2** | Previsto pelo desenho; sem ação prévia |

## 5. O que mudou desde o run de empréstimos, e por que importa aqui

O Prisma tem exatamente a forma do produto que falhou em 04/08: **Web, com tela que uma pessoa
opera**. As sete correções fechadas hoje batem uma a uma no que deu errado:

| O que aconteceu com empréstimos | O que acontece agora |
|---|---|
| Fase 5 fechou sem interface | Entrega Web sem interface **não fecha** — requisito órfão segura a fase |
| ADR removeu a interface do escopo | Decisão de arquitetura **não tem autoridade** para remover capacidade |
| Cards de interface cancelados, requisito órfão | Card cancelado **não cobre** requisito |
| Gap não virou trabalho | Cada lacuna vira **card derivado**, idempotente |
| 7 tentativas na mesma parede | Duas avaliações sem progresso **proíbem repetição cega** |
| Contrato/persistência/jornada por script | Quatro **verificadores nativos** |

Isso torna impossível repetir aquele fracasso. **Não garante que o Prisma saia bom** — garante que,
se sair ruim, o Poseidon sabe dizer onde e cria o trabalho para corrigir, sem depender de alguém ler
o diagnóstico e traduzir.

## 6. Veredito

```
PRISMA START = READY
```

Com três ressalvas declaradas — R1 (19c real não exercitado), R2 e R3 (Rich Intake e preservação do
protótipo são norma sem mecanismo). Nenhuma delas impede começar; todas custam vigilância nos
primeiros cards.
