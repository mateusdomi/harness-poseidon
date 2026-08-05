# Padrões de autenticação e sessão — aplicação web própria

**Escopo:** produto entregue · **Seleção:** produtos Web que exigem autenticação e **não** usam SSO
corporativo

> Complementa [`security-and-operability.md`](security-and-operability.md). Onde este documento é
> silencioso, o padrão geral vale.

## 1. A escolha padrão, e por que ela

Para SPA servida na **mesma origem** que a API, o padrão é:

**ASP.NET Core Identity + autenticação por cookie.**

Não é a opção mais moderna do mercado; é a que tem menos superfície para errar. JWT em SPA same-origin
troca um cookie que o navegador já sabe proteger por um token que a aplicação precisa guardar em
algum lugar — e todo lugar disponível no navegador é pior que o cookie. Só se afaste deste padrão
com restrição real declarada (cliente móvel nativo, terceiro consumindo a API, SSO exigido).

**Não invente o que o requisito não pediu.** Recuperação de senha por e-mail, MFA, federação e SSO
são funcionalidades com custo e risco próprios; entram quando alguém as pede, não por completude.

## 2. Senha

- Hashing **gerenciado pelo framework** (`PasswordHasher<TUser>` do Identity). Nunca implemente
  derivação própria, nunca use SHA sozinho, nunca "salt caseiro".
- Política mínima: comprimento ≥ 8, e a validação declarada em `IdentityOptions` — não espalhada em
  regex pelo código.
- **Bloqueio por tentativa**: `Lockout` ligado, com janela e limite explícitos. Sem isso, força bruta
  é uma questão de paciência.
- Limite de taxa no endpoint de login, além do bloqueio por conta — um atacante que varia contas não
  aciona lockout nenhum.
- Provisionamento inicial de usuário é operação administrativa, com senha trocada no primeiro acesso
  quando o requisito pedir.

## 3. Sessão

- Cookie **`HttpOnly`** — script da página não lê o cookie de autenticação. Não negociável.
- **`Secure`** em qualquer ambiente que não seja `localhost`.
- **`SameSite=Lax`** por padrão; `Strict` quando não houver fluxo de retorno externo. `None` só com
  `Secure` e motivo escrito.
- Expiração explícita, com renovação deslizante quando o requisito de uso justificar.
- **Logout invalida a sessão no servidor**, não apenas apaga o cookie no cliente.
- **Antiforgery/CSRF** obrigatório em toda mutação autenticada por cookie. É o preço do cookie, e é
  barato: o ASP.NET Core já traz o mecanismo.

## 4. O que nunca vai para o frontend

- Segredo, chave, cadeia de conexão ou credencial de serviço — nenhum, em nenhuma forma, nem em
  variável de build.
- Token de autenticação em `localStorage` ou `sessionStorage` por padrão. Se um requisito real exigir
  token, ele precisa de decisão registrada explicando por que o cookie não serve.
- Hash de senha, mesmo truncado, em resposta de API.

## 5. Autorização

- **Deny by default**: o pipeline exige autenticação, e o que é público se declara explicitamente.
  O inverso — proteger caso a caso — falha por esquecimento, e o esquecimento não aparece em teste.
- Autorização é decidida no **backend**. Esconder botão no frontend é experiência de uso, nunca
  controle de acesso.
- RBAC por papel quando o domínio tem papéis; verificação de **escopo do recurso** quando o dado
  pertence a alguém — "é gerente" não responde "é gerente *deste* risco".
- Cada endpoint declara sua exigência. Herança implícita de política é onde o furo mora.

## 6. Log e observabilidade

- Nunca registre senha, hash, cookie, token ou cabeçalho `Authorization`.
- Registre **evento** de autenticação: sucesso, falha, bloqueio, logout — com identificador de
  usuário e instante, sem credencial.
- Falha de login devolve mensagem **genérica** ao usuário ("credenciais inválidas"); a distinção
  entre "usuário não existe" e "senha errada" é enumeração de contas.

## 7. Verificação

- A jornada de autenticação entra no E2E: login, acesso a recurso protegido, logout, e acesso negado
  **depois** do logout.
- Requisição não autenticada a endpoint protegido devolve 401/403 — provado por teste, não assumido.
- A varredura de segredo do Poseidon já reprova credencial em código; ela não substitui revisão de
  onde o segredo é lido em runtime.
