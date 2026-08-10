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
ACCOUNTS_ROOT = HARNESS_DIR / "accounts"


PROVIDERS = {
    "claude": ("anthropic", "claude-code"),
    "anthropic": ("anthropic", "claude-code"),
    "codex": ("openai", "codex"),
    "openai": ("openai", "codex"),
}


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
        env["CODEX_HOME"] = str(home / "codex")
    return env


def ensure_dirs(account: dict[str, Any]) -> None:
    home = account_home(account["alias"])
    home.mkdir(parents=True, exist_ok=True)
    if account["executorId"] == "claude-code":
        (home / "config").mkdir(parents=True, exist_ok=True)
    elif account["executorId"] == "codex":
        (home / "codex").mkdir(parents=True, exist_ok=True)


def accounts_doc() -> dict[str, Any]:
    doc = load_json(ACCOUNTS_FILE, {"accounts": []})
    doc.setdefault("accounts", [])
    return doc


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
    print("ALIAS\tPROVIDER\tEXECUTOR\tROLES\tENABLED\tUSAGE_POLICY\tAVAILABILITY")
    for account in accounts_doc().get("accounts", []):
        alias = account.get("alias", "")
        state = availability.get(alias, {}).get("State", "Unknown")
        roles = ",".join(account.get("allowedRoles", []))
        print(
            f"{alias}\t{account.get('providerKind','')}\t{account.get('executorId','')}\t"
            f"{roles}\t{account.get('enabled', False)}\t{account.get('usagePolicy', 'AUTOMATIC')}\t{state}"
        )
    return 0


def command_add(args: argparse.Namespace) -> int:
    provider_key = args.provider.lower()
    if provider_key not in PROVIDERS:
        raise SystemExit(f"Provider não suportado: {args.provider}. Use claude ou codex.")
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


def command_auth(args: argparse.Namespace) -> int:
    account = find_account(args.account_id)
    if account["executorId"] == "claude-code":
        print(f"Autenticando Claude isolado: {args.account_id}")
        print(f"CLAUDE_CONFIG_DIR={account_home(args.account_id) / 'config'}")
        print("Use o fluxo nativo do Claude Code no navegador. Não informe senha ao Poseidon.")
        set_availability(args.account_id, "Unavailable", "availability.auth_pending")
        code = run_auth_flow(account, ["claude"])
    elif account["executorId"] == "codex":
        print(f"Autenticando Codex isolado: {args.account_id}")
        print(f"CODEX_HOME={account_home(args.account_id) / 'codex'}")
        print("Use o fluxo nativo do Codex no navegador/device auth. Não informe senha ao Poseidon.")
        set_availability(args.account_id, "Unavailable", "availability.auth_pending")
        code = run_auth_flow(account, ["codex", "login", "--device-auth"])
    else:
        raise SystemExit(f"Auth nativo não implementado para executor {account['executorId']}")
    if code != 0:
        set_availability(args.account_id, "Unavailable", "availability.auth_failed")
        return code
    return command_probe(args)


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
    else:
        raise SystemExit(f"Logout não implementado para executor {account['executorId']}")
    set_availability(args.account_id, "Unavailable", "availability.logged_out")
    print(f"Conta desautenticada: {args.account_id}")
    return 0


def command_probe(args: argparse.Namespace) -> int:
    account = find_account(args.account_id)
    ensure_dirs(account)
    env = account_env(account)
    cwd = str(account_home(args.account_id))
    if account["executorId"] == "claude-code":
        argv = ["claude", "-p", "--safe-mode", "--output-format", "json", "Responda apenas OK."]
    elif account["executorId"] == "codex":
        argv = [
            "codex",
            "exec",
            "--skip-git-repo-check",
            "--sandbox",
            "workspace-write",
            "Responda apenas OK.",
        ]
    else:
        raise SystemExit(f"Probe nativo não implementado para executor {account['executorId']}")
    try:
        result = subprocess.run(argv, cwd=cwd, env=env, text=True, capture_output=True, timeout=120)
    except subprocess.TimeoutExpired:
        set_availability(args.account_id, "Unavailable", "availability.probe_timeout")
        print("Probe = FAIL (timeout)")
        return 2
    output = (result.stdout + "\n" + result.stderr).lower()
    if result.returncode == 0 and "ok" in output:
        set_availability(args.account_id, "Available", "availability.available")
        print("Probe = PASS")
        print("Auth = AUTHENTICATED")
        print("Quota = AVAILABLE")
        print("WriteCapability = YES" if "project-executor" in account.get("allowedRoles", []) else "WriteCapability = ROLE_DEPENDENT")
        return 0
    if "quota" in output or "limit" in output or "rate" in output:
        set_availability(args.account_id, "QuotaLimited", "availability.quota_exhausted")
        print("Probe = FAIL")
        print("Quota = EXHAUSTED_OR_LIMITED")
        return 2
    set_availability(args.account_id, "Unavailable", "availability.probe_failed")
    print("Probe = FAIL")
    print(f"ExitCode = {result.returncode}")
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
        codex_home = home / "codex"
        auth_state = "AUTHENTICATED" if any(codex_home.glob("auth*.json")) or any(codex_home.glob("*.json")) else "UNKNOWN"
        profile_location = str(codex_home)
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
        p.set_defaults(func=func)

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
