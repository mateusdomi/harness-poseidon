import errno
import http.client
import http.server
import json
import os
import select
import socket
import socketserver
import sys
import time
import urllib.parse


TARGET_HOST = "harness-target"
TARGET_PORT = 8080


class QuietHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, _format, *_args):
        return


class TargetHandler(QuietHandler):
    def do_GET(self):
        body = b"allowlisted-through-proxy"
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class ProxyHandler(QuietHandler):
    def do_GET(self):
        parsed = urllib.parse.urlsplit(self.path)
        if parsed.hostname != TARGET_HOST or (parsed.port or 80) != TARGET_PORT:
            self.send_error(403, "destination not allowlisted")
            return

        connection = http.client.HTTPConnection(TARGET_HOST, TARGET_PORT, timeout=5)
        try:
            target_path = urllib.parse.urlunsplit(("", "", parsed.path or "/", parsed.query, ""))
            connection.request("GET", target_path, headers={"Host": f"{TARGET_HOST}:{TARGET_PORT}"})
            response = connection.getresponse()
            body = response.read()
            self.send_response(response.status)
            self.send_header("Content-Type", response.getheader("Content-Type", "application/octet-stream"))
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        finally:
            connection.close()

    def do_CONNECT(self):
        host, separator, port_text = self.path.partition(":")
        if host != TARGET_HOST or separator != ":" or int(port_text) != TARGET_PORT:
            self.send_error(403, "destination not allowlisted")
            return

        upstream = socket.create_connection((TARGET_HOST, TARGET_PORT), timeout=5)
        try:
            self.send_response(200, "Connection established")
            self.end_headers()
            sockets = [self.connection, upstream]
            while True:
                readable, _, _ = select.select(sockets, [], [], 5)
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


def request_through_proxy(proxy_host, absolute_url):
    with socket.create_connection((proxy_host, 8080), timeout=2) as connection:
        request = (
            f"GET {absolute_url} HTTP/1.1\r\n"
            f"Host: {urllib.parse.urlsplit(absolute_url).netloc}\r\n"
            "Connection: close\r\n\r\n"
        )
        connection.sendall(request.encode("ascii"))
        chunks = []
        while True:
            chunk = connection.recv(65536)
            if not chunk:
                break
            chunks.append(chunk)
        return b"".join(chunks)


def request_connect_tunnel(proxy_host):
    with socket.create_connection((proxy_host, 8080), timeout=2) as connection:
        connection.sendall(
            (
                f"CONNECT {TARGET_HOST}:{TARGET_PORT} HTTP/1.1\r\n"
                f"Host: {TARGET_HOST}:{TARGET_PORT}\r\n\r\n"
            ).encode("ascii")
        )
        response = b""
        while b"\r\n\r\n" not in response:
            response += connection.recv(4096)
        if b" 200 " not in response:
            return response

        connection.sendall(
            (
                "GET /connect-tunnel HTTP/1.1\r\n"
                f"Host: {TARGET_HOST}:{TARGET_PORT}\r\n"
                "Connection: close\r\n\r\n"
            ).encode("ascii")
        )
        chunks = []
        while True:
            chunk = connection.recv(65536)
            if not chunk:
                break
            chunks.append(chunk)
        return response + b"".join(chunks)


