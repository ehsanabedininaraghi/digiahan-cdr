#!/bin/bash
set -euo pipefail

BASE="$(cd "$(dirname "$0")" && pwd)"
TARGET="/opt/digiahan-call-intelligence"
STAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP="/root/digiahan-call-intelligence-backup-$STAMP"
MANAGER="/etc/asterisk/manager_custom.conf"

echo "[1/7] Backup..."
mkdir -p "$BACKUP"
cp -a "$MANAGER" "$BACKUP/" 2>/dev/null || true
cp -a "$TARGET" "$BACKUP/" 2>/dev/null || true
cp -a /etc/systemd/system/digiahan-call-intelligence.service "$BACKUP/" 2>/dev/null || true

echo "[2/7] Install files..."
mkdir -p "$TARGET"
cp "$BASE/digiahan_call_intelligence.py" "$TARGET/"
cp "$BASE/config.json" /etc/digiahan-call-intelligence.json
chmod 700 "$TARGET/digiahan_call_intelligence.py"
chmod 600 /etc/digiahan-call-intelligence.json

echo "[3/7] Configure dedicated AMI user..."
touch "$MANAGER"
if grep -q '^\[digiahan_call_intelligence\]' "$MANAGER"; then
  python3 - "$MANAGER" "$BASE/manager_custom.conf.snippet" <<'PY'
import sys,re
path,snippet=sys.argv[1],sys.argv[2]
text=open(path,encoding="utf-8").read()
new=open(snippet,encoding="utf-8").read().strip()+"\n"
text=re.sub(r'(?ms)^\[digiahan_call_intelligence\].*?(?=^\[|\Z)',new,text)
open(path,"w",encoding="utf-8").write(text)
PY
else
  printf '\n' >> "$MANAGER"
  cat "$BASE/manager_custom.conf.snippet" >> "$MANAGER"
fi
chown asterisk:asterisk "$MANAGER"
chmod 640 "$MANAGER"

echo "[4/7] Reload AMI..."
asterisk -rx "manager reload"

echo "[5/7] Install systemd service..."
cp "$BASE/digiahan-call-intelligence.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now digiahan-call-intelligence.service

echo "[6/7] Verify..."
sleep 2
systemctl --no-pager --full status digiahan-call-intelligence.service || true

echo "[7/7] Test dashboard connectivity..."
curl -fsS --max-time 5 http://192.168.8.143:5088/api/version || true
echo
echo "Installation complete."
echo "Logs: journalctl -u digiahan-call-intelligence -f"
