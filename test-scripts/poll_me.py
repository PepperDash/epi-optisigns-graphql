"""
Poll the OptiSigns GraphQL API to verify the API key and summarise account access.

The OptiSigns `me` query requires user-level OAuth authentication and returns
API_NOT_AVAILABLE when called with an API key (Bearer token).  API keys are
service-level credentials.  This script uses the `devices` query instead —
it returns all devices visible to the key without requiring a teamId, providing
an equivalent "who am I / what do I have access to" health-check.

Usage:
    python poll_me.py [--api-key YOUR_API_KEY]

If --api-key is omitted the script reads OPTISIGNS_API_KEY from test-scripts/.env.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _utils import load_env, setup_logging, execute_query

# The `me` query returns API_NOT_AVAILABLE for API key auth.
# The unfiltered `devices` query works without a teamId and serves as a
# reliable identity/health-check for service-level API keys.
DEVICES_QUERY = """
query {
    devices(query: {}) {
        page {
            edges {
                node {
                    _id
                    deviceName
                    currentType
                    status
                    localAppVersion
                }
            }
        }
    }
}
"""


def fetch_device_summary(api_key: str) -> list:
    data = execute_query(api_key, DEVICES_QUERY)
    edges = data.get("devices", {}).get("page", {}).get("edges", [])
    return [e["node"] for e in edges if e.get("node")]


def main():
    load_env()
    log_path = setup_logging(os.path.basename(__file__))
    print(f"Logging to: {log_path}\n")

    parser = argparse.ArgumentParser(
        description="Verify an OptiSigns API key and summarise account access."
    )
    parser.add_argument(
        "--api-key", default=os.environ.get("OPTISIGNS_API_KEY"),
        help="OptiSigns API key (default: OPTISIGNS_API_KEY in .env)"
    )
    args = parser.parse_args()

    if not args.api_key:
        parser.error("--api-key is required or set OPTISIGNS_API_KEY in test-scripts/.env")

    devices = fetch_device_summary(args.api_key)

    print(f"API key is valid.  Devices accessible: {len(devices)}\n")

    if devices:
        print(f"{'Device ID':<28} {'Name':<35} {'Type':<14} {'Status'}")
        print("-" * 90)
        for d in devices:
            print(
                f"{d['_id']:<28} "
                f"{(d.get('deviceName') or '—'):<35} "
                f"{(d.get('currentType') or '—'):<14} "
                f"{(d.get('status') or '—')}"
            )


if __name__ == "__main__":
    main()
