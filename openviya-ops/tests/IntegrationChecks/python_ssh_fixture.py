"""Loopback-only AsyncSSH fixture. It accepts one disposable public key and only forwards to one local HTTP port."""
import argparse
import asyncio
import json
import sys

import asyncssh


class FixtureServer(asyncssh.SSHServer):
    def __init__(self, allowed_key, target_port):
        self.allowed_key = allowed_key
        self.target_port = target_port

    def begin_auth(self, username):
        print(f"begin_auth username_ok={username == 'root'}", file=sys.stderr, flush=True)
        return True

    def public_key_auth_supported(self):
        return True

    def password_auth_supported(self):
        return False

    def validate_public_key(self, username, key):
        accepted = username == "root" and key.get_fingerprint("sha256") == self.allowed_key.get_fingerprint("sha256")
        print(f"public_key accepted={accepted}", file=sys.stderr, flush=True)
        return accepted

    def connection_requested(self, dest_host, dest_port, orig_host, orig_port):
        return dest_host in ("127.0.0.1", "localhost") and dest_port == self.target_port


async def handle_process(process):
    # The SSH connection check sends this exact fixed command. Never invoke a shell.
    print(f"session command_is_exit={process.command == 'exit'}", file=sys.stderr, flush=True)
    if process.command == "exit":
        process.exit(0)
    else:
        process.stderr.write("fixture permits only the fixed exit check command\n")
        process.exit(126)


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--target-port", type=int, required=True)
    parser.add_argument("--client-public-key", required=True)
    args = parser.parse_args()

    with open(args.client_public_key, "r", encoding="ascii") as key_file:
        allowed_key = asyncssh.import_public_key(key_file.read())
    host_key = asyncssh.generate_private_key("ssh-ed25519")
    host_public = host_key.export_public_key().decode("ascii").strip()
    host_type, host_blob = host_public.split()[:2]
    fingerprint = host_key.get_fingerprint("sha256")

    server = await asyncssh.create_server(
        lambda: FixtureServer(allowed_key, args.target_port),
        "127.0.0.1",
        args.port,
        server_host_keys=[host_key],
        process_factory=handle_process,
        encoding="utf-8",
    )
    # This owned stdout line is the independent expected identity for ssh-keyscan.
    print(json.dumps({"ready": True, "hostKeyType": host_type, "hostKey": host_blob, "fingerprint": fingerprint}), flush=True)
    try:
        await asyncio.Event().wait()
    finally:
        server.close()
        await server.wait_closed()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
    except Exception as exc:
        print(json.dumps({"ready": False, "error": type(exc).__name__}), flush=True)
        raise
