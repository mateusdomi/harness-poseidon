#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Poseidon — FALAR COM O CHEFE (duplo-clique no Finder).
#
# Abre o Claude Code JÁ dentro da pasta do projeto — é isso que dispara o boot
# do Chefe: o hook SessionStart (briefing + status vivo), as memórias e o
# CLAUDE.md só carregam quando o Claude é aberto AQUI, não na sua home.
# ---------------------------------------------------------------------------
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
export PATH="${HOME}/.local/bin:${PATH}"
echo "== Poseidon — abrindo o Chefe no projeto: $(pwd) =="
# Permissões full: o Chefe é um orquestrador de confiança; sem isto ele pede
# permissão a cada ação. bypassPermissions = age direto (rede/arquivos/git locais).
exec claude --permission-mode bypassPermissions
