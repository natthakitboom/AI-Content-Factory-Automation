#!/usr/bin/env bash
# Scans the files git tracks (run it from a repository) for credentials and for files that must
# never be committed. It prints where a finding is and which rule fired, never the value.
set -uo pipefail
cd "${1:-.}"
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || { echo "Not a git repository; nothing to scan." >&2; exit 2; }

findings=0
report() { printf '%s: %s\n' "$1" "$2"; findings=$((findings + 1)); }

# Files that must not be tracked at all.
while IFS= read -r -d '' file; do
  case "$file" in
    .env.example|.env.test.example) ;;
    .env|.env.*|*/.env|*/.env.*|*.pem|*.p12|*.pfx|*id_rsa*|*id_ed25519*) report "$file" "tracked file that should be ignored" ;;
  esac
done < <(git ls-files -z)

# tests/ContentFactory.UnitTests/ReleaseReadinessTests.cs is excluded on purpose: it plants fake secrets
# to prove this scanner detects them.
scan() { # rule-name regex
  local name="$1" regex="$2"
  while IFS=: read -r file line _; do
    [ -n "$file" ] && report "$file:$line" "$name"
  done < <(git ls-files -z | xargs -0 grep -I -n -E -e "$regex" 2>/dev/null \
    | grep -v -E '^(\.env\.example|\.env\.test\.example|apps/dashboard/package-lock\.json|tests/ContentFactory\.UnitTests/ReleaseReadinessTests\.cs):' || true)
}

scan "private key block" '-----BEGIN [A-Z ]*PRIVATE KEY-----'
scan "named secret with a value" '(FAL_KEY|LM_STUDIO_API_TOKEN|DATABASE_PASSWORD)[[:space:]]*[=:][[:space:]]*["'"'"']?[A-Za-z0-9_./+-]{12,}([^=A-Za-z0-9_./+-]|$)'
scan "API key shape (sk-)" 'sk-[A-Za-z0-9]{20,}'
scan "AWS access key shape" 'AKIA[0-9A-Z]{16}'
scan "GitHub token shape" 'gh[pousr]_[A-Za-z0-9]{30,}'
scan "Slack token shape" 'xox[baprs]-[A-Za-z0-9-]{10,}'
scan "password inside a connection string" 'Password=[A-Za-z0-9_!@#%^&*+/-]{10,}[;"'"'"' ]'

echo "Secret scan: $findings findings."
[ "$findings" -eq 0 ]
