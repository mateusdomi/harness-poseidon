# Evidência — correção transversal do Checkbox (P2 de usabilidade/acessibilidade)

Screenshots "after" da tela Notificações (a reportada) nos 2 temas, capturados
contra o build de produção + mock determinístico (desktop-1440).

- `desktop-dark-notifications.png` — tema escuro.
- `desktop-light-notifications.png` — tema claro.

Em ambos, "Notificações ativadas" (marcado) exibe **preenchimento accent + ícone
de check escuro claramente visível**; as categorias desmarcadas exibem o quadrado
vazio contornado. O estado deixou de depender apenas de cor.

## Before (defeito reproduzido)

O componente compartilhado desenhava o check via `background-image` com um
data-URI de SVG cujo `stroke` era `var(--color-accent-foreground)`. Custom
properties CSS **não resolvem dentro de data-URI de SVG** (contexto de documento
isolado), então o traço do check ficava sem cor e invisível: sobrava apenas o
preenchimento/contorno accent — exatamente o "contorno verde sem check" relatado.
A prova automatizada está em `frontend/e2e/checkbox-visual.spec.ts`, que valida a
opacidade computada do indicador (não apenas `checked=true`).