def run_probe():
    direct_target_blocked = False
    try:
        socket.create_connection((TARGET_HOST, TARGET_PORT), timeout=1).close()
    except (OSError, socket.gaierror):
        direct_target_blocked = True

    direct_internet_blocked = False
    try:
        socket.create_connection(("1.1.1.1", 80), timeout=1).close()
    except OSError:
        direct_internet_blocked = True

    allowed_response = None
    for _ in range(40):
        try:
            allowed_response = request_through_proxy(
                "harness-proxy", f"http://{TARGET_HOST}:{TARGET_PORT}/allowed"
            )
            break
        except OSError:
            time.sleep(0.1)

    denied_response = request_through_proxy("harness-proxy", "http://example.com/denied")
    connect_response = request_connect_tunnel("harness-proxy")
    proxy_allowed = allowed_response is not None and b"allowlisted-through-proxy" in allowed_response
    proxy_denied = b" 403 " in denied_response
    connect_tunnel_allowed = b" 200 " in connect_response and b"allowlisted-through-proxy" in connect_response

    workspace_marker = "/workspace/sandbox-mounted.txt"
    with open(workspace_marker, "w", encoding="utf-8") as marker:
        marker.write("mounted-worktree-writable\n")

    disk_limited = False
    try:
        with open("/tmp/quota-probe.bin", "wb") as quota_probe:
            chunk = b"0" * (1024 * 1024)
            for _ in range(16):
                quota_probe.write(chunk)
            quota_probe.flush()
    except OSError as error:
        disk_limited = error.errno == errno.ENOSPC

    result = {
        "directBlocked": direct_target_blocked,
        "directInternetBlocked": direct_internet_blocked,
        "proxyAllowed": proxy_allowed,
        "proxyDenied": proxy_denied,
        "connectTunnelAllowed": connect_tunnel_allowed,
        "diskLimited": disk_limited,
        "worktreeWritable": os.path.exists(workspace_marker),
        "httpProxyConfigured": os.environ.get("HTTP_PROXY") == "http://harness-proxy:8080",
        "httpsProxyConfigured": os.environ.get("HTTPS_PROXY") == "http://harness-proxy:8080",
    }
    print(json.dumps(result, sort_keys=True))
    if not all(result.values()):
        raise SystemExit(1)


def run_fake_codex_app_server():
    for line in sys.stdin:
        request = json.loads(line)
        method = request.get("method")
        request_id = request.get("id")
        if method == "initialize":
            print(json.dumps({
                "id": request_id,
                "result": {
                    "userAgent": "harness-docker-fixture",
                    "platformFamily": "unix",
                    "platformOs": "linux",
                },
            }), flush=True)
        elif method == "thread/start":
            print(json.dumps({
                "id": request_id,
                "result": {"thread": {"id": "thr_docker", "ephemeral": False}},
            }), flush=True)
        elif method == "thread/resume":
            thread_id = request["params"]["threadId"]
            print(json.dumps({
                "id": request_id,
                "result": {"thread": {"id": thread_id, "ephemeral": False}},
            }), flush=True)
        elif method == "turn/start":
            with open("/workspace/docker-codex-ran.txt", "w", encoding="utf-8") as marker:
                marker.write("isolated app-server completed\n")
            final_message = json.dumps({
                "response": "Docker-isolated fixture complete.",
                "demands": [],
            }, separators=(",", ":"))
            print(json.dumps({
                "id": request_id,
                "result": {"turn": {"id": "turn_docker", "items": [], "status": "inProgress"}},
            }), flush=True)
            print(json.dumps({
                "method": "item/agentMessage/delta",
                "params": {
                    "threadId": "thr_docker",
                    "turnId": "turn_docker",
                    "itemId": "item_docker",
                    "delta": final_message,
                },
            }), flush=True)
            print(json.dumps({
                "method": "item/completed",
                "params": {
                    "threadId": "thr_docker",
                    "turnId": "turn_docker",
                    "completedAtMs": 1,
                    "item": {
                        "id": "item_docker",
                        "type": "agentMessage",
                        "text": final_message,
                    },
                },
            }), flush=True)
            print(json.dumps({
                "method": "turn/completed",
                "params": {
                    "threadId": "thr_docker",
                    "turn": {"id": "turn_docker", "items": [], "status": "completed"},
                },
            }), flush=True)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "probe"
    if mode == "target":
        ThreadedServer(("0.0.0.0", TARGET_PORT), TargetHandler).serve_forever()
    elif mode == "proxy":
        ThreadedServer(("0.0.0.0", 8080), ProxyHandler).serve_forever()
    elif mode == "probe":
        run_probe()
    elif mode == "fake-codex":
        run_fake_codex_app_server()
    else:
        raise SystemExit(f"unknown mode: {mode}")


if __name__ == "__main__":
    main()
