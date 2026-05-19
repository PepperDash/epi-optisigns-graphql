"""
Assign an asset or playlist to an OptiSigns device via the updateDevice mutation.

Usage:
    # Assign an asset (default)
    python assign_content.py --type ASSET \
        --device-id 6682d6d553fca60012953e17 \
        --content-id uRQynMhDsJ6QY35Wf

    # Assign a playlist
    python assign_content.py --type PLAYLIST \
        --device-id 6126edf99834540019b30ff1 \
        --content-id d87B9ARKPyH8YYBbs

If --api-key is omitted the script reads OPTISIGNS_API_KEY from test-scripts/.env.

--type      : ASSET or PLAYLIST (default: ASSET)
--device-id : The device _id to update
--content-id: The asset _id or playlist _id to assign
--device-name: Display name for the device (default: GraphAPI Test)
--orientation: LANDSCAPE or PORTRAIT (default: LANDSCAPE)
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

UPDATE_DEVICE_MUTATION = """
mutation UpdateDevice(
    $id: String!,
    $teamId: String!,
    $payload: UpdateDeviceInput!
) {
    updateDevice(_id: $id, teamId: $teamId, payload: $payload) {
        _id
        deviceName
        UUID
        pairingCode
        currentType
        currentAssetId
        localAppVersion
    }
}
"""

# Default test values keyed by content type
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


DEFAULTS = {
    "ASSET": {
        "device_id": "6682d6d553fca60012953e17",
        "content_id": "uRQynMhDsJ6QY35Wf",
    },
    "PLAYLIST": {
        "device_id": "6126edf99834540019b30ff1",
        "content_id": "d87B9ARKPyH8YYBbs",
    },
}


def assign_content(
    api_key: str,
    device_id: str,
    content_id: str,
    content_type: str,
    device_name: str,
    orientation: str,
    team_id: str,
) -> dict:
    variables = {
        "id": device_id,
        "teamId": team_id,
        "payload": {
            "deviceName": device_name,
            "currentType": content_type,
            "currentAssetId": content_id,
            "orientation": orientation,
        },
    }

    log_query(UPDATE_DEVICE_MUTATION, variables)
    payload = json.dumps({"query": UPDATE_DEVICE_MUTATION, "variables": variables}).encode("utf-8")

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

    return body.get("data", {}).get("updateDevice")


def main():
    _load_env()
    log_path = setup_logging(os.path.basename(__file__))
    print(f"Logging to: {log_path}\n")
    parser = argparse.ArgumentParser(
        description="Assign an asset or playlist to an OptiSigns device"
    )
    parser.add_argument(
        "--api-key", default=os.environ.get("OPTISIGNS_API_KEY"),
        help="OptiSigns API key (Bearer token); falls back to OPTISIGNS_API_KEY in .env"
    )
    parser.add_argument(
        "--type", dest="content_type", default="ASSET", choices=["ASSET", "PLAYLIST"],
        help="Content type to assign: ASSET or PLAYLIST (default: ASSET)"
    )
    parser.add_argument("--device-id", default=None, help="Device _id (defaults per --type)")
    parser.add_argument("--content-id", default=None, help="Asset or playlist _id to assign (defaults per --type)")
    parser.add_argument("--device-name", default="GraphAPI Test", help="Display name for the device")
    parser.add_argument(
        "--team-id", default=os.environ.get("TEAM_ID", "1"),
        help="OptiSigns team ID (default: TEAM_ID in .env, fallback \"1\")"
    )
    parser.add_argument(
        "--orientation", default="LANDSCAPE", choices=["LANDSCAPE", "PORTRAIT"],
        help="Screen orientation (default: LANDSCAPE)"
    )
    args = parser.parse_args()
    if not args.api_key:
        parser.error("--api-key is required or set OPTISIGNS_API_KEY in test-scripts/.env")

    defaults = DEFAULTS[args.content_type]
    device_id = args.device_id or defaults["device_id"]
    content_id = args.content_id or defaults["content_id"]

    print(f"Assigning {args.content_type.lower()} '{content_id}' to device '{device_id}' (teamId: {args.team_id})...")
    result = assign_content(
        args.api_key, device_id, content_id, args.content_type, args.device_name, args.orientation, args.team_id
    )

    if not result:
        print("Mutation returned no data — assignment may have failed.", file=sys.stderr)
        sys.exit(1)

    print("\nDevice updated successfully:\n")
    print(f"  _id            : {result.get('_id')}")
    print(f"  deviceName     : {result.get('deviceName')}")
    print(f"  UUID           : {result.get('UUID')}")
    print(f"  pairingCode    : {result.get('pairingCode')}")
    print(f"  currentType    : {result.get('currentType')}")
    print(f"  currentAssetId : {result.get('currentAssetId')}")
    print(f"  localAppVersion: {result.get('localAppVersion')}")


if __name__ == "__main__":
    main()
