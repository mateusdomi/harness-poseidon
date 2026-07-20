# Regra canônica — documentação e manifest

Owner: Technical Writing. Versão: 1.0.0.

- `governance/manifest.yaml` é o catálogo canônico. Markdown ativo precisa de
  entrada completa ou allowlist explícita; IDs, paths e tópicos/escopos canônicos
  não podem conflitar.
- Source of truth é editável; adapter, índice e projeção gerada nunca são editados
  manualmente. Regenerar e validar checksum após mudança canônica.
- Documentos superseded/historical usam `on-demand` ou `never`, jamais carga
  automática. Pesquisa é histórica e on-demand.
- Paths ativos são relativos a `$REPO_ROOT` ou usam configuração tipada. Paths de
  usuário, máquina, diretórios temporários e fontes obrigatórias fora do Git
  falham no gate.
- Links internos devem resolver. Dependências precisam existir e ciclos proibidos
  falham. Review vencida, baixa evidência ou documento sem uso gera finding, não
  exclusão automática.
- Mudança normativa registra owner, versão, verificação, enforcement e relação de
  supersession quando substitui outra fonte.

Enforcement: governance linter/generator, stale-doc detector, CI e release gate.
