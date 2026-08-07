#!/usr/bin/env python3
"""Proxy de egresso das sandboxes de agente do Poseidon.

É a ÚNICA porta de saída de um contêiner de agente: a sandbox vive numa rede `--internal`
(sem gateway) e fala somente com este proxy, que é o único membro da rede com acesso ao
mundo. O proxy encaminha APENAS destinos da allowlist — tudo o mais recebe 403 e fica
visível no log do contêiner (um destino negado é um fato operacional, não um silêncio).

A allowlist vem de HARNESS_PROXY_ALLOWLIST (vírgulas). O default cobre o que uma execução
de agente precisa de verdade: os provedores de modelo da frota, o registro de pacotes e o
GitHub (dependências do produto gerado). Um destino a mais aqui é uma decisão de
segurança — não se adiciona host "por via das dúvidas".
"""

import http.client
import http.server
import os
import select
import socket
import socketserver
import sys
import urllib.parse

DEFAULT_ALLOWLIST = (
    "api.anthropic.com",
    "open.bigmodel.cn",
    "api.z.ai",
    "api.openai.com",
    # O Codex CLI com assinatura ChatGPT fala com o backend da conta, não com api.openai.com —
    # medido ao vivo: proxy-deny chatgpt.com:443 até a entrada existir.
    "chatgpt.com",
    "registry.npmjs.org",
    # Restore do .NET dentro da sandbox: o executor se auto-verifica com `dotnet build/test`
    # (regra do dono, 2026-08-08) e o feed v3 do NuGet vive nestes hosts.
    "api.nuget.org",
    "nuget.org",
    "github.com",
    "api.github.com",
    "codeload.github.com",
    "pypi.org",
    "files.pythonhosted.org",
)


def load_allowlist():
    raw = os.environ.get("HARNESS_PROXY_ALLOWLIST", "")
    entries = [item.strip().lower() for item in raw.split(",") if item.strip()]
    return frozenset(entries) if entries else frozenset(DEFAULT_ALLOWLIST)


ALLOWLIST = load_allowlist()


def allowlisted(host, port):
    return port in (80, 443) and host.lower() in ALLOWLIST


class QuietHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, _format, *_args):
        return


class ProxyHandler(QuietHandler):
    def _deny(self, destination):
        # A negação é o ÚNICO sinal que o operador tem de um destino faltando na allowlist.
        print(f"proxy-deny {destination}", file=sys.stderr, flush=True)
        self.send_error(403, "destination not allowlisted")

    def do_GET(self):
        parsed = urllib.parse.urlsplit(self.path)
        host = parsed.hostname or ""
        port = parsed.port or 80
        if not allowlisted(host, port):
            self._deny(f"{host}:{port}")
            return

        connection = http.client.HTTPConnection(host, port, timeout=15)
        try:
            target_path = urllib.parse.urlunsplit(("", "", parsed.path or "/", parsed.query, ""))
            connection.request("GET", target_path, headers={"Host": f"{host}:{port}"})
            response = connection.getresponse()
            body = response.read()
            self.send_response(response.status)
            self.send_header(
                "Content-Type", response.getheader("Content-Type", "application/octet-stream"))
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except OSError:
            self.send_error(502, "upstream unreachable")
        finally:
            connection.close()

    def do_CONNECT(self):
        host, separator, port_text = self.path.partition(":")
        try:
            port = int(port_text)
        except ValueError:
            port = -1
        if separator != ":" or not allowlisted(host, port):
            self._deny(self.path)
            return

        try:
            upstream = socket.create_connection((host, port), timeout=15)
        except OSError:
            self.send_error(502, "upstream unreachable")
            return

        try:
            self.send_response(200, "Connection established")
            self.end_headers()
            sockets = [self.connection, upstream]
            while True:
                readable, _, _ = select.select(sockets, [], [], 30)
                if not readable:
                    break
                for source in readable:
                    data = source.recv(65536)
                    if not data:
                        return
                    destination = upstream if source is self.connection else self.connection
                    destination.sendall(data)
        finally:
            upstream.close()


class ThreadedServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


def main():
    server = ThreadedServer(("0.0.0.0", 8080), ProxyHandler)
    print(f"proxy-up allowlist={sorted(ALLOWLIST)}", file=sys.stderr, flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
