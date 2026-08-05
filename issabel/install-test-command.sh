#!/bin/bash
set -euo pipefail
BASE="$(cd "$(dirname "$0")" && pwd)"
cp "$BASE/test-ring.sh" /usr/local/bin/digiahan-test-ring
chmod 755 /usr/local/bin/digiahan-test-ring
echo "Installed: /usr/local/bin/digiahan-test-ring"
echo "Example:"
echo "  digiahan-test-ring 201 09121234567"
