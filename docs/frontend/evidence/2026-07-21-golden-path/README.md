# Evidência visual — golden path (2026-07-21)

Capturas do mock determinístico (`VITE_API_MODE=mock`) em 1440×1250, tema
claro, sobre o build de produção (`npm run build` + `vite preview`).

O projeto usado ("Projeto Golden Path") foi criado **sem workflow**, para
exercitar a jornada incompleta — o estado que a homologação humana apontou
como desorientador.

| Arquivo | O que prova |
| --- | --- |
| `cockpit-checklist.png` | Checklist persistente com 5 de 8 etapas concluídas, passo atual (Workflow) destacado com explicação e CTA única, e etapas seguintes bloqueadas mostrando o pré-requisito ("Requer antes: Workflow"). Selo "Modo simulado" no cabeçalho. |
| `chat-bloqueio-honesto.png` | Execução do Chief bloqueada **com motivo**: lista do que está pronto (provedor, modelo) e do que falta (workflow), CTA única e composer desabilitado — sem esconder a razão. |
| `workflow-recomendado.png` | Estado vazio orientado: explicação do conceito, template **Recomendado** com versão, descrição e fases resumidas, CTA "Usar workflow recomendado". |
| `orquestrador-prontidao.png` | Prontidão real do Chief com explicação, selo "Modo simulado" e o modelo herdado da definição marcado como **"Binding pendente"** em vez de "em uso". |

**Limite desta evidência:** o mock nasce com organizações e projetos, então o
recorte "zero organização" (pré-condição da tela de Projetos e retorno
automático ao fluxo) **não** aparece aqui. Ele é coberto por
`e2e/package-clean.spec.ts`, que roda contra o pacote self-contained com data
dir vazio — gate ainda **não executado** nesta sessão por falta do pacote
instalado.
