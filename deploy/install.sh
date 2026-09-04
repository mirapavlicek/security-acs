#!/usr/bin/env bash
# =============================================================================
# Instalace ACS na RHEL nody přes SSH (spouští se z vývojového stroje,
# např. macOS-ai proxyhub; používá lokální SSH klíč).
#
# Použití:
#   ./deploy/install.sh <ssh-user> [node...]
#   ./deploy/install.sh root                     # oba výchozí nody
#   ./deploy/install.sh admin 10.84.7.146        # jen jeden node
#
# Před spuštěním:
#   1. zkopírujte deploy/acs.env.example, doplňte hodnoty (DB heslo…)
#      a uložte jako deploy/acs.env (soubor je v .gitignore),
#   2. ověřte, že máte SSH přístup: ssh <user>@10.84.7.146
# =============================================================================
set -euo pipefail

SSH_USER="${1:?Použití: $0 <ssh-user> [node...]}"
shift
if [[ $# -gt 0 ]]; then
    NODES=("$@")
else
    NODES=(10.84.7.146 10.84.7.147)
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Lze přepsat proměnnou REPO_URL; jinak se odvodí z gitu. Případný vložený
# token (https://x-access-token:...@github.com/...) se odstraní, aby se
# nepropašoval na cílové nody (repo je veřejné, klonuje se anonymně).
REPO_URL="${REPO_URL:-$(git -C "$SCRIPT_DIR/.." remote get-url origin)}"
REPO_URL="$(printf '%s' "$REPO_URL" | sed -E 's#https://[^@/]*@#https://#')"
ENV_FILE="$SCRIPT_DIR/acs.env"

if [[ ! -f "$ENV_FILE" ]]; then
    echo "CHYBA: $ENV_FILE neexistuje. Zkopírujte deploy/acs.env.example a doplňte hodnoty." >&2
    exit 1
fi

for NODE in "${NODES[@]}"; do
    echo "=== Instalace na $NODE ==="

    # 1. Soubory
    scp "$ENV_FILE" "$SSH_USER@$NODE:/tmp/acs.env"
    scp "$SCRIPT_DIR/systemd/acs-web.service" \
        "$SCRIPT_DIR/systemd/acs-updater.service" \
        "$SCRIPT_DIR/systemd/acs-updater.timer" \
        "$SSH_USER@$NODE:/tmp/"
    scp "$SCRIPT_DIR/bin/acs-updater.sh" "$SSH_USER@$NODE:/tmp/acs-updater.sh"

    # 2. Instalace na nodu
    ssh "$SSH_USER@$NODE" REPO_URL="$REPO_URL" 'bash -s' <<'REMOTE'
set -euo pipefail
SUDO=""; [[ $EUID -ne 0 ]] && SUDO="sudo"

# .NET 10 SDK (build v updateru) — kontrolujeme SDK, ne jen runtime.
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    $SUDO dnf install -y dotnet-sdk-10.0 \
      || (curl -sSL https://dot.net/v1/dotnet-install.sh | $SUDO bash -s -- --channel 10.0 --install-dir /usr/lib/dotnet \
          && $SUDO ln -sf /usr/lib/dotnet/dotnet /usr/bin/dotnet)
fi
$SUDO dnf install -y git curl policycoreutils-python-utils || true
# Písmo s českou diakritikou pro generování PDF (kartičky parkovacích povolení, reporty).
$SUDO dnf install -y dejavu-sans-fonts || true

# Servisní účet a adresáře
id acs >/dev/null 2>&1 || $SUDO useradd --system --home /opt/acs --shell /sbin/nologin acs
$SUDO mkdir -p /opt/acs/{releases,bin} /etc/acs
$SUDO mv /tmp/acs.env /etc/acs/acs.env
$SUDO chmod 600 /etc/acs/acs.env && $SUDO chown acs:acs /etc/acs/acs.env
$SUDO mv /tmp/acs-updater.sh /opt/acs/bin/acs-updater.sh
$SUDO chmod +x /opt/acs/bin/acs-updater.sh

# Klon repozitáře (updater z něj staví)
if [[ ! -d /opt/acs/src/.git ]]; then
    $SUDO git clone "$REPO_URL" /opt/acs/src
fi
$SUDO chown -R acs:acs /opt/acs

# Sudo pravidlo: updater (acs) smí restartovat službu
echo 'acs ALL=(root) NOPASSWD: /usr/bin/systemctl restart acs-web' | $SUDO tee /etc/sudoers.d/acs-updater >/dev/null
$SUDO chmod 440 /etc/sudoers.d/acs-updater

# systemd + firewall + SELinux
$SUDO mv /tmp/acs-web.service /tmp/acs-updater.service /tmp/acs-updater.timer /etc/systemd/system/
$SUDO systemctl daemon-reload
$SUDO firewall-cmd --permanent --add-port=52000/tcp && $SUDO firewall-cmd --reload || true
$SUDO semanage port -a -t http_port_t -p tcp 52000 2>/dev/null || true

# Rezervace portu 52000: leží v efemérním rozsahu (ip_local_port_range), takže
# na vytížených serverech by ho jiná služba mohla obsadit jako zdrojový port
# odchozího spojení a zablokovat bind aplikace. Rezervace tomu zabrání.
echo "net.ipv4.ip_local_reserved_ports = 52000" | $SUDO tee /etc/sysctl.d/99-acs-reserved-port.conf >/dev/null
$SUDO sysctl -p /etc/sysctl.d/99-acs-reserved-port.conf >/dev/null 2>&1 || true
# Pokud port právě drží cizí odchozí spojení, uvolni jen ten jeden soket.
if command -v ss >/dev/null && ss -ltnH 2>/dev/null | grep -qw 52000; then :; else
    $SUDO ss -K sport = :52000 2>/dev/null || true
fi

# První build a nasazení pod účtem acs (sestaví verzi a vytvoří /opt/acs/current).
# runuser funguje i když skript běží přímo jako root (kdy je $SUDO prázdné).
$SUDO runuser -u acs -- /opt/acs/bin/acs-updater.sh || true
$SUDO systemctl enable --now acs-updater.timer
$SUDO systemctl enable --now acs-web || true
echo "--- Stav: ---"
systemctl is-active acs-web || true
curl -fsS http://127.0.0.1:52000/health || echo "(health zatím neodpovídá — zkontrolujte journalctl -u acs-web)"
REMOTE

    echo "=== $NODE hotovo ==="
done

echo "Instalace dokončena. Aplikace poslouchá na portu 52000; nasměrujte HAProxy (viz deploy/haproxy.cfg.example)."
