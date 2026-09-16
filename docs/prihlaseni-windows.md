# Přihlášení účtem Windows (NTLM / Kerberos)

Uživatel na doménovém PC se do ACS přihlásí bez zadávání jména a hesla — prohlížeč
se prokáže účtem, kterým je přihlášen k Windows. Technicky jde o HTTP autentizaci
**Negotiate** (SPNEGO): prohlížeč pošle Kerberos ticket, případně NTLM token, aplikace
ho ověří proti doméně a vydá běžnou přihlašovací cookie ACS. Zbytek aplikace (role,
audit, schvalování) se nemění; jen v menu uživatele je u jména „· Windows“.

## Jak to funguje

```
prohlížeč ──GET /Account/WindowsLogin──▶ ACS  401 WWW-Authenticate: Negotiate
prohlížeč ──Authorization: Negotiate <ticket>──▶ ACS  ověří (GSSAPI/Kerberos, keytab)
                                              ACS  identita FNMH\novak → uživatel „novak“
                                              ACS  servisním účtem načte z AD jméno, e-mail, skupiny → role
                                              ACS  Set-Cookie: acs-auth … → přesměruje na returnUrl
```

- **Formulář zůstává.** Lokální `admin` a záloha při výpadku Kerberosu:
  `https://acs.fnmh.hospital/Account/Login?manual=1`. Po odhlášení se formulář ukáže
  vždy (jinak by automatické přihlášení uživatele hned přihlásilo znovu).
- **Selhání nikdy neskončí chybou 500** — neplatný token, chybějící keytab, NTLM bez
  podpory → přesměrování na formulář s vysvětlením (`?sso=failed`).
- **Mapování na uživatele ACS** je stejné jako u přihlášení heslem: první přihlášení
  založí doménového uživatele s rolí Zaměstnanec, spáruje ho se zaměstnancem podle
  AD účtu, a je-li nastavené *Mapování AD skupin na role*, role se přepočítají při
  každém přihlášení. Skupiny se čtou **servisním účtem** (sekce Active Directory —
  bind účet + heslo); bez něj se role nemění a zůstávají tak, jak jsou v ACS.
- Doménový účet se jménem shodným s lokálním účtem (`admin`) se přes Windows nepřihlásí.
- Volitelně lze omezit **povolené domény** účtu (např. `FNMH`), aby se nepřihlásili
  uživatelé z důvěryhodných cizích domén.

## Stav (16. 9. 2026)

Na nodech **keytab zatím není** (`/etc/acs/acs.keytab` neexistuje, `KRB5_KTNAME` v `acs.env`
chybí) a nody jsou klienty jiného Kerberos realmu (IPA `RHEL.NNH.NETWORK`, `/etc/krb5.conf`),
takže Kerberos ticket pro ACS nemá čím ověřit — v logu je `Přihlášení Windows (Negotiate)
selhalo: UnknownCredentials`. Aplikace to od v1.12.40 pozná sama: bez čitelného keytabu z
`KRB5_KTNAME` se tlačítko ani automatická výzva Negotiate **nenabízí** a uživatelé dostanou
rovnou formulář (Nastavení to hlásí červeně). Jakmile keytab nahrajete a nastavíte
`KRB5_KTNAME` (kroky 1–2 níže) a restartujete `acs-web`, přihlášení Windows začne fungovat
bez dalšího zásahu. Výchozí `/etc/krb5.keytab` se úmyslně nepoužívá — patří hostiteli
v realmu IPA a SPN aplikace neobsahuje.

## Co je potřeba

ACS běží na **RHEL** (Kestrel za HAProxy). Kestrel na Linuxu ověřuje Negotiate přes
GSSAPI — to znamená **Kerberos s keytabem**. Čisté NTLM na straně serveru Linux/Kestrel
podle Microsoftu nepodporuje (NTLM ověření proti doméně umí jen hostování na Windows /
IIS). V praxi to nevadí: doménové PC s dostupným řadičem posílá Kerberos; NTLM by
prohlížeč použil jen při přístupu přes IP adresu nebo bez ticketu — a v tom případě
uživatel skončí na formuláři.

