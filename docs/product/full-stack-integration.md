# Integração full-stack — o que separa peças de produto

**Escopo:** produto entregue · **Seleção:** produtos Web com interface e API

> Complementa [`definition-of-done.md`](definition-of-done.md). O DoD diz *quais evidências* a
> entrega precisa; este documento diz *o que integração significa* quando a modalidade tem duas
> metades.

## 1. A regra

**Componentes que existem independentemente não constituem produto integrado.**

Um backend que compila, uma interface que compila e um banco que aceita migration são três peças.
Produto é o que acontece quando elas se encontram — e esse encontro precisa ser provado, não
presumido.

A cadeia que precisa fechar, inteira:

```
Navegador → Interface → HTTP → API → Aplicação → Domínio → Persistência → Banco
                                                                            ↓
Interface ← resposta ← API ← leitura de volta ←────────────────────────────┘
```

Uma entrega em que o dado sai da tela e volta para a tela tendo passado pelo banco está integrada.
Qualquer coisa menor que isso é peça.

## 2. O que precisa estar decidido e verificável

| Item | Exigência |
|---|---|
| **Configuração da base da API** | a interface lê a URL da API de configuração, não de literal no código |
| **Contrato** | o que a interface consome corresponde ao que a API publica; divergência é defeito, não detalhe |
| **CORS** | resolvido para desenvolvimento e declarado para produção; mesma origem é preferível |
| **Autenticação** | a sessão atravessa a cadeia — a tela autenticada chama a API autenticada |
| **Erros** | falha da API vira mensagem compreensível na tela, nunca tela em branco nem stack no navegador |
| **Carregamento** | toda espera tem estado visível; tela congelada é defeito |
| **Estado vazio** | lista sem item explica o que fazer, não mostra vazio silencioso |
| **Migrations** | o schema é criado por migration versionada, do zero, sem passo manual |
| **Saúde/prontidão** | a API responde a uma rota de prontidão antes de ser considerada no ar |

## 3. A prova

A integração é provada por **jornada executada**, não por inspeção:

- backend e interface **no ar**, ambos vivos do começo ao fim da jornada;
- navegador percorrendo o caminho principal do usuário;
- tráfego da interface **chegando de fato** à API — medido pelo Poseidon, não deduzido;
- dado escrito pela jornada **sobrevivendo** e voltando na leitura.

Uma tela que renderiza dado embutido passa em build, passa em jornada e não está integrada. É por
isso que a medição de tráfego existe e é condição para derivar a evidência de integração.

## 4. O antipadrão que este documento existe para nomear

> "O backend está pronto e testado; a interface fica para depois."

Backend pronto sem interface, num produto cuja modalidade exige interface, **não é meia entrega — é
zero entrega**, porque quem pediu não consegue usar nada. A BUILD não fecha com a metade que dá
sentido ao produto pendente, e nenhuma decisão de arquitetura tem autoridade para transformar essa
pendência em escopo reduzido.
