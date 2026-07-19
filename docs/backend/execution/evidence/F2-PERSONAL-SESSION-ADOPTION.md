# Evidência — adoção de sessão no modo pessoal (desbloqueio da homologação GNG-3)

Data: 2026-07-19.

## Defeito

Na homologação, nenhuma tela carregava em `http://127.0.0.1:5090/cockpit`. Console:
`GET /api/v1/profiles/current` → 404 (`ApiError: The local profile does not exist.`) seguido de
cascata de 401 em `projects`, `notifications`, `budgets`, `audit-events`.

Causa raiz: no modo pessoal a sessão vinha **apenas** do cookie `harness.profile`, gravado somente
no momento da **criação** do perfil. Um navegador novo (sem cookie) ficava travado — não conseguia
recriar (o único perfil já existia → 409) nem "logar" (não há endpoint de login no modo pessoal).
Sendo o modo pessoal um app desktop single-user em loopback, sem muro de autenticação, isso era um
defeito real de sessão.

## Correção

`PersonalProfileSessionMiddleware` (registrado **apenas** quando `!Multiuser`, ou seja, nunca em
modo servidor/OIDC): quando a requisição não traz sessão (nem item OIDC nem cookie válido) e existe
ao menos um perfil local, adota deterministicamente o perfil mais antigo (ULID crescente) como
sessão da requisição (`HttpContext.Items`) e grava o cookie de sessão (HttpOnly, SameSite=Strict,
Secure sob HTTPS) para as requisições seguintes. Zero perfis → nenhuma adoção (mantém 404 e o
fluxo de onboarding).

Isolamento preservado: no modo servidor/OIDC (`Multiuser=true`) o middleware **não** é registrado,
então a autenticação real continua exigida — os probes adversariais (cookie forjado/ausente → 404,
sessão B não muta perfil de A → 403) permanecem válidos.

## Testes

`PersonalSessionAdoptionTests`: com zero perfis, navegador sem cookie → 404; após onboarding criar o
perfil em um "navegador", um navegador **diferente sem cookie** obtém `/profiles/current` → 200
(perfil adotado, `Set-Cookie` presente) e `GET /api/v1/projects` → 200 (a cascata de 401 some).
Regressão: `LocalProfileApiTests`, `OidcServerModeHostTests`, `PostgresMultiuserLoadTests` (isolamento
adversarial) e `SecurityHeadersTests` seguem verdes.

Gate: format sem mudanças; build Release 0 warnings/0 erros; suíte integral **233/233**.
