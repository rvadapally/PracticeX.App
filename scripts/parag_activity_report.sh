#!/bin/bash
# Post-demo report: pull all UI events from the last <since> window,
# grouped by email so we can see what each viewer explored. Run after
# Parag's session.
#
# Usage:
#   ./parag_activity_report.sh                 # last 24h
#   ./parag_activity_report.sh 2026-05-05      # since this date
#   ./parag_activity_report.sh 2026-05-05 parag@example.com  # filter to email

SINCE="${1:-$(date -u -d '24 hours ago' '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u '+%Y-%m-%dT%H:%M:%SZ')}"
EMAIL="${2:-}"

URL="https://localhost:7100/api/analytics/events?since=${SINCE}"
if [[ -n "$EMAIL" ]]; then
  URL="${URL}&email=${EMAIL}"
fi

echo "=== UI activity report ==="
echo "Since: $SINCE"
[[ -n "$EMAIL" ]] && echo "Email filter: $EMAIL"
echo

curl -s -k "$URL" | python -c "
import json, sys
from collections import Counter
d = json.load(sys.stdin)
print(f'Total events: {d[\"totalEvents\"]}')
print()
print('=== Per-email summary ===')
for e in d['byEmail']:
    span = ''
    print(f'{e[\"email\"]}')
    print(f'  events: {e[\"eventCount\"]}  distinct paths: {e[\"distinctPaths\"]}')
    print(f'  first:  {e[\"firstSeen\"]}')
    print(f'  last:   {e[\"lastSeen\"]}')
    print(f'  top paths:')
    for p in e['topPaths']:
        print(f'    {p[\"hits\"]:>3}  {p[\"path\"] or \"(none)\"}')
    print()

print('=== Event-type histogram (across all viewers) ===')
ctr = Counter(ev['eventType'] for ev in d['events'])
for evt, n in ctr.most_common():
    print(f'  {n:>4}  {evt}')
"
