#!/bin/bash
set -euo pipefail

CONFIG="/etc/digiahan-call-intelligence.json"
EXTENSION="${1:-201}"
CALLER="${2:-09121234567}"

if [ ! -f "$CONFIG" ]; then
  echo "Config not found: $CONFIG"
  exit 1
fi

DASHBOARD_URL="$(python3 -c 'import json;print(json.load(open("/etc/digiahan-call-intelligence.json"))["dashboard_url"])')"
TOKEN="$(python3 -c 'import json;print(json.load(open("/etc/digiahan-call-intelligence.json"))["api_token"])')"

echo "Sending test ring event..."
echo "Extension: $EXTENSION"
echo "Caller: $CALLER"
echo "Dashboard: $DASHBOARD_URL"

curl -i -sS --max-time 10 \
  -X POST "$DASHBOARD_URL/api/voip/events" \
  -H "Content-Type: application/json" \
  -H "X-Voip-Token: $TOKEN" \
  --data "{\"extension\":\"$EXTENSION\",\"callerNumber\":\"$CALLER\",\"linkedId\":\"manual-test-$(date +%s)\",\"channel\":\"manual-test\"}"

echo
echo "Done. Open: $DASHBOARD_URL/agent/$EXTENSION"
