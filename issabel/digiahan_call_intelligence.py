#!/usr/bin/env python3
import json
import logging
import re
import socket
import time
import urllib.request
from datetime import datetime, timezone

CONFIG_PATH="/etc/digiahan-call-intelligence.json"
LOG=logging.getLogger("digiahan-call-intelligence")
logging.basicConfig(level=logging.INFO,format="%(asctime)s %(levelname)s %(message)s")

INTERNAL_RE=re.compile(r"^(20[1-8]|21[1-9]|220|22[1-6])$")

def normalize_phone(value):
    digits=re.sub(r"\D+","",value or "")
    if digits.startswith("0098"):
        digits="0"+digits[4:]
    elif digits.startswith("98") and len(digits)>=12:
        digits="0"+digits[2:]
    elif len(digits)==10 and not digits.startswith("0"):
        digits="0"+digits
    return digits

def send_action(sock,fields):
    payload="".join("%s: %s\r\n"%(k,v) for k,v in fields.items())+"\r\n"
    sock.sendall(payload.encode("utf-8"))

def read_packet(file_obj):
    lines=[]
    while True:
        line=file_obj.readline()
        if not line:
            raise EOFError()
        line=line.decode("utf-8","replace").rstrip("\r\n")
        if line=="":
            break
        lines.append(line)
    result={}
    for line in lines:
        if ":" in line:
            k,v=line.split(":",1)
            result[k.strip()]=v.strip()
    return result

def post_event(config,extension,caller,linkedid,channel):
    body=json.dumps({
        "extension":extension,
        "callerNumber":caller,
        "linkedId":linkedid or None,
        "channel":channel or None,
        "eventTimeUtc":datetime.now(timezone.utc).isoformat()
    }).encode("utf-8")
    req=urllib.request.Request(
        config["dashboard_url"].rstrip("/")+"/api/voip/events",
        data=body,
        headers={
            "Content-Type":"application/json",
            "X-Voip-Token":config["api_token"]
        },
        method="POST")
    with urllib.request.urlopen(req,timeout=8) as response:
        response.read()

def main():
    with open(CONFIG_PATH,"r",encoding="utf-8") as f:
        config=json.load(f)

    recent={}
    while True:
        try:
            sock=socket.create_connection(("127.0.0.1",5038),10)
            file_obj=sock.makefile("rb")
            read_packet(file_obj)
            send_action(sock,{
                "Action":"Login",
                "Username":config["ami_username"],
                "Secret":config["ami_secret"],
                "Events":"on"
            })
            login=read_packet(file_obj)
            if login.get("Response")!="Success":
                raise RuntimeError("AMI login failed: "+str(login))
            LOG.info("AMI connected")

            while True:
                event=read_packet(file_obj)
                event_name=event.get("Event","")
                if event_name not in ("Newchannel","DialBegin","Newstate"):
                    continue

                channel=event.get("Channel","")
                dest_channel=event.get("DestChannel","")
                exten=event.get("Exten","")
                dest_exten=event.get("DestExten","")
                caller=normalize_phone(event.get("CallerIDNum",""))
                connected=normalize_phone(event.get("ConnectedLineNum",""))

                extension=""
                for candidate in (dest_exten,exten):
                    if INTERNAL_RE.match(candidate or ""):
                        extension=candidate
                        break
                if not extension:
                    for ch in (dest_channel,channel):
                        m=re.search(r"SIP/(\d{3})-",ch or "")
                        if m and INTERNAL_RE.match(m.group(1)):
                            extension=m.group(1)
                            break

                external=""
                for candidate in (caller,connected):
                    if len(candidate)>4 and not INTERNAL_RE.match(candidate):
                        external=candidate
                        break

                if not extension or not external:
                    continue

                linkedid=event.get("Linkedid") or event.get("Uniqueid") or ""
                key="%s|%s|%s"%(extension,external,linkedid)
                now=time.time()
                if key in recent and now-recent[key]<20:
                    continue
                recent[key]=now
                recent={k:v for k,v in recent.items() if now-v<120}

                try:
                    post_event(config,extension,external,linkedid,channel)
                    LOG.info("Sent ring event extension=%s caller=%s",extension,external)
                except Exception:
                    LOG.exception("Dashboard event delivery failed")
        except Exception:
            LOG.exception("AMI connection loop failed; retrying")
            time.sleep(5)

if __name__=="__main__":
    main()
