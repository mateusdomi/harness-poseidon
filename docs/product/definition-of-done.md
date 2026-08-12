# Definition of Done do produto entregue

Aplica-se ao software construído para o usuário. Os gates do próprio repositório do
Poseidon estão em `governance/rules/testing.md`.

## A regra

**"Código gerado" não é "sistema entregue".** Build verde e endpoint respondendo `200`
não provam que o usuário consegue atingir o objetivo que descreveu.

Caso real que motivou esta regra: um pedido de "sistema de empréstimos" resultou em
`GET /emprestimos` devolvendo `{"emprestimos": []}` e nenhuma interface, com a
justificativa de que o usuário não informou a stack. Para quem pediu, nada foi entregue.

## Regra de bloqueio

Se o produto é operado por uma pessoa e **não existe interface utilizável**, a BUILD não
pode ser considerada concluída — salvo escopo aprovado dizendo explicitamente "somente API",
"backend only" ou equivalente, registrado no perfil efetivo. Nesse caso, a validação deve
tratar como critério de aceite não atendido, não preferência de estilo.

## Mínimo por modalidade

A modalidade é inferida conforme `docs/product/baseline.md` e registrada no perfil
efetivo. Além do DoD comum abaixo, cada uma exige:

- **Web / sistema / portal / gestão** — frontend utilizável, backend, API documentada,
  persistência e banco reproduzível.
- **API only** (pedido explícito) — OpenAPI navegável, exemplos, persistência e regras
  funcionando.
- **Worker / serviço** — operação, configuração, logging, health, retry, instalação e
  observabilidade.
- **Desktop / EXE** — executável ou instalável, com fluxo funcional.
- **Mobile** — app mais backend quando necessário, não apenas endpoints.
- **Biblioteca** — API pública documentada, exemplos de uso e testes.

## Funcional

Requisitos do MVP implementados; jornada principal funcionando ponta a ponta; estados
vazios orientam o usuário; mensagens de erro compreensíveis por quem não escreveu o
sistema; dados persistem quando se espera que persistam; regras de negócio protegidas no
domínio, não só na tela; **o usuário consegue atingir o objetivo que descreveu**.

## Técnico

Build reprodutível; testes relevantes passam; lint e analyzers passam; migrations válidas
e banco inicializável do zero; OpenAPI gerado sem erro quando há API; frontend builda
quando existe; configuração externalizada e nenhum segredo commitado; dependências
documentadas e fixadas.

## Integração

O frontend consome o backend **de verdade** — nenhuma camada permanece mock quando a
entrega pressupõe integração real; contratos compatíveis, com incompatibilidade falhando
em teste ou build; CORS, autenticação e configuração corretos para o ambiente alvo.

## Operacional

README ou runbook com: como executar, portas, variáveis necessárias, health check quando
aplicável, onde estão os logs e qual a estratégia de banco.

Para produto com interface, a passagem para homologação humana exige mais do que código validado:
ao final da validação o ambiente local/controlado deve estar acessível, com URL e health
registrados, instruções de start/stop/status disponíveis e credenciais de teste tratadas como
`TEST_ONLY`. A entrega não deve depender de o humano abrir o repositório e descobrir manualmente
qual script ou porta usar.

## Testes que sustentam o DoD

Teste existe para impedir regressão e provar comportamento, não para produzir percentual
cosmético. Maioria unitária (invariantes, regras de domínio, casos de borda, cálculos);
integração nas fronteiras reais (banco, mapeamento do ORM, API, serialização,
autenticação); contrato quando frontend e backend evoluem separadamente ou há consumidor
externo; **E2E das jornadas principais em todo produto com interface**.

Jornada mínima de um sistema de empréstimos:

```
abrir a aplicação → ver a lista (vazia, com orientação) → cadastrar um empréstimo
   → persistir → ver o registro na lista → consultar os detalhes
```

`GET /emprestimos → 200` **não** substitui esse teste: prova que o processo subiu, não
que o produto funciona.

Cobertura é indicador, não objetivo; trecho sem teste precisa ter justificativa objetiva, não
ser escondido por exclusão artificial. Teste instável é corrigido ou colocado em quarentena
com dívida técnica registrada — nunca reexecutado até passar e tratado como sucesso.

## Quality gate mínimo da BUILD

Build sem erro; testes relevantes verdes; lint e analyzers verdes; migration validada
quando existir; OpenAPI gerado sem erro quando houver API; frontend buildando quando
existir; fluxo principal validado; nenhuma falha crítica de segurança conhecida;
evidências registradas no relatório da missão.

## Evidências exigidas

Arquivos principais alterados; comandos e builds executados; resumo dos testes; endpoints
e URL do contrato; evidência de interface quando aplicável; migration aplicada; limitações
conhecidas; decisões e ADRs relevantes. Evidência ausente mantém o gate fechado — é o
Default-FAIL do núcleo aplicado ao produto.

Na validação final, totais como "244 checks, 0 FAIL" não bastam. Cada critério de aceite e cada
item de checklist obrigatório precisa ter status individual e referência de evidência proporcional
(browser, teste, build, API, banco, runtime ou inspeção). `N/A` exige razão explícita.

## Mínimo aceitável de um MVP web genérico

Tela inicial · listagem · estado vazio orientado · cadastro · consulta · validação exibida
na interface · persistência em banco · API com OpenAPI navegável · migrations · testes
essenciais incluindo a jornada principal · instruções de execução · execução demonstrada.
