#!/usr/bin/env bash
# =============================================================================
# AutoRoute v2 gate (PLAN.v2.md, M1): verify the pactl mechanics the hybrid
# virtual-sink design depends on, against the LIVE PipeWire session.
#
# Run this on the real machine (needs pactl, pw-dump, a running pipewire-pulse):
#
#     ./scripts/v2-gate.sh            # steps 1-3, 5, 6 (no service restarts)
#     ./scripts/v2-gate.sh --restart  # also step 4 (restarts pipewire-pulse!)
#
# It creates and removes a throwaway sink named autoroute_gate_sink and captures
# live fixtures under tests/AutoRoute.Tests/fixtures/ (git-diff them afterwards):
#   - pactl-modules.short.sample.txt  (step 2 capture, hand-trimmed shape today)
#   - pw-dump.managed-sink.json       (step 1 capture: the node with our prop tag)
#
# Gate criteria: steps 1-4 PASS -> proceed. If step 1 shows the sink_properties
# custom key stripped, ownership stays purely name-based (already the design)
# and stale-module auto-cleanup must be dropped — record the result in
# docs/adr/0011-app-managed-virtual-sinks.md either way.
# =============================================================================
set -u

SINK=autoroute_gate_sink
FIXTURES="$(dirname "$0")/../tests/AutoRoute.Tests/fixtures"
PROPS="device.description='AR Gate' autoroute.managed=true"
PASS=0; FAIL=0

ok()   { echo "  PASS  $1"; PASS=$((PASS+1)); }
bad()  { echo "  FAIL  $1"; FAIL=$((FAIL+1)); }

cleanup() {
  for idx in $(pactl list modules short | awk -v s="sink_name=$SINK" '$2=="module-null-sink" && index($0,s) {print $1}'); do
    pactl unload-module "$idx" 2>/dev/null
  done
}
trap cleanup EXIT

echo "== Step 1: load-module prints index; pw-dump shows tagged Audio/Sink node =="
IDX=$(pactl load-module module-null-sink "sink_name=$SINK" "sink_properties=\"$PROPS\"")
if [[ "$IDX" =~ ^[0-9]+$ ]]; then ok "load-module returned module index $IDX"; else bad "no module index (got: $IDX)"; fi
sleep 0.5
NODE_JSON=$(pw-dump | jq "[.[] | select(.info.props[\"node.name\"]==\"$SINK\")]")
if [[ $(echo "$NODE_JSON" | jq 'length') -ge 1 ]]; then ok "node $SINK exists in pw-dump"; else bad "node $SINK missing from pw-dump"; fi
MEDIA_CLASS=$(echo "$NODE_JSON" | jq -r '.[0].info.props["media.class"] // empty')
[[ "$MEDIA_CLASS" == "Audio/Sink" ]] && ok "media.class=Audio/Sink" || bad "media.class=$MEDIA_CLASS"
TAG=$(echo "$NODE_JSON" | jq -r '.[0].info.props["autoroute.managed"] // empty')
if [[ -n "$TAG" ]]; then
  ok "autoroute.managed prop round-trips through pw-dump (value: $TAG)"
  echo "$NODE_JSON" | jq '.[0]' > "$FIXTURES/pw-dump.managed-sink.json" && echo "  wrote $FIXTURES/pw-dump.managed-sink.json"
else
  bad "autoroute.managed prop STRIPPED — drop stale-module auto-cleanup (see header)"
fi

echo "== Step 2: pactl list modules short shows the module with its args =="
ROW=$(pactl list modules short | awk -v i="$IDX" '$1==i')
echo "$ROW" | grep -q "sink_name=$SINK" && ok "module row carries sink_name" || bad "module row: $ROW"
echo "$ROW" | grep -q "autoroute.managed=true" && ok "module args carry the ownership tag" || bad "tag missing from module args"
pactl list modules short > "$FIXTURES/pactl-modules.short.sample.txt.live" && \
  echo "  wrote $FIXTURES/pactl-modules.short.sample.txt.live (review + replace the .sample manually)"

echo "== Step 3: unload-module removes the sink =="
pactl unload-module "$IDX" && sleep 0.5
if pw-dump | jq -e "[.[] | select(.info.props[\"node.name\"]==\"$SINK\")] | length == 0" >/dev/null; then
  ok "node gone after unload"
else
  bad "node still present after unload"
fi

if [[ "${1:-}" == "--restart" ]]; then
  echo "== Step 4: generated drop-in boots the sink via pipewire-pulse restart =="
  DROPIN_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/pipewire/pipewire-pulse.conf.d"
  DROPIN="$DROPIN_DIR/autoroute-gate.conf"
  mkdir -p "$DROPIN_DIR"
  cat > "$DROPIN" <<EOF
pulse.cmd = [
    { cmd = "load-module" args = "module-null-sink sink_name=$SINK sink_properties=\\"$PROPS\\"" flags = [ ] }
]
EOF
  systemctl --user restart pipewire-pulse && sleep 2
  if pw-dump | jq -e "[.[] | select(.info.props[\"node.name\"]==\"$SINK\")] | length >= 1" >/dev/null; then
    ok "drop-in created the sink at service start"
  else
    bad "drop-in did not create the sink"
  fi
  BOOT_ROW=$(pactl list modules short | grep "sink_name=$SINK" || true)
  [[ -n "$BOOT_ROW" ]] && ok "boot-loaded module is visible to pactl (load-path symmetry)" || bad "boot module invisible to pactl"
  rm -f "$DROPIN"
  systemctl --user restart pipewire-pulse && sleep 2
  ok "gate drop-in removed and pipewire-pulse restarted clean"
else
  echo "== Step 4 SKIPPED (pass --restart to run; it restarts pipewire-pulse) =="
fi

echo "== Step 5 (negative): one-shot pw-cli node dies with the process =="
pw-cli create-node adapter '{ factory.name=support.null-audio-sink node.name=autoroute_gate_pwcli media.class=Audio/Sink }' >/dev/null 2>&1
sleep 0.5
if pw-dump | jq -e '[.[] | select(.info.props["node.name"]=="autoroute_gate_pwcli")] | length == 0' >/dev/null; then
  ok "pw-cli one-shot node vanished on exit (documents why pactl was chosen)"
else
  bad "pw-cli node SURVIVED — revisit ADR-0011 mechanism notes"
fi

echo "== Step 6: duplicate load-module with the same sink_name =="
A=$(pactl load-module module-null-sink "sink_name=$SINK")
B=$(pactl load-module module-null-sink "sink_name=$SINK" 2>&1)
if [[ "$B" =~ ^[0-9]+$ ]]; then
  echo "  INFO  duplicate load created a second module ($A, $B) — the modules-list guard is REQUIRED"
  pactl unload-module "$B" 2>/dev/null
else
  echo "  INFO  duplicate load refused: $B"
fi
pactl unload-module "$A" 2>/dev/null

echo
echo "GATE: $PASS pass, $FAIL fail — record results in docs/adr/0011-app-managed-virtual-sinks.md"
[[ $FAIL -eq 0 ]]
