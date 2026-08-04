# Frontend do produto entregue

Aplica-se ao software construído para o usuário. O painel do próprio Poseidon é regido
pelas regras do repositório, em `governance/`.

## Quando o frontend é obrigatório

Se a solução se destina a **uma pessoa operar** e o usuário não pediu explicitamente
somente API, worker, biblioteca ou CLI, o frontend é parte do produto.

**Ausência de stack informada não autoriza omitir o frontend.** Quem escreve "quero um
sistema de empréstimos" não está pedindo um endpoint. Um `GET /emprestimos` devolvendo
`{"emprestimos": []}` é uma peça do produto, não o produto.

A modalidade e sua justificativa ficam no perfil efetivo do projeto.

## Stack default

React, TypeScript e Vite; roteamento explícito; cliente HTTP centralizado; tratamento
padronizado de erro; biblioteca de componentes ou design system existente quando houver
— **ele tem precedência** sobre este default; gerenciamento de estado somente quando
necessário. Versões fixadas no projeto: build reprodutível não usa `latest` implícito.

## TypeScript

Evitar `any`, cast em cascata para calar o compilador e tipo duplicado sem necessidade.
Quando a entrada é realmente desconhecida, `unknown` com narrowing é preferível a `any`.

## Organização

Separar, no mínimo: componentes de UI; páginas ou features; serviços de API; hooks;
modelos e tipos; preocupações transversais. A estrutura acompanha a escala do produto —
não se cria árvore de pastas para um formulário.

## Experiência mínima obrigatória

Toda operação assíncrona relevante tem estado de carregamento; confirmação de sucesso
quando fizer diferença; **estado vazio com orientação** — lista vazia sem dizer o que
fazer é experiência incompleta; estado de erro compreensível; caminho de recuperação
quando aplicável.

```
Sistema de Empréstimos                    [ Novo empréstimo ]

Empréstimos
────────────────────────────────────────────────────────────
Nenhum empréstimo cadastrado.
Cadastre o primeiro empréstimo para começar.

                                      [ Cadastrar empréstimo ]
```

## Formulários

Labels explícitos; mensagem de validação próxima do campo; prevenção de envio
duplicado; estado de submissão visível; erros do backend tratados e exibidos; navegação
por teclado funcional.

## Acessibilidade

Objetivo mínimo WCAG 2.2 AA: contraste adequado; foco visível; informação nunca
transmitida só por cor; HTML semântico; navegação por teclado; `aria-*` apenas quando a
semântica nativa não resolver.

## Segurança

O frontend **nunca** é fronteira confiável. Não colocar segredo no bundle; não confiar
em papel enviado pelo navegador; não montar HTML não confiável sem sanitização; não
persistir token sensível sem estratégia aprovada. Toda autorização é decidida no
servidor.

## Integração

Contratos de API tipados; preferir geração ou compartilhamento controlado do contrato
quando trouxer consistência real. Mudança incompatível entre backend e frontend deve
**falhar em teste ou build antes da entrega**.

Integração real é requisito de conclusão: tela alimentada por mock, quando a entrega
pressupõe persistência, não satisfaz o [Definition of Done](definition-of-done.md).

## Design

Sem design system: layout limpo, responsivo, hierarquia visual clara, consistência de
componentes, feedback de interação e estados vazios úteis.

"Funciona no endpoint" não é critério de conclusão de experiência do usuário.

## Relacionado

- [`baseline.md`](baseline.md) · [`definition-of-done.md`](definition-of-done.md) · [`backend-standards.md`](backend-standards.md)
