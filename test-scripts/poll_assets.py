"""
Poll the OptiSigns GraphQL API for assets.

Usage:
    # All assets
    python poll_assets.py

    # Filter by original file name
    python poll_assets.py --name "Houston Weather Test"

If --api-key is omitted the script reads OPTISIGNS_API_KEY from test-scripts/.env.
"""

import argparse
import json
import os
import sys
import urllib.request
import urllib.error

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _utils import setup_logging, log_query

GRAPHQL_ENDPOINT = "https://graphql-gateway.optisigns.com/graphql"


def _load_env():
    """Load key=value pairs from .env in the same directory as this script."""
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

ASSETS_QUERY_FILTERED = """
query GetAssets($originalFileName: String!, $teamId: String) {
    assets(query: { originalFileName: $originalFileName }, teamId: $teamId) {
        page {
            edges {
                cursor
                node {
                    _id
                    appType
                    fileType
                    name
                    filename
                }
            }
        }
    }
}
"""

ASSETS_QUERY_ALL = """
query Assets($teamId: String) {
    assets(query: {}, teamId: $teamId) {
        page {
            edges {
                cursor
                node {
                    _id
                    appType
                    fileType
                    name
                    filename
                }
            }
        }
    }
}
"""


def fetch_assets(api_key: str, name: str = None, team_id: str = None) -> list:
    if name:
        body = {"query": ASSETS_QUERY_FILTERED, "variables": {"originalFileName": name, "teamId": team_id}}
    else:
        body = {"query": ASSETS_QUERY_ALL, "variables": {"teamId": team_id}}

    log_query(body["query"], body.get("variables"))
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
            body = json.loads(resp.read().decode("utf-8"))
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

    if "errors" in body and body["errors"]:
        print("GraphQL errors:", file=sys.stderr)
        for err in body["errors"]:
            print(f"  - {err.get('message', err)}", file=sys.stderr)
        sys.exit(1)

    edges = body.get("data", {}).get("assets", {}).get("page", {}).get("edges", [])
    return [edge["node"] for edge in edges if edge.get("node")]


def main():
    _load_env()
    log_path = setup_logging(os.path.basename(__file__))
    print(f"Logging to: {log_path}\n")
    parser = argparse.ArgumentParser(
        description="Poll OptiSigns for assets by original file name"
    )
    parser.add_argument(
        "--api-key", default=os.environ.get("OPTISIGNS_API_KEY"),
        help="OptiSigns API key (Bearer token); falls back to OPTISIGNS_API_KEY in .env"
    )
    parser.add_argument(
        "--name", default=None, help="Filter by original file name; omit to return all assets"
    )
    parser.add_argument(
        "--team-id", default=None,
        help="Filter results to a specific team ID (e.g. $TEAM_HOUSTON)"
    )
    args = parser.parse_args()
    if not args.api_key:
        parser.error("--api-key is required or set OPTISIGNS_API_KEY in test-scripts/.env")

    assets = fetch_assets(args.api_key, args.name, args.team_id)

    if not assets:
        msg = f'No assets found matching "{args.name}".' if args.name else "No assets found."
        print(msg)
        return

    header = f'Found {len(assets)} asset(s) matching "{args.name}":\n' if args.name else f"Found {len(assets)} asset(s):\n"
    print(header)
    print(f"{'ID':<28} {'Name':<30} {'App Type':<14} {'File Type':<14} {'Filename'}")
    print("-" * 120)
    for a in assets:
        print(
            f"{a['_id']:<28} "
            f"{(a.get('name') or '—'):<30} "
            f"{(a.get('appType') or '—'):<14} "
            f"{(a.get('fileType') or '—'):<14} "
            f"{a.get('filename') or '—'}"
        )


if __name__ == "__main__":
    main()
