import argparse
import asyncio
import json
import sys
from pathlib import Path

import asyncssh


class TestServer(asyncssh.SSHServer):
    def __init__(self, allowed_key):
        self.allowed_key = allowed_key

    def begin_auth(self, username):
        return True

    def password_auth_supported(self):
        return True

    def validate_password(self, username, password):
        return username == "root" and password == "test-password-local-only"

    def public_key_auth_supported(self):
        return True

    def validate_public_key(self, username, key):
        return username == "root" and key == self.allowed_key


async def run_fixed_command(process):
    command = process.command
    if command == "id -u":
        process.stdout.write("0\n")
        process.exit(0)
    elif command == "sleep 5":
        await asyncio.sleep(5)
        process.stdout.write("finished\n")
        process.exit(0)
    else:
        process.stderr.write("fixture rejected command\n")
        process.exit(127)


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--public-key", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--private-key-source", required=True)
    parser.add_argument("--encrypted-key-output", required=True)
    args = parser.parse_args()
    # These paths belong exclusively to the C# fixture's disposable temp directory.
    client_private = asyncssh.read_private_key(args.private_key_source)
    Path(args.encrypted_key_output).write_bytes(client_private.export_private_key(
        "openssh", passphrase="fixture-only-passphrase"))

    client_key = asyncssh.read_public_key(args.public_key)
    host_key = asyncssh.generate_private_key("ssh-ed25519")
    print("fixture-key-ready", file=sys.stderr, flush=True)
    listener = await asyncssh.create_server(
        lambda: TestServer(client_key),
        "127.0.0.1",
        args.port,
        server_host_keys=[host_key],
        process_factory=run_fixed_command,
        sftp_factory=lambda chan: asyncssh.SFTPServer(chan, chroot=args.root),
    )
    print("fixture-listener-ready", file=sys.stderr, flush=True)
    pub = host_key.export_public_key("openssh").decode("ascii").split()
    print(json.dumps({"key_type": pub[0], "public_key": pub[1]}), flush=True)
    await listener.wait_closed()


asyncio.run(main())
