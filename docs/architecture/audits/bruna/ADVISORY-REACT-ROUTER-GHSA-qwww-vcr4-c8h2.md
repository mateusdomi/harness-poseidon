# Avaliação do advisory GHSA-qwww-vcr4-c8h2 (react-router)

**Data:** 31/07/2026
**Escopo:** apenas análise de aplicabilidade. Nenhuma dependência foi alterada.

## Fato

`npm audit` no `frontend/` acusa:

- **Advisory:** GHSA-qwww-vcr4-c8h2 — *React Router: RSC Mode CSRF Bypass Allows Action
  Execution Before 400 Response* (CWE-352).
- **Faixa afetada:** `react-router >= 7.12.0 < 8.3.0`.
- **Versão instalada:** `react-router-dom@7.18.1` → `react-router@7.18.1` (dentro da faixa).
- **Severidade reportada:** high. CVSS publicado: sem vetor (score 0).

## Aplicabilidade: NÃO explorável neste produto

A falha está no **modo RSC** do React Router: um servidor que atende requisições RSC executa a
*action* antes de a verificação de CSRF responder 400. Isso exige o runtime RSC — o handler
`matchRSCServerRequest`, o plugin `@vitejs/plugin-rsc` e um servidor Node executando rotas do
React Router.

O frontend do Poseidon é uma SPA compilada e servida como arquivo estático pelo Host .NET:

- `frontend/src/app/router.tsx` usa `createBrowserRouter` — roteamento inteiramente no navegador;
- `frontend/src/main.tsx` monta `RouterProvider` no cliente;
- não há ocorrência de `matchRSCServerRequest`, `unstable_RSC`, `ServerRouter`,
  `createStaticHandler` nem de `@vitejs/plugin-rsc` no repositório;
- nenhuma *action* do React Router é executada no servidor: toda mutação passa pela API do Host,
  que tem a própria política de autenticação e sessão.

Sem servidor RSC não existe a superfície descrita pelo advisory.

## Por que não atualizar agora

A única versão corrigida é `>= 8.3.0`: **um major**. O `fixAvailable` sugerido pelo `npm audit`
(`react-router-dom@7.11.0`) é um *downgrade* de sete minors, que troca um risco não explorável por
uma regressão real de funcionalidade.

Subir para o major durante a Fase 0 significaria migração de rotas e retestes de toda a navegação —
exatamente o que a fase proíbe ("nova arquitetura de frontend", "alterações de UI sem necessidade")
e um risco maior do que o que se quer mitigar.

## Decisão

Manter `react-router-dom@7.18.1`. Reavaliar quando:

1. a linha 7.x receber correção — a faixa afetada passa a ter limite superior em 7.x; ou
2. o produto adotar RSC, em qualquer forma — aí o advisory passa a ser aplicável e a atualização
   vira bloqueante; ou
3. o major 8.x entrar em um ciclo de trabalho próprio de frontend, com regressão visual e e2e.

Até lá o item permanece registrado como aceito, com esta evidência — não como desconhecido.

## Reprodução

```bash
cd frontend && npm audit --json
grep -rn "createBrowserRouter\|RouterProvider" src | head
grep -rn "matchRSCServerRequest\|unstable_RSC\|@vitejs/plugin-rsc" src vite.config.ts package.json
```
