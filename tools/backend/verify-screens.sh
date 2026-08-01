#!/usr/bin/env bash
#
# Testes de TELA (Playwright) no gate canônico.
#
# Os 21 arquivos de e2e existiam e nenhum gate os executava. Enquanto ficaram de fora,
# apodreceram em silêncio: quando o formulário de projeto foi simplificado para o modo Negócio,
# os quatro specs que criavam projeto quebraram de uma vez e nada acusou. Pior — o mesmo silêncio
# escondeu um defeito de PRODUTO: aprovação sem título de negócio bloqueava o dono de aprovar
# qualquer coisa, inclusive o Termo de Aceite.
#
# Um teste fora do gate não é rede de segurança: é uma opinião antiga sobre a tela.
#
# Roda aqui, e NÃO no `build-frontend.sh`, porque aquele script também está no caminho de
# `./poseidon start`: o dono que abre o produto não pode esperar dois minutos de teste de tela
# para ver a primeira janela. Validação é do gate; inicialização é do usuário.
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly FRONTEND_DIR="${REPOSITORY_ROOT}/frontend"

cd "${FRONTEND_DIR}"

# O navegador pode não estar instalado numa máquina limpa. Instalar é barato e idempotente;
# falhar por ausência de binário seria reprovar o código por um problema de ambiente.
NODE_NO_WARNINGS=1 npx playwright install chromium >/dev/null 2>&1 || true

NODE_NO_WARNINGS=1 npm run test:e2e -- --reporter=line
