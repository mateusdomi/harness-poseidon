# Padrões de produto — normas INVIOLÁVEIS de toda entrega da fábrica

**Autoridade:** dono (2026-08-08). **Escopo:** todo software construído pelo Poseidon, em
todo objetivo, sem exceção. Violação de qualquer item é reprovação de validação — não é
achado menor, é defeito básico.

## 1. Produto final, nunca MVP

A fábrica entrega PRODUTOS FINAIS. As palavras "MVP", "demonstração", "ambiente de
demonstração", "beta" e equivalentes NUNCA aparecem em interface, título, rodapé, e-mail ou
documento voltado ao usuário final. Versões internas existem no versionamento, não na tela.

## 2. Tela de login limpa

- NUNCA listar usuários de demonstração, credenciais, dicas de senha ou nomes de variáveis
  de ambiente na tela de login (ou em qualquer tela).
- Mensagens de erro de autenticação não revelam se o usuário existe.
- A política de senha exibida ao usuário é a MESMA que o backend aplica (uma fonte).

## 3. Identidade do produto

- Favicon, título da aba, logos e metadados são DO PRODUTO. Marca de ferramenta usada na
  construção (Lovable, template, boilerplate) NUNCA aparece — auditar `index.html`,
  manifestos, favicons e assets herdados de scaffolds.

## 4. Primeiro acesso

- TODO sistema exige troca de senha no primeiro login de qualquer usuário semeado. A
  credencial inicial é descartável por construção — só assim ela pode ser comunicada ao
  operador pelo canal do Poseidon.

## 5. Auto-verificação visual

- Todo produto com interface entrega suíte E2E de navegador (Playwright) cobrindo no
  mínimo: login (incluindo o fluxo de troca de senha do 1º acesso), a jornada núcleo de
  cada perfil e a ausência de erros de console nas telas percorridas.
- Essas jornadas são executáveis pelos gates da plataforma (`Gates: e2e`) — o produto que
  não pode se provar navegando não está entregue.

## 6. Acesso comunicado pelo Poseidon

- O produto mantém `docs/ACESSO.md` (URLs, perfis, onde vive a credencial inicial). O
  pacote de acesso da Bruna entrega URL + credencial inicial NO CHAT do projeto — válido
  porque o item 4 torna a credencial inicial descartável.

> Estes padrões entram no contexto das missões de produto V3. O validador de produto os cobra
> item a item; a homologação humana não deveria encontrar violação de nenhum deles.
