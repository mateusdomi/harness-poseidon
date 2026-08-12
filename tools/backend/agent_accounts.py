#!/usr/bin/env python3
"""Deterministic local agent account operations for Poseidon V3.

This script coordinates provider CLIs inside per-account homes. It never stores
Google passwords and never prints provider credential file contents.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import shutil
import subprocess
import sys
import re
from typing import Any


HARNESS_DIR = pathlib.Path.home() / ".harness"
ACCOUNTS_FILE = HARNESS_DIR / "agent-accounts.json"
AVAILABILITY_FILE = HARNESS_DIR / "account-availability.json"
AUTH_METADATA_FILE = HARNESS_DIR / "agent-auth-metadata.json"
ACCOUNTS_ROOT = HARNESS_DIR / "accounts"


PROVIDERS = {
    "claude": ("anthropic", "claude-code"),
    "anthropic": ("anthropic", "claude-code"),
    "codex": ("openai", "codex"),
    "openai": ("openai", "codex"),
    "kimi": ("moonshot", "kimi-code"),
    "moonshot": ("moonshot", "kimi-code"),
}

CODEX_AUTH_STRATEGIES = {"auto", "browser", "device", "api-key", "access-token"}


def load_json(path: pathlib.Path, default: Any) -> Any:
    if not path.exists():
        return default
    return json.loads(path.read_text())


def save_json(path: pathlib.Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    tmp.replace(path)


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def account_home(alias: str) -> pathlib.Path:
    return ACCOUNTS_ROOT / alias


def account_env(account: dict[str, Any]) -> dict[str, str]:
    env = os.environ.copy()
    home = account_home(account["alias"])
    if account["executorId"] == "claude-code":
        env["CLAUDE_CONFIG_DIR"] = str(home / "config")
    elif account["executorId"] == "codex":
        # Must match AccountProfileProvisioner.ConfigHomePath used by the Host runtime.
        # A previous helper-only `<account>/codex` home made `poseidon agents probe`
        # green while V3 dispatch used `<account>/config` and failed with 401.
        env["CODEX_HOME"] = str(home / "config")
    elif account["executorId"] == "kimi-code":
        config_home = home / "config"
        env["HOME"] = str(config_home)
        kimi_bin = pathlib.Path.home() / ".kimi-code" / "bin"
        env["PATH"] = f"{kimi_bin}{os.pathsep}{env.get('PATH', '')}"
    return env


def ensure_dirs(account: dict[str, Any]) -> None:
    home = account_home(account["alias"])
    home.mkdir(parents=True, exist_ok=True)
    if account["executorId"] == "claude-code":
        (home / "config").mkdir(parents=True, exist_ok=True)
    elif account["executorId"] == "codex":
        (home / "config").mkdir(parents=True, exist_ok=True)
    elif account["executorId"] == "kimi-code":
        (home / "config").mkdir(parents=True, exist_ok=True)


def accounts_doc() -> dict[str, Any]:
    doc = load_json(ACCOUNTS_FILE, {"accounts": []})
    doc.setdefault("accounts", [])
    return doc


def auth_metadata_doc() -> dict[str, Any]:
    doc = load_json(AUTH_METADATA_FILE, {"accounts": {}})
    doc.setdefault("accounts", {})
    return doc


def auth_metadata_for(alias: str) -> dict[str, Any]:
    return dict(auth_metadata_doc().get("accounts", {}).get(alias, {}))


def save_auth_metadata(alias: str, updates: dict[str, Any]) -> None:
    doc = auth_metadata_doc()
    current = dict(doc["accounts"].get(alias, {}))
    current.update({key: value for key, value in updates.items() if value is not None})
    doc["accounts"][alias] = current
    save_json(AUTH_METADATA_FILE, doc)


def cli_version(command: str) -> str | None:
    try:
        result = subprocess.run([command, "--version"], text=True, capture_output=True, timeout=20)
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return None
    text = (result.stdout or result.stderr).strip()
    return text.splitlines()[0] if text else None


def account_login_label(account: dict[str, Any]) -> str | None:
    metadata = auth_metadata_for(account["alias"])
    for key in ["providerAccountLabel", "loginEmail", "loginEmailHint"]:
        value = account.get(key) or metadata.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


def set_account_login_label(alias: str, label: str) -> None:
    doc = accounts_doc()
    found = False
    for account in doc["accounts"]:
        if account.get("alias") == alias:
            account["providerAccountLabel"] = label.strip()
            found = True
            break
    if not found:
        raise SystemExit(f"Conta não encontrada: {alias}")
    save_json(ACCOUNTS_FILE, doc)
    save_auth_metadata(alias, {"providerAccountLabel": label.strip(), "updatedAt": now()})


def find_account(alias: str) -> dict[str, Any]:
    for account in accounts_doc().get("accounts", []):
        if account.get("alias") == alias:
            return account
    raise SystemExit(f"Conta não encontrada: {alias}")


def availability_by_alias() -> dict[str, dict[str, Any]]:
    rows = load_json(AVAILABILITY_FILE, [])
    return {row.get("Alias"): row for row in rows if row.get("Alias")}


def set_availability(alias: str, state: str, reason: str) -> None:
    rows = load_json(AVAILABILITY_FILE, [])
    found = False
    for row in rows:
        if row.get("Alias") == alias:
            row.update(
                {
                    "State": state,
                    "CooldownUntil": None,
                    "ReasonCode": reason,
                    "ConsecutiveFailures": 0 if state == "Available" else row.get("ConsecutiveFailures", 0),
                    "UpdatedAt": now(),
                }
            )
            found = True
            break
    if not found:
        rows.append(
            {
                "Alias": alias,
                "State": state,
                "CooldownUntil": None,
                "ReasonCode": reason,
                "ConsecutiveFailures": 0,
                "UpdatedAt": now(),
            }
        )
    save_json(AVAILABILITY_FILE, rows)


def command_list(_: argparse.Namespace) -> int:
    availability = availability_by_alias()
    print("ALIAS\tPROVIDER\tEXECUTOR\tROLES\tENABLED\tUSAGE_POLICY\tAVAILABILITY\tLOGIN_LABEL\tAUTH_STRATEGY")
    for account in accounts_doc().get("accounts", []):
        alias = account.get("alias", "")
        state = availability.get(alias, {}).get("State", "Unknown")
        roles = ",".join(account.get("allowedRoles", []))
        metadata = auth_metadata_for(alias)
        label = account_login_label(account) or "-"
        strategy = metadata.get("successfulAuthStrategy") or account.get("preferredAuthStrategy") or "-"
        print(
            f"{alias}\t{account.get('providerKind','')}\t{account.get('executorId','')}\t"
            f"{roles}\t{account.get('enabled', False)}\t{account.get('usagePolicy', 'AUTOMATIC')}\t{state}\t"
            f"{label}\t{strategy}"
        )
    return 0


def command_add(args: argparse.Namespace) -> int:
    provider_key = args.provider.lower()
    if provider_key not in PROVIDERS:
        raise SystemExit(f"Provider não suportado: {args.provider}. Use claude, codex ou kimi.")
    provider_kind, executor_id = PROVIDERS[provider_key]
    doc = accounts_doc()
    if any(account.get("alias") == args.account_id for account in doc["accounts"]):
        print(f"Conta já existe: {args.account_id}")
        return 0
    if args.role == "chief":
        roles = ["chief-orchestrator"]
        priority = 1000
    elif args.role == "critic":
        roles = ["critic"]
        priority = 80
    else:
        roles = ["project-executor"]
        priority = 100
    doc["accounts"].append(
        {
            "alias": args.account_id,
            "providerKind": provider_kind,
            "executorId": executor_id,
            "credentialRef": f"keychain://poseidon/{args.account_id}",
            "allowedRoles": roles,
            "allowedPathScopes": [],
            "concurrencyLimit": 1,
            "priority": priority,
            "enabled": True,
            "usagePolicy": "AUTOMATIC",
            **({"providerAccountLabel": args.login_label.strip()} if args.login_label else {}),
        }
    )
    save_json(ACCOUNTS_FILE, doc)
    account = find_account(args.account_id)
    ensure_dirs(account)
    (account_home(args.account_id) / "profile.json").write_text(
        json.dumps(
            {
                "accountId": args.account_id,
                "provider": provider_kind,
                "executor": executor_id,
                "role": args.role,
                "createdAt": now(),
            },
            indent=2,
        )
        + "\n"
    )
    set_availability(args.account_id, "Unavailable", "availability.not_probed")
    print(f"Conta adicionada: {args.account_id} ({provider_kind}/{executor_id})")
    return 0


def command_label(args: argparse.Namespace) -> int:
    find_account(args.account_id)
    set_account_login_label(args.account_id, args.login_label)
    print(f"Login label atualizado para {args.account_id}: {args.login_label}")
    return 0


def open_url_if_present(line: str, opened: set[str]) -> None:
    for match in re.findall(r"https?://[^\s)>\"]+", line):
        url = match.rstrip(".,;")
        if url in opened:
            continue
        opened.add(url)
        if sys.platform == "darwin":
            subprocess.Popen(["open", url], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            print(f"Browser aberto para autenticação: {url}", flush=True)
        else:
            print(f"Abra no navegador: {url}", flush=True)


def run_interactive(account: dict[str, Any], argv: list[str]) -> int:
    ensure_dirs(account)
    env = account_env(account)
    return subprocess.call(argv, env=env)


def run_auth_flow(account: dict[str, Any], argv: list[str], timeout_seconds: int = 900) -> int:
    ensure_dirs(account)
    env = account_env(account)
    opened: set[str] = set()
    process = subprocess.Popen(
        argv,
        env=env,
        cwd=str(account_home(account["alias"])),
        text=True,
        stdin=sys.stdin,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        bufsize=1,
    )
    deadline = dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=timeout_seconds)
    assert process.stdout is not None
    while True:
        line = process.stdout.readline()
        if line:
            print(line, end="", flush=True)
            open_url_if_present(line, opened)
        elif process.poll() is not None:
            return process.returncode or 0
        elif dt.datetime.now(dt.timezone.utc) >= deadline:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
            set_availability(account["alias"], "Unavailable", "availability.auth_expired")
            print("AUTH_PENDING_EXPIRED: rode auth novamente quando puder concluir no navegador.", flush=True)
            return 124


def resolve_codex_auth_strategy(account: dict[str, Any], requested: str) -> str:
    strategy = requested.lower().strip()
    if strategy not in CODEX_AUTH_STRATEGIES:
        raise SystemExit(f"Estratégia Codex inválida: {requested}. Use {', '.join(sorted(CODEX_AUTH_STRATEGIES))}.")
    if strategy != "auto":
        return strategy
    metadata = auth_metadata_for(account["alias"])
    learned = str(metadata.get("successfulAuthStrategy") or "").lower()
    if learned in {"browser", "device", "api-key", "access-token"}:
        return learned
    configured = str(account.get("preferredAuthStrategy") or "").lower()
    if configured in {"browser", "device", "api-key", "access-token"}:
        return configured
    # Codex CLI 0.147.0 documents browser OAuth as the default `codex login` flow.
    # Device auth stays available, but it must not be the only hardcoded path.
    return "browser"


def codex_auth_argv(strategy: str) -> list[str]:
    if strategy == "browser":
        return ["codex", "login"]
    if strategy == "device":
        return ["codex", "login", "--device-auth"]
    if strategy == "api-key":
        return ["codex", "login", "--with-api-key"]
    if strategy == "access-token":
        return ["codex", "login", "--with-access-token"]
    raise SystemExit(f"Estratégia Codex não suportada: {strategy}")


def print_auth_banner(account: dict[str, Any], strategy: str, version: str | None) -> None:
    label = account_login_label(account) or "NÃO CADASTRADO"
    print("================================================")
    print("AUTENTICAÇÃO CODEX")
    print("================================================")
    print(f"Agente: {account.get('publicAgentProfile') or account.get('alias')}")
    print(f"Conta Poseidon: {account.get('alias')}")
    print("Provider: OpenAI Codex")
    print(f"E-mail/Login esperado: {label}")
    print(f"Estado atual: {availability_by_alias().get(account['alias'], {}).get('State', 'Unknown')}")
    print("Objetivo: autenticar esta conta como Project Executor")
    print(f"Estratégia: {strategy}")
    print(f"CLI: {version or 'unknown'}")
    print("================================================")


def command_auth(args: argparse.Namespace) -> int:
    account = find_account(args.account_id)
    if account["executorId"] == "claude-code":
        print(f"Autenticando Claude isolado: {args.account_id}")
        print(f"CLAUDE_CONFIG_DIR={account_home(args.account_id) / 'config'}")
        print("Use o fluxo nativo do Claude Code no navegador. Não informe senha ao Poseidon.")
        set_availability(args.account_id, "Unavailable", "availability.auth_pending")
        code = run_auth_flow(account, ["claude"])
    elif account["executorId"] == "codex":
        strategy = resolve_codex_auth_strategy(account, getattr(args, "strategy", "auto"))
        version = cli_version("codex")
        print_auth_banner(account, strategy, version)
        print(f"Autenticando Codex isolado: {args.account_id}")
        print(f"CODEX_HOME={account_home(args.account_id) / 'codex'}")
        print("Use o fluxo nativo do Codex. Não informe senha ao Poseidon.")
        if strategy in {"api-key", "access-token"}:
            print("ATENÇÃO: esta estratégia recebe segredo pela CLI nativa. O Poseidon não armazena nem imprime o valor.")
        set_availability(args.account_id, "Unavailable", "availability.auth_pending")
        code = run_auth_flow(account, codex_auth_argv(strategy))
    elif account["executorId"] == "kimi-code":
        print(f"Autenticando Kimi isolado: {args.account_id}")
        print(f"HOME={account_home(args.account_id) / 'config'}")
        print("Use o fluxo nativo do Kimi Code no navegador/device auth. Não informe senha ao Poseidon.")
        set_availability(args.account_id, "Unavailable", "availability.auth_pending")
        code = run_auth_flow(account, ["kimi", "login"])
    else:
        raise SystemExit(f"Auth nativo não implementado para executor {account['executorId']}")
    if code != 0:
        set_availability(args.account_id, "Unavailable", "availability.auth_failed")
        return code
    probe_code = command_probe(args)
    if probe_code == 0 and account["executorId"] == "codex":
        save_auth_metadata(
            args.account_id,
            {
                "provider": account.get("providerKind"),
                "executorId": account.get("executorId"),
                "cliVersion": cli_version("codex"),
                "successfulAuthStrategy": resolve_codex_auth_strategy(account, getattr(args, "strategy", "auto")),
                "lastAuthSuccessAt": now(),
                "platform": sys.platform,
            },
        )
    return probe_code


def set_account_usage_policy(alias: str, usage_policy: str, enabled: bool) -> None:
    doc = accounts_doc()
    found = False
    for account in doc["accounts"]:
        if account.get("alias") == alias:
            account["usagePolicy"] = usage_policy
            account["enabled"] = enabled
            found = True
            break
    if not found:
        raise SystemExit(f"Conta não encontrada: {alias}")
    save_json(ACCOUNTS_FILE, doc)


def command_disable(args: argparse.Namespace) -> int:
    find_account(args.account_id)
    set_account_usage_policy(args.account_id, "RESERVED", False)
    set_availability(args.account_id, "Unavailable", "account.reserved")
    print(f"Conta reservada/desabilitada para auto-dispatch: {args.account_id}")
    return 0


def command_enable(args: argparse.Namespace) -> int:
    find_account(args.account_id)
    set_account_usage_policy(args.account_id, "AUTOMATIC", True)
    set_availability(args.account_id, "Unavailable", "availability.not_probed")
    print(f"Conta reabilitada para uso automático: {args.account_id}")
    return 0


def command_logout(args: argparse.Namespace) -> int:
    account = find_account(args.account_id)
    ensure_dirs(account)
    if account["executorId"] == "codex":
        code = run_interactive(account, ["codex", "logout"])
        if code != 0:
            return code
    elif account["executorId"] == "claude-code":
        config = account_home(args.account_id) / "config"
        auth_file = config / ".claude.json"
        if auth_file.exists():
            backups = config / "backups"
            backups.mkdir(parents=True, exist_ok=True)
            auth_file.replace(backups / f".claude.json.logout.{int(dt.datetime.now().timestamp())}")
        history = config / "history.jsonl"
        if history.exists():
            history.unlink()
        print(f"Estado de autenticação Claude removido somente de {config}")
    elif account["executorId"] == "kimi-code":
        config = account_home(args.account_id) / "config" / ".kimi-code"
        backups = config / "backups"
        backups.mkdir(parents=True, exist_ok=True)
        for relative in [
            pathlib.Path("credentials") / "kimi-code.json",
            pathlib.Path("oauth") / "kimi-code",
        ]:
            target = config / relative
            if target.exists():
                backup_name = f"{relative.name}.logout.{int(dt.datetime.now().timestamp())}"
                target.replace(backups / backup_name)
        print(f"Estado de autenticação Kimi removido somente de {config}")
    else:
        raise SystemExit(f"Logout não implementado para executor {account['executorId']}")
    set_availability(args.account_id, "Unavailable", "availability.logged_out")
    print(f"Conta desautenticada: {args.account_id}")
    return 0


def command_probe(args: argparse.Namespace) -> int:
    account = find_account(args.account_id)
    ensure_dirs(account)
    reserved = account.get("usagePolicy") == "RESERVED" or account.get("enabled") is False
    env = account_env(account)
    cwd = str(account_home(args.account_id))
    if account["executorId"] == "claude-code":
        argv = ["claude", "-p", "--safe-mode", "--output-format", "json", "Responda apenas OK."]
    elif account["executorId"] == "codex":
        last_message = account_home(args.account_id) / "work" / f"probe-last-message-{int(dt.datetime.now().timestamp())}.txt"
        argv = [
            "codex",
            "exec",
            "--json",
            "--skip-git-repo-check",
            "-C",
            cwd,
            "--sandbox",
            "read-only",
            "--output-last-message",
            str(last_message),
            "-",
        ]
    elif account["executorId"] == "kimi-code":
        argv = ["kimi", "--output-format", "text", "-p", "Responda apenas OK."]
    else:
        raise SystemExit(f"Probe nativo não implementado para executor {account['executorId']}")
    try:
        result = subprocess.run(
            argv,
            cwd=cwd,
            env=env,
            text=True,
            input="Probe de disponibilidade do Poseidon. Responda somente: OK\n"
            if account["executorId"] == "codex"
            else None,
            capture_output=True,
            timeout=120)
    except subprocess.TimeoutExpired:
        if not reserved:
            set_availability(args.account_id, "Unavailable", "availability.probe_timeout")
        print("Probe = FAIL (timeout)")
        if reserved:
            print("AccountUsage = RESERVED (ledger preservado)")
        return 2
    output = (result.stdout + "\n" + result.stderr).lower()
    if result.returncode == 0 and "ok" in output:
        if not reserved:
            set_availability(args.account_id, "Available", "availability.available")
        print("Probe = PASS")
        print("Auth = AUTHENTICATED")
        print("Quota = AVAILABLE")
        print("WriteCapability = YES" if "project-executor" in account.get("allowedRoles", []) else "WriteCapability = ROLE_DEPENDENT")
        if reserved:
            print("AccountUsage = RESERVED (não elegível para auto-dispatch até enable explícito)")
        return 0
    if "quota" in output or "limit" in output or "rate" in output:
        if not reserved:
            set_availability(args.account_id, "QuotaLimited", "availability.quota_exhausted")
        print("Probe = FAIL")
        print("Quota = EXHAUSTED_OR_LIMITED")
        if reserved:
            print("AccountUsage = RESERVED (ledger preservado)")
        return 2
    if not reserved:
        set_availability(args.account_id, "Unavailable", "availability.probe_failed")
    print("Probe = FAIL")
    print(f"ExitCode = {result.returncode}")
    if reserved:
        print("AccountUsage = RESERVED (ledger preservado)")
    tail = (result.stderr or result.stdout)[-800:]
    if tail:
        print(tail)
    return 2


def command_status(args: argparse.Namespace) -> int:
    account = find_account(args.account_id)
    availability = availability_by_alias().get(args.account_id, {})
    ensure_dirs(account)
    home = account_home(args.account_id)
    if account["executorId"] == "claude-code":
        auth_state = "AUTHENTICATED" if (home / "config" / ".claude.json").exists() else "NOT_AUTHENTICATED"
        profile_location = str(home / "config")
    elif account["executorId"] == "codex":
        codex_home = home / "config"
        auth_state = "AUTHENTICATED" if any(codex_home.glob("auth*.json")) or any(codex_home.glob("*.json")) else "UNKNOWN"
        profile_location = str(codex_home)
    elif account["executorId"] == "kimi-code":
        kimi_home = home / "config" / ".kimi-code"
        auth_state = "AUTHENTICATED" if (kimi_home / "credentials" / "kimi-code.json").exists() else "NOT_AUTHENTICATED"
        profile_location = str(kimi_home)
    else:
        auth_state = "UNKNOWN"
        profile_location = str(home)
    print(f"AccountId: {args.account_id}")
    print(f"Provider: {account.get('providerKind')}")
    print(f"Executor: {account.get('executorId')}")
    print(f"Roles: {','.join(account.get('allowedRoles', []))}")
    print(f"UsagePolicy: {account.get('usagePolicy', 'AUTOMATIC')}")
    print(f"Enabled: {account.get('enabled', True)}")
    print(f"AuthState: {auth_state}")
    print(f"Availability: {availability.get('State', 'Unknown')}")
    print(f"Reason: {availability.get('ReasonCode', 'unknown')}")
    print(f"LoginLabel: {account_login_label(account) or 'NOT_CONFIGURED'}")
    metadata = auth_metadata_for(args.account_id)
    print(f"SuccessfulAuthStrategy: {metadata.get('successfulAuthStrategy', 'UNKNOWN')}")
    print(f"LastAuthSuccessAt: {metadata.get('lastAuthSuccessAt', 'UNKNOWN')}")
    print(f"ProfileLocation: {profile_location}")
    return 0


def command_chief_set(args: argparse.Namespace) -> int:
    doc = accounts_doc()
    selected = None
    for account in doc["accounts"]:
        if account.get("alias") == args.account_id:
            selected = account
            break
    if selected is None:
        raise SystemExit(f"Conta não encontrada: {args.account_id}")
    if "chief-orchestrator" not in selected.get("allowedRoles", []):
        raise SystemExit(f"Conta {args.account_id} não possui role chief-orchestrator")
    for account in doc["accounts"]:
        if "chief-orchestrator" in account.get("allowedRoles", []):
            account["priority"] = 10000 if account.get("alias") == args.account_id else min(int(account.get("priority", 999)), 999)
    save_json(ACCOUNTS_FILE, doc)
    print(f"Chief ativa definida: {args.account_id}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="poseidon-agent-accounts")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("list").set_defaults(func=command_list)

    add = sub.add_parser("add")
    add.add_argument("provider")
    add.add_argument("account_id")
    add.add_argument("--role", choices=["executor", "chief", "critic"], default="executor")
    add.add_argument("--login-label", default=None)
    add.set_defaults(func=command_add)

    for name, func in [
        ("auth", command_auth),
        ("logout", command_logout),
        ("probe", command_probe),
        ("status", command_status),
        ("disable", command_disable),
        ("enable", command_enable),
    ]:
        p = sub.add_parser(name)
        p.add_argument("account_id")
        if name == "auth":
            p.add_argument("--strategy", choices=sorted(CODEX_AUTH_STRATEGIES), default="auto")
        p.set_defaults(func=func)

    label = sub.add_parser("label")
    label.add_argument("account_id")
    label.add_argument("login_label")
    label.set_defaults(func=command_label)

    chief = sub.add_parser("chief-set")
    chief.add_argument("account_id")
    chief.set_defaults(func=command_chief_set)

    return parser


def main(argv: list[str]) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
