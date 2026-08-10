#!/usr/bin/env bash
set -euo pipefail

observed_failures=0
e2e_capable=0

pass() { printf '%-22s PASS %s\n' "$1" "${2:-}"; }
fail() { printf '%-22s FAIL %s\n' "$1" "${2:-}"; observed_failures=1; }

command_check() {
  local label="$1" command="$2"
  if command -v "${command}" >/dev/null 2>&1; then
    pass "${label}" "$(command -v "${command}")"
  else
    fail "${label}" "not found"
  fi
}

node_package_check() {
  local package="$1"
  if command -v node >/dev/null 2>&1 && node -e "require.resolve('${package}')" >/dev/null 2>&1; then
    pass "${package}" "node package"
    return
  fi
  if command -v npm >/dev/null 2>&1; then
    local root
    root="$(npm root -g 2>/dev/null || true)"
    if [[ -n "${root}" ]] && NODE_PATH="${root}" node -e "require.resolve('${package}')" >/dev/null 2>&1; then
      pass "${package}" "global node package"
      return
    fi
  fi
  fail "${package}" "node package not found"
}

python_package_check() {
  local label="$1" module="$2"
  if command -v python3 >/dev/null 2>&1 && python3 -c "import ${module}" >/dev/null 2>&1; then
    pass "${label}" "python module"
  else
    fail "${label}" "python module not found"
  fi
}

browser_path_check() {
  local label="$1" path="$2"
  if [[ -x "${path}" ]]; then
    pass "${label}" "${path}"
  else
    fail "${label}" "${path} not executable"
  fi
}

cache_check() {
  local label="$1" path="$2"
  if [[ -d "${path}" ]]; then
    pass "${label}" "${path}"
  else
    fail "${label}" "${path} not found"
  fi
}

command_check "NODE" node
command_check "NPM" npm
command_check "PYTHON" python3

node_package_check "playwright"
node_package_check "@playwright/test"
node_package_check "@playwright/mcp"
node_package_check "puppeteer"
node_package_check "cypress"
node_package_check "selenium-webdriver"
node_package_check "webdriver-manager"
node_package_check "taiko"
node_package_check "nightwatch"
node_package_check "codeceptjs"

python_package_check "PY_PLAYWRIGHT" "playwright"
python_package_check "PYTEST_PLAYWRIGHT" "pytest_playwright"
python_package_check "PY_SELENIUM" "selenium"
python_package_check "SELENIUM_WIRE" "seleniumwire"
python_package_check "PYPPETEER" "pyppeteer"
python_package_check "ROBOTFRAMEWORK" "robot"
python_package_check "ROBOT_SELENIUM" "SeleniumLibrary"
python_package_check "REQUESTS_HTML" "requests_html"
python_package_check "BEAUTIFULSOUP4" "bs4"

browser_path_check "CHROME" "/opt/homebrew/bin/google-chrome"
browser_path_check "CHROMIUM" "/opt/homebrew/bin/chromium"
browser_path_check "FIREFOX" "/opt/homebrew/bin/firefox"
command_check "CHROMEDRIVER" chromedriver
command_check "GECKODRIVER" geckodriver
cache_check "PLAYWRIGHT_CACHE" "${HOME}/Library/Caches/ms-playwright"
cache_check "PUPPETEER_CACHE" "${HOME}/.cache/puppeteer"

if command -v node >/dev/null 2>&1 \
  && command -v npm >/dev/null 2>&1 \
  && node -e "require.resolve('@playwright/test')" >/dev/null 2>&1 \
  && ([[ -x "/opt/homebrew/bin/google-chrome" ]] || [[ -x "/opt/homebrew/bin/chromium" ]] || [[ -x "/opt/homebrew/bin/firefox" ]]); then
  e2e_capable=1
fi

if [[ "${e2e_capable}" -eq 1 ]]; then
  echo "E2E_CAPABLE=YES"
else
  echo "E2E_CAPABLE=NO"
fi

exit "$((1 - e2e_capable))"
