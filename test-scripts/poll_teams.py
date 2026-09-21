"""
Poll the OptiSigns GraphQL API for teams in the account.

Usage:
    python poll_teams.py [--api-key YOUR_API_KEY] [--team-id TEAM_ID]

If --api-key is omitted the script reads OPTISIGNS_API_KEY from test-scripts/.env.
Use --team-id to filter results to a specific team.
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


TEAMS_QUERY = """
query {
    teams {
        page {
            edges {
                node {
                    _id
                    name
                    description
                    accountId
                    createdAt
                }
            }
        }
    }
}
"""

TEAMS_QUERY_FILTERED = """
query TeamsFiltered($teamId: String) {
    teams(teamId: $teamId) {
        page {
            edges {
                node {
                    _id
                    name
                    description
                    accountId
                    createdAt
                }
            }
        }
    }
}
"""


def fetch_teams(api_key: str, team_id: str = None) -> list:
    if team_id:
        body = {"query": TEAMS_QUERY_FILTERED, "variables": {"teamId": team_id}}
        log_query(TEAMS_QUERY_FILTERED, {"teamId": team_id})
    else:
        body = {"query": TEAMS_QUERY}
        log_query(TEAMS_QUERY)

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
            resp_body = json.loads(resp.read().decode("utf-8"))
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

    if "errors" in resp_body:
        print("GraphQL errors:", file=sys.stderr)
        for err in resp_body["errors"]:
            print(f"  {err.get('message')}", file=sys.stderr)
        sys.exit(1)

    edges = resp_body["data"]["teams"]["page"]["edges"]
    return [e["node"] for e in edges]


def main():
    _load_env()
    setup_logging(os.path.basename(__file__))

    parser = argparse.ArgumentParser(description="List teams in the OptiSigns account.")
    parser.add_argument(
        "--api-key", default=os.environ.get("OPTISIGNS_API_KEY"),
        help="OptiSigns API key (default: OPTISIGNS_API_KEY in .env)"
    )
    parser.add_argument(
        "--team-id", default=None,
        help="Filter to a specific team ID"
    )
    args = parser.parse_args()

    if not args.api_key:
        print("Error: --api-key is required or set OPTISIGNS_API_KEY in .env", file=sys.stderr)
        sys.exit(1)

    teams = fetch_teams(args.api_key, args.team_id)

    if not teams:
        print("No teams found.")
        return

    col_id   = max(len(t.get("_id", "") or "") for t in teams)
    col_name = max(len(t.get("name", "") or "") for t in teams)

    header = f"{'ID':<{col_id}}  {'Name':<{col_name}}  {'Account ID':<24}  Created At"
    print(f"\n{header}")
    print("-" * len(header))
    for t in teams:
        print(
            f"{t.get('_id', ''):<{col_id}}"
            f"  {t.get('name', ''):<{col_name}}"
            f"  {t.get('accountId', ''):<24}"
            f"  {t.get('createdAt', '')}"
        )
    print(f"\nTotal: {len(teams)} team(s)")


if __name__ == "__main__":
    main()