### 1. Servisní účet a SPN (na doménovém řadiči, jako Domain Admin)

Kerberos ticket pro `https://acs.fnmh.hospital` musí být vydaný na SPN
`HTTP/acs.fnmh.hospital`, zaregistrované na účtu, jehož klíč mají oba nody.

```powershell
# Účet služby (může být stávající svc-acs; heslo bez expirace)
New-ADUser svc-acs-web -Enabled $true -AccountPassword (Read-Host -AsSecureString) -PasswordNeverExpires $true
# SPN podle DNS jména, na které se uživatelé připojují (přes HAProxy)
setspn -S HTTP/acs.fnmh.hospital NNH\svc-acs-web
# Kontrola: setspn -L NNH\svc-acs-web
# Doporučeno: povolit AES na účtu (záložka Account → "This account supports Kerberos AES 256")
```

Keytab (soubor s klíčem účtu) — **pozor, `ktpass` nastaví účtu nové heslo**:

```powershell
ktpass -princ HTTP/acs.fnmh.hospital@NNH.LOCAL -mapuser NNH\svc-acs-web `
       -pass * -pType KRB5_NT_PRINCIPAL -crypto AES256-SHA1 -out C:\temp\acs.keytab
```

Realm (`@NNH.LOCAL`) je DNS jméno domény velkými písmeny.

### 2. Nody (RHEL)

```bash
sudo dnf install -y krb5-libs krb5-workstation          # install.sh to dělá
sudo cp acs.keytab /etc/acs/acs.keytab
sudo chown acs:acs /etc/acs/acs.keytab && sudo chmod 600 /etc/acs/acs.keytab
sudo -u acs klist -kte /etc/acs/acs.keytab               # musí vypsat HTTP/acs.fnmh.hospital@NNH.LOCAL (aes256)
echo 'KRB5_KTNAME=/etc/acs/acs.keytab' | sudo tee -a /etc/acs/acs.env   # pokud řádek chybí
sudo systemctl restart acs-web
```

`/etc/krb5.conf` na nodech **neměňte** — spravuje ho IPA klient (realm `RHEL.NNH.NETWORK`).
Pro ověřování ticketů (acceptor) ho GSSAPI nepotřebuje: klíč bere z keytabu, který nese
principal `HTTP/acs.fnmh.hospital@NNH.LOCAL` včetně realmu. Stačí, aby `dns_lookup_kdc = true`
zůstalo (je) a čas nodu seděl s řadiči (chrony).

`/etc/acs/acs.env` — cesta ke keytabu (viz `deploy/acs.env.example`):

```
KRB5_KTNAME=/etc/acs/acs.keytab
```

Restart: `sudo systemctl restart acs-web`. Při instalaci přes `deploy/install.sh` stačí
uložit keytab jako `deploy/acs.keytab` (je v `.gitignore`) — skript ho nahraje a nastaví
práva sám.

### 3. Zapnutí v ACS

**Nastavení → Přihlášení Windows (NTLM / Kerberos)**:

- *Povolit přihlášení účtem Windows* — na přihlašovací stránce přibude tlačítko,
  endpoint `/Account/WindowsLogin` začne vyzývat prohlížeč.
- *Přihlašovat automaticky* — přihlašovací stránka rovnou přesměruje na přihlášení
  Windows (uživatel formulář vůbec nevidí). Zapínejte až po úspěšném testu.
- *Povolené domény účtu* — např. `FNMH`.
- Tabulka *Stav na tomto nodu* ukazuje, zda je nastavený `KRB5_KTNAME` a soubor existuje,
  a jaké SPN se očekává podle adresy aplikace.
- Tlačítko *Otestovat přihlášení Windows* ověří identitu (bez změny přihlášení správce)
  a vypíše `OK — identita „FNMH\novak“, protokol Kerberos`.

Sekce Active Directory: vyplněný **servisní účet pro bind** (jinak se nenačtou role ze
skupin) a *Mapování AD skupin na role*.

### 4. Prohlížeče na PC

Prohlížeč pošle ticket jen webu, kterému věří:

- **Edge / Chrome / IE zóny** (GPO *Site to Zone Assignment List*):
  `https://acs.fnmh.hospital` → zóna 1 (Místní intranet). Případně politika
  `AuthServerAllowlist = acs.fnmh.hospital` (Edge, Chrome).
