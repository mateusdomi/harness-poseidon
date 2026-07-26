# Git e integração

## Branches

As únicas branches remotas permanentes são `main` e `develop`. Trabalho de
implementação ocorre em `develop`; `main` só recebe integração com autorização
humana explícita. É proibido force push, reescrever histórico publicado, alterar
remotes ou contornar proteção de branch.

Paralelismo de agentes usa worktrees locais efêmeras, ScopeClaims e checkpoints,
sem criar branches remotas adicionais. Exceção à política de branches exige
decisão humana registrada.

## Preservação de trabalho

- Inspecione branch, status, diff e escopo antes de alterar arquivos.
- Alterações preexistentes ou fora do card pertencem a outro trabalho e devem ser
  preservadas.
- Nunca use comandos destrutivos para resolver conflito ou obter uma árvore limpa.
- Um commit contém uma unidade verificável e usa mensagem convencional.
- Não misture refatoração, formatação ou arquivos sem relação com o card.
- Integração rejeita patch obsoleto, conflito de ScopeClaim ou gate vermelho.

## Push e merge

Push só ocorre a partir de árvore limpa e com os gates aplicáveis verdes. Cards que
alteram código exigem revisão por agente distinto antes do merge. O coordenador de
merge serializa a integração, confirma o fencing token vigente e executa os testes
de integração após o fan-in.
