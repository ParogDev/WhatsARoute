#!/usr/bin/env bash
# FALLBACK ONLY. Presses normally go through the AI Bridge to the MCP's F8 dispatcher, which starts a fresh Claude session
# per press (docs/feedback-agent.md). Use this only while the bridge or the dispatcher is down, never next to it (a press
# would be handled twice).
# The agent's half of the live route loop: blocks until the player presses the feedback key (a "feedback" line lands
# in feedback.jsonl), then prints every line written since it started (the trace leading up to it, then the press).
# Run it as a background task; answer by editing route.live.json in the same folder (its "Message" shows in game).
#   bash tools/feedback-wait.sh [timeout-seconds]      (default 3600)
dir="${HEXILE_ROUTE_LIVE:-$LOCALAPPDATA/Hexile/WhatsARoute}"
f="$dir/feedback.jsonl"
mkdir -p "$dir"; touch "$f"
start=$(wc -l < "$f")
deadline=$(( $(date +%s) + ${1:-3600} ))
while [ "$(date +%s)" -lt "$deadline" ]; do
  if tail -n +"$((start + 1))" "$f" | grep -q '"Kind":"feedback"'; then
    tail -n +"$((start + 1))" "$f"
    exit 0
  fi
  sleep 1
done
echo "timeout: no feedback in ${1:-3600}s; trace since start:"
tail -n +"$((start + 1))" "$f"
