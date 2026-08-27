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

def post_status(config,linkedid,state,extension=None):
    if not linkedid:
        return
    body=json.dumps({
        "linkedId":linkedid,
        "state":state,
        "extension":extension or None,
        "eventTimeUtc":datetime.now(timezone.utc).isoformat()
    }).encode("utf-8")
    req=urllib.request.Request(
        config["dashboard_url"].rstrip("/")+"/api/voip/call-status",
        data=body,
        headers={
            "Content-Type":"application/json",
            "X-Voip-Token":config["api_token"]
        },
        method="POST")
    with urllib.request.urlopen(req,timeout=5) as response:
        response.read()

def main():
    with open(CONFIG_PATH,"r",encoding="utf-8") as f:
        config=json.load(f)

    recent={}
    calls={}
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
                if event_name not in ("Newchannel","DialBegin","Newstate","Hangup"):
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

                linkedid=event.get("Linkedid") or event.get("Uniqueid") or ""
                tracked=calls.get(linkedid,{})
                if not external:
                    external=tracked.get("caller","")
                if not extension:
                    m=re.search(r"SIP/(\d{3})-",channel or "")
                    if m and INTERNAL_RE.match(m.group(1)):
                        extension=m.group(1)

                if event_name=="Newstate" and event.get("ChannelStateDesc")=="Up":
                    if linkedid and extension and (external or tracked):
                        calls[linkedid]={
                            "caller":external or tracked.get("caller",""),
                            "answered_extension":extension
                        }
                        try:
                            post_status(config,linkedid,"ANSWERED",extension)
                            LOG.info("Call answered linkedid=%s extension=%s",linkedid,extension)
                        except Exception:
                            LOG.exception("Dashboard answer status delivery failed")
                    continue

                if event_name=="Hangup":
                    answered_extension=tracked.get("answered_extension")
                    is_answered_leg=answered_extension and extension==answered_extension
                    is_external_leg=not extension
                    if linkedid and tracked and (is_answered_leg or is_external_leg):
                        try:
                            post_status(config,linkedid,"ENDED",answered_extension)
                            LOG.info("Call ended linkedid=%s extension=%s",linkedid,answered_extension or "")
                        except Exception:
                            LOG.exception("Dashboard end status delivery failed")
                        calls.pop(linkedid,None)
                    continue

                if not extension or not external:
                    continue

                if linkedid:
                    current=calls.get(linkedid,{})
                    current["caller"]=external
                    calls[linkedid]=current
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
