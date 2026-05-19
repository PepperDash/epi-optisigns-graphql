"""
Shared utilities for OptiSigns test scripts.
"""

import json
import os
import sys
import urllib.error
import urllib.request
from datetime import datetime

GRAPHQL_ENDPOINT = "https://graphql-gateway.optisigns.com/graphql"


def load_env() -> None:
    """Load key=value pairs from .env in the same directory as this module."""
    env_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env")
    if not os.path.exists(env_path):
        return
    with open(env_path) as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            os.environ.setdefault(key.strip(), value.strip())


class _Tee:
    """Mirrors writes to multiple streams (e.g. stdout + log file)."""

    def __init__(self, *streams):
        self._streams = streams

    def write(self, data):
        for s in self._streams:
            s.write(data)

    def flush(self):
        for s in self._streams:
            s.flush()


def setup_logging(script_name: str = "") -> str:
    """
    Creates a timestamped log file under test-scripts/logs/ and redirects
    stdout/stderr so all output is written to both the console and the file.

    Filename format: YYYY-MM-DD_HH:MM:SS:tt-<script_name>.txt  (tt = centiseconds)

    Returns the path to the log file.
    """
    logs_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs")
    os.makedirs(logs_dir, exist_ok=True)

    now = datetime.now()
    cs = now.microsecond // 10000  # centiseconds (2 digits)
    ts = now.strftime("%Y-%m-%d_%H:%M:%S:") + f"{cs:02d}"
    # Strip .py extension and sanitize for use in a filename
    stem = os.path.splitext(script_name)[0] if script_name else "log"
    log_path = os.path.join(logs_dir, f"{ts}-{stem}.txt")

    log_file = open(log_path, "w", encoding="utf-8", buffering=1)
    if script_name:
        log_file.write(f"# script   : {script_name}\n")
        log_file.write(f"# timestamp: {now.isoformat()}\n\n")

    sys.stdout = _Tee(sys.__stdout__, log_file)
    sys.stderr = _Tee(sys.__stderr__, log_file)

    return log_path


def log_query(query: str, variables: dict = None) -> None:
    """
    Prints the GraphQL query/mutation and variables to stdout so they are
    captured in both the console output and the log file.

    When variables are provided, also prints a resolved view where $varName
    placeholders are substituted with their actual values.
    """
    import json as _json
    import re as _re

    print("=" * 60)
    print("GraphQL Request")
    print("=" * 60)
    print(query.strip())

    if variables:
        print("\nVariables:")
        print(_json.dumps(variables, indent=2))

        # Build a resolved copy of the query with $varName replaced by values
        resolved = query
        for key, value in variables.items():
            replacement = _json.dumps(value, indent=4) if isinstance(value, dict) else _json.dumps(value)
            resolved = _re.sub(rf"\${key}\b", replacement, resolved)

        print("\nResolved:")
        print(resolved.strip())

    print("=" * 60)
    print()


def execute_query(api_key: str, query: str, variables: dict = None) -> dict:
    """
    Logs the request, sends it to the GraphQL endpoint with Bearer auth,
    and returns the ``data`` dict from the response.

    Exits the process (via sys.exit(1)) on HTTP errors or GraphQL errors so
    individual scripts never need to repeat this boilerplate.
    """
    log_query(query, variables)

    body: dict = {"query": query}
    if variables is not None:
        body["variables"] = variables
    payload = json.dumps(body).encode("utf-8")

    req = urllib.request.Request(
        GRAPHQL_ENDPOINT,
        data=payload,
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_key}",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            response = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        print(f"HTTP Error {e.code}: {e.reason}", file=sys.stderr)
        try:
            print(e.read().decode("utf-8"), file=sys.stderr)
        except Exception:
            pass
        sys.exit(1)
    except urllib.error.URLError as e:
        print(f"Connection error: {e.reason}", file=sys.stderr)
        sys.exit(1)

    if response.get("errors"):
        print("GraphQL errors:", file=sys.stderr)
        for err in response["errors"]:
            print(f"  - {err.get('message', err)}", file=sys.stderr)
        sys.exit(1)

    return response.get("data") or {}