- **Firefox**: `network.negotiate-auth.trusted-uris = https://acs.fnmh.hospital`
  (GPO / policies.json `Authentication.SPNEGO`).
- Adresu používejte **jménem**, ne IP adresou — pro IP prohlížeč Kerberos nepoužije.

## Ověření a řešení potíží

| Příznak | Příčina / řešení |
|---|---|
| Po kliknutí na tlačítko se hned ukáže formulář s hláškou „neposlal platný token“ | Server token neověřil. `journalctl -u acs-web` — typicky chybí keytab (`KRB5_KTNAME`), klíč v keytabu je jiný než v AD (po `ktpass` se změnilo heslo → vygenerovat znovu a nahrát na **oba** nody) nebo prohlížeč poslal NTLM (viz níže). |
| Prohlížeč zobrazí dialog na jméno a heslo | Web není v zóně Intranet / AuthServerAllowlist, nebo přístup přes IP. Zrušením dialogu se ukáže stránka s odkazem na formulář. |
| V logu `NTLM` / `No credentials were supplied` | Klient nemá Kerberos ticket pro SPN — zkontrolujte `setspn -L`, DNS jméno a čas (Kerberos toleruje odchylku 5 min). Na klientovi `klist` po otevření stránky musí ukázat ticket `HTTP/acs.fnmh.hospital`. |
| Funguje na jednom nodu, na druhém ne | Keytab chybí / má jiná práva na druhém nodu (`sudo -u acs klist -k /etc/acs/acs.keytab`). |
| Uživatel se přihlásí, ale nemá role | Sekce Active Directory: servisní účet pro bind + mapování skupin. Bez servisního účtu se skupiny nenačtou. |
| „Účet Windows byl ověřen, ale v ACS ho nelze použít“ | Uživatel je v ACS neaktivní, účet je z nepovolené domény, nebo jméno patří lokálnímu účtu. |
| Přihlášení AD účtem jménem a heslem skončí **504** od proxy, lokální účet funguje | LDAP bind k řadiči se nedokončil: řadič TCP spojení (636/389) přijme, ale TLS handshake nebo bind visí (přetížený DC, firewall propouštějící jen SYN, přesměrování portu). Knihovna na Linuxu na bind žádný timeout nemá, takže požadavek čekal do vypršení TCP. Od v1.12.37 má bind limit 10 s, zkusí se náhradní řadič a stránka vypíše „řadič Active Directory neodpovídá“ místo 504; v logu `journalctl -u acs-web` je `LDAP: řadič <host>:<port> po <ms> ms — …`. Ověření z nodu: `openssl s_client -connect <dc>:636 -brief` musí do pár sekund vypsat certifikát. |

Audit: přihlášení přes Windows se zapisuje jako `login` s detailem `windows: FNMH\novak`,
neúspěch jako `login-failed`.

## Poznámky k HAProxy

Kerberos je bezestavový (ticket v každém požadavku) — funguje s `balance roundrobin`
bez sticky session. NTLM je vázaný na TCP spojení; HAProxy s výchozím `http-reuse safe`
takové spojení označí za privátní a drží ho k jednomu serveru, takže i případný NTLM
handshake projde. Nepoužívejte `http-reuse aggressive/always`. Viz
`deploy/haproxy.cfg.example`.

## Bezpečnost

- Keytab je ekvivalent hesla servisního účtu — jen `/etc/acs/acs.keytab`, `600`, vlastník
  `acs`, nikdy do Gitu (`*.keytab` je v `.gitignore`).
- Servisní účet nepotřebuje žádná práva v doméně (stačí běžný uživatel se SPN).
- Aplikace ověřuje jen identitu z Negotiate; neukládá žádné heslo ani ticket.
  Přihlášení je stejně jako u formuláře cookie s 10 h platností.
- Endpoint `/Account/WindowsLogin` funguje jen při zapnutém nastavení; jinde v aplikaci
  se hlavička `Authorization: Negotiate` ignoruje (požadavek pokračuje jako nepřihlášený).
