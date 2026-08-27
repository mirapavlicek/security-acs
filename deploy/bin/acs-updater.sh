#!/usr/bin/env bash
# =============================================================================
# ACS auto-updater: kontroluje Git main; při nové verzi sestaví a nasadí.
#
# Běží na obou HA nodech (systemd timer s náhodným rozptylem). Aktualizace je
# bezvýpadková z pohledu clusteru: HAProxy healthcheckem pozná restartující
# node a provoz drží na druhém.
#
# Rozvržení na disku:
#   /opt/acs/src       – klon repozitáře (main)
#   /opt/acs/releases/<sha> – publikované verze
#   /opt/acs/current   – symlink na aktivní verzi
#   /opt/acs/bin       – tento skript
# =============================================================================
set -euo pipefail

ACS_HOME="/opt/acs"
SRC_DIR="$ACS_HOME/src"
RELEASES_DIR="$ACS_HOME/releases"
CURRENT_LINK="$ACS_HOME/current"
BRANCH="${ACS_UPDATE_BRANCH:-main}"
# ACS_UPDATE_MODE=tag (výchozí, bezpečnější — nasazuje jen podepsané/označené
# release tagy vX.Y.Z) nebo branch (nasazuje každý commit ve větvi).
UPDATE_MODE="${ACS_UPDATE_MODE:-tag}"
KEEP_RELEASES=3

log() { echo "[acs-updater] $(date -Is) $*"; }

# --- 1. Zjištění nové verze --------------------------------------------------
cd "$SRC_DIR"
git fetch --quiet --tags --prune origin "$BRANCH"

if [[ "$UPDATE_MODE" == "tag" ]]; then
    # Nejnovější release tag vX.Y.Z (setříděno podle verze).
    TARGET_REF="$(git tag -l 'v*' --sort=-v:refname | head -n1)"
    if [[ -z "$TARGET_REF" ]]; then
        log "Režim 'tag': zatím neexistuje žádný release tag (v*). Nic k nasazení."
        exit 0
    fi
    REMOTE_SHA="$(git rev-list -n1 "$TARGET_REF")"
    log "Cílový release: $TARGET_REF (${REMOTE_SHA:0:12})."
else
    TARGET_REF="origin/$BRANCH"
    REMOTE_SHA="$(git rev-parse "$TARGET_REF")"
fi

CURRENT_SHA="$(cat "$CURRENT_LINK/.git-sha" 2>/dev/null || echo "none")"

if [[ "$REMOTE_SHA" == "$CURRENT_SHA" ]]; then
    log "Žádná nová verze (aktuální: ${CURRENT_SHA:0:12})."
    exit 0
fi

log "Nová verze: ${CURRENT_SHA:0:12} -> ${REMOTE_SHA:0:12}. Aktualizuji…"
git checkout --quiet "$REMOTE_SHA"

# --- 2. Build ---------------------------------------------------------------
RELEASE_DIR="$RELEASES_DIR/$REMOTE_SHA"
rm -rf "$RELEASE_DIR"
dotnet publish src/Acs.Web -c Release -o "$RELEASE_DIR" --nologo
echo "$REMOTE_SHA" > "$RELEASE_DIR/.git-sha"

# --- 3. Testy (s očištěným prostředím — bez produkčních proměnných z acs.env) ---
if ! env -u ASPNETCORE_ENVIRONMENT -u ConnectionStrings__Default -u Database__Provider \
        -u ACS_BOOTSTRAP_ADMIN_PASSWORD -u Kestrel__Endpoints__Http__Url \
        dotnet test --nologo -c Release 2>&1 | tail -5; then
    log "CHYBA: testy nové verze selhaly — nasazení zrušeno."
    rm -rf "$RELEASE_DIR"
    exit 1
fi

# --- 4. Atomické přepnutí a restart -------------------------------------------
ln -sfn "$RELEASE_DIR" "$CURRENT_LINK"
sudo /usr/bin/systemctl restart acs-web

# --- 5. Ověření zdraví ---------------------------------------------------------
for i in $(seq 1 30); do
    if curl -fsS "http://127.0.0.1:52000/health" >/dev/null 2>&1; then
        log "Nová verze ${REMOTE_SHA:0:12} běží a je zdravá."
        break
    fi
    if [[ "$i" == "30" ]]; then
        log "CHYBA: aplikace po restartu neodpovídá na /health!"
        exit 1
    fi
    sleep 2
done

# --- 6. Úklid starých verzí (ponech KEEP_RELEASES nejnovějších) -----------------
cd "$RELEASES_DIR"
find . -maxdepth 1 -mindepth 1 -type d -printf '%T@ %p\n' \
    | sort -rn \
    | tail -n "+$((KEEP_RELEASES + 1))" \
    | cut -d' ' -f2- \
    | xargs -r rm -rf

log "Hotovo."
