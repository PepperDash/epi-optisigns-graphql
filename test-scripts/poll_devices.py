"""
Poll the OptiSigns GraphQL API for available devices (screens/players).

Usage:
    python poll_devices.py [--api-key YOUR_API_KEY]

If --api-key is omitted the script reads OPTISIGNS_API_KEY from test-scripts/.env.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _utils import load_env, setup_logging, execute_query

DEVICES_QUERY = """
query Devices($teamId: String) {
    devices(query: {}, teamId: $teamId) {
        page {
            edges {
                cursor
                node {
                    _id
                    deviceName
                    UUID
                    pairingCode
                    currentType
                    currentAssetId
                    currentPlaylistId
                    localAppVersion
                }
            }
        }
    }
}
"""


def fetch_devices(api_key: str, team_id: str = None) -> list:
    variables = {"teamId": team_id}
    data = execute_query(api_key, DEVICES_QUERY, variables)
    edges = data.get("devices", {}).get("page", {}).get("edges", [])
    return [edge["node"] for edge in edges if edge.get("node")]


def main():
    load_env()
    log_path = setup_logging(os.path.basename(__file__))
    print(f"Logging to: {log_path}\n")
    parser = argparse.ArgumentParser(
        description="Poll OptiSigns for available devices"
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

    devices = fetch_devices(args.api_key, args.team_id)

    if not devices:
        print("No devices found.")
        return

    print(f"Found {len(devices)} device(s):\n")
    print(f"{'ID':<28} {'Name':<30} {'Type':<14} {'Status/Version':<18} {'Playlist ID'}")
    print("-" * 120)
    for d in devices:
        print(
            f"{d['_id']:<28} "
            f"{(d.get('deviceName') or '—'):<30} "
            f"{(d.get('currentType') or '—'):<14} "
            f"{(d.get('localAppVersion') or '—'):<18} "
            f"{d.get('currentPlaylistId') or '—'}"
        )


if __name__ == "__main__":
    main()
