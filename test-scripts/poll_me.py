"""
Poll the OptiSigns GraphQL API for the current authenticated user (me query).

Usage:
    python poll_me.py [--api-key YOUR_API_KEY]

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


ME_QUERY = """
query {
    me {
        _id
        accountId
        firstName
        lastName
        name
        email
        username
    }
}
"""


def fetch_me(api_key: str) -> dict:
    log_query(ME_QUERY)
    payload = json.dumps({"query": ME_QUERY}).encode("utf-8")

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

    if "errors" in body:
        print("GraphQL errors:", file=sys.stderr)
        for err in body["errors"]:
            print(f"  {err.get('message')}", file=sys.stderr)
        sys.exit(1)

    return body["data"]["me"]


def main():
    _load_env()
    setup_logging(os.path.basename(__file__))

    parser = argparse.ArgumentParser(description="Fetch current user info from OptiSigns.")
    parser.add_argument(
        "--api-key", default=os.environ.get("OPTISIGNS_API_KEY"),
        help="OptiSigns API key (default: OPTISIGNS_API_KEY in .env)"
    )
    args = parser.parse_args()

    if not args.api_key:
        print("Error: --api-key is required or set OPTISIGNS_API_KEY in .env", file=sys.stderr)
        sys.exit(1)

    me = fetch_me(args.api_key)

    print("\n--- Current User ---")
    print(f"  ID:         {me.get('_id')}")
    print(f"  Account ID: {me.get('accountId')}")
    print(f"  Name:       {me.get('firstName')} {me.get('lastName')}".strip())
    print(f"  Display:    {me.get('name')}")
    print(f"  Email:      {me.get('email')}")
    print(f"  Username:   {me.get('username')}")


if __name__ == "__main__":
    main()
