"""
Poll the OptiSigns GraphQL API for available playlists that can be pushed to a screen.

Usage:
    python poll_playlists.py [--api-key YOUR_API_KEY]

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

PLAYLISTS_QUERY = """
query Playlists($teamId: String) {
    playlists(query: {}, teamId: $teamId) {
        page {
            edges {
                node {
                    _id
                    name
                    totalDuration
                    tags
                }
            }
        }
    }
}
"""


def fetch_playlists(api_key: str, team_id: str = None) -> list:
    variables = {"teamId": team_id}
    log_query(PLAYLISTS_QUERY, variables)
    payload = json.dumps({"query": PLAYLISTS_QUERY, "variables": variables}).encode("utf-8")

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

    edges = body.get("data", {}).get("playlists", {}).get("page", {}).get("edges", [])
    return [edge["node"] for edge in edges if edge.get("node")]


def main():
    _load_env()
    log_path = setup_logging(os.path.basename(__file__))
    print(f"Logging to: {log_path}\n")
    parser = argparse.ArgumentParser(
        description="Poll OptiSigns for available playlists"
    )
    parser.add_argument(
        "--api-key", default=os.environ.get("OPTISIGNS_API_KEY"),
        help="OptiSigns API key (Bearer token); falls back to OPTISIGNS_API_KEY in .env"
    )
    parser.add_argument(
        "--team-id", default=None,
        help="Filter results to a specific team ID (e.g. $TEAM_HOUSTON)"
    )
    args = parser.parse_args()
    if not args.api_key:
        parser.error("--api-key is required or set OPTISIGNS_API_KEY in test-scripts/.env")

    playlists = fetch_playlists(args.api_key, args.team_id)

    if not playlists:
        print("No playlists found.")
        return

    print(f"Found {len(playlists)} playlist(s):\n")
    print(f"{'ID':<28} {'Name':<40} {'Duration (s)':<14} {'Tags'}")
    print("-" * 100)
    for p in playlists:
        tags = ", ".join(p.get("tags") or []) or "—"
        duration = p.get("totalDuration") or "—"
        print(f"{p['_id']:<28} {p['name']:<40} {str(duration):<14} {tags}")


if __name__ == "__main__":
    main()
