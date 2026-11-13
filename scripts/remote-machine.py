"""Transfer files and run PowerShell over an already trusted Windows SSH connection.

Install paramiko in a private environment, and supply AGING_SSH_PASSWORD only in
the invoking process environment. No credentials are written to disk.
"""

import argparse
import base64
import getpass
import json
import os
from pathlib import Path
import sys
import time

import paramiko


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--user", default="a")
    parser.add_argument("--timeout", type=int, default=120)
    actions = parser.add_subparsers(dest="action", required=True)
    run = actions.add_parser("run")
    source = run.add_mutually_exclusive_group(required=True)
    source.add_argument("--command")
    source.add_argument("--script", type=Path)
    actions.add_parser("session")
    for name in ("put", "get"):
        action = actions.add_parser(name)
        action.add_argument("source")
        action.add_argument("destination")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    password = os.environ.get("AGING_SSH_PASSWORD") or getpass.getpass("SSH password: ")
    with paramiko.SSHClient() as client:
        client.load_host_keys(str(Path.home() / ".ssh" / "known_hosts"))
        client.set_missing_host_key_policy(paramiko.RejectPolicy())
        client.connect(args.host, username=args.user, password=password,
                       look_for_keys=False, allow_agent=False, timeout=15,
                       auth_timeout=45, banner_timeout=45)
        client.get_transport().set_keepalive(10)
        if args.action == "session":
            print("READY", flush=True)
            for line in sys.stdin:
                request = json.loads(line)
                if request["action"] == "exit":
                    return 0
                try:
                    status = dispatch(client, request, args.timeout)
                    print(json.dumps({"completed": True, "exit_code": status}), flush=True)
                except Exception as error:
                    print(json.dumps({"completed": True, "error": str(error)}), flush=True)
            return 0
        request = vars(args).copy()
        return dispatch(client, request, args.timeout)


def dispatch(client, request, timeout):
        if request["action"] != "run":
            with client.open_sftp() as sftp:
                if request["action"] == "put":
                    sftp.put(request["source"], request["destination"].replace("\\", "/"))
                else:
                    Path(request["destination"]).parent.mkdir(parents=True, exist_ok=True)
                    sftp.get(request["source"].replace("\\", "/"), request["destination"])
            print(f"Transferred: {request['source']} -> {request['destination']}", flush=True)
            return 0
        script = request.get("command")
        if script is None:
            script = Path(request["script"]).read_text(encoding="utf-8-sig")
        script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);\n" + script
        encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
        _, stdout, stderr = client.exec_command(
            "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            timeout=timeout)
        channel = stdout.channel
        deadline = time.monotonic() + timeout
        output, errors = bytearray(), bytearray()
        while True:
            while channel.recv_ready():
                output.extend(channel.recv(65536))
            while channel.recv_stderr_ready():
                errors.extend(channel.recv_stderr(65536))
            if channel.exit_status_ready() and not channel.recv_ready() and not channel.recv_stderr_ready():
                break
            if time.monotonic() > deadline:
                channel.close()
                raise TimeoutError("Remote command exceeded its deadline")
            time.sleep(0.05)
        print(output.decode("utf-8", errors="replace"), end="", flush=True)
        print(errors.decode("utf-8", errors="replace"), end="", file=sys.stderr, flush=True)
        return channel.recv_exit_status()


if __name__ == "__main__":
    raise SystemExit(main())
