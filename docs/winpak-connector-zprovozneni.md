# Zprovoznění WinPak Connectoru (mezičlánek k WIN-PAK API)

Postup pro obsluhu: jak dostat konektor od nuly do funkčního stavu a napojit
na něj ACS. Referenční popis konektoru je v
[`src/Acs.WinPakConnector/README.md`](../src/Acs.WinPakConnector/README.md),
rozbor samotného WIN-PAK API v
[`docs/winpak-api/README.md`](winpak-api/README.md).

## K čemu konektor je

WIN-PAK nemá REST API — jeho rozhraní jsou COM objekty přes COM+/DCOM, tedy
Windows-only. ACS běží na RHEL a COM volat nemůže. Konektor je proto malá
Windows služba, která běží na WIN-PAK serveru, mluví s ním nativně přes COM
a pro ACS z toho dělá normální REST:

```
ACS (RHEL, 10.84.7.146/147)  ──HTTP + API klíč──▶  WinPak Connector (Windows, port 52001)  ──COM+──▶  WIN-PAK
```

Bez konektoru ACS neumí načítat čtečky ani předávat přístupy — správce karet je
musí zadávat ve WIN-PAKu ručně.

## Než začnete

| Předpoklad | Jak ověřit |
| --- | --- |
| WIN-PAK edice **SE nebo PE** s licencí **`SRVWPPAPI`** | v XE API vůbec není; licenci potvrdí dodavatel WIN-PAKu |
| WIN-PAK nainstalovaný **s volbou Web** | tím se nasadí `DatabaseAPIServer`, bez kterého API nefunguje |
| Existují COM+ aplikace **WIN-PAK CS DBServer Helper** a **WIN-PAK CS ComServer Helper** | krok 1 níže |
| **Účet operátora WIN-PAK** pro konektor (jméno, heslo, případně doména) | krok 2 níže |
| Znáte **název účtu (account)** a případně podúčtu, ve kterém jsou karty a držitelé | ve WIN-PAK klientovi; ověří se v kroku 7 |
| Na WIN-PAK serveru je volný **TCP port 52001** | `netstat -ano \| findstr 52001` |
| Firewall pustí na 52001 **jen** aplikační servery ACS | krok 4 |

.NET runtime instalovat netřeba — konektor se publikuje jako self-contained.

Doporučené místo pro konektor je **přímo WIN-PAK server**. Odpadne tím DCOM přes
síť a s ním firewallové i autentizační potíže. Pokud to nejde, viz poznámku
na konci kroku 1.

## Krok 1 — ověřit COM+ komponenty

Na WIN-PAK serveru:

1. Spusťte `dcomcnfg` (nebo Ovládací panely → Nástroje pro správu → Component Services).
2. Rozbalte **Component Services → Computers → My Computer → COM+ Applications**.
3. Musí tam být:
   - **WIN-PAK CS DBServer Helper** — databázová část API (`NCIHelper.dll`),
   - **WIN-PAK CS ComServer Helper** — komunikační server (`ACCW.dll`).

Když tam nejsou, WIN-PAK není nainstalovaný s volbou Web nebo chybí licence API —
dál nemá smysl pokračovat, řešte to s dodavatelem WIN-PAKu.

> **Konektor na jiném stroji než WIN-PAK:** u obou COM+ aplikací dejte
> pravým tlačítkem **Export → Application proxy**, vzniklý instalátor přeneste
> na stroj s konektorem a nainstalujte. Bez proxy tam COM objekty nejsou
> zaregistrované a konektor to ohlásí hláškou
> „COM objekt '…' není na tomto stroji zaregistrován“.

## Krok 2 — účet operátora WIN-PAK

Konektor se k WIN-PAKu přihlašuje jako operátor. Ve WIN-PAK klientovi založte
(nebo si vyžádejte) účet, který má práva na to, co má ACS dělat:

- čtení čteček, přístupových úrovní, držitelů a karet,
- zápis karet a jejich přístupových úrovní (to je vlastní přidělení přístupu),
- volitelně ovládání dveří, pokud ho chcete používat ze sekce Funkce.

Nedávejte účtu víc, než potřebuje. Pokud WIN-PAK používá **doménové přihlášení**
(„Logon using Domain Credentials“), poznamenejte si i doménu — konektor pak
použije `InitServer2`.

Účet, pod kterým poběží **služba konektoru**, musí mít právo volat COM+ aplikace
z kroku 1. Nejjednodušší je nechat službu běžet pod `LocalSystem` na WIN-PAK
serveru; při vzdáleném nasazení použijte doménový účet s právy na DCOM.

## Krok 3 — získat balík a zkopírovat

**Nejsnazší cesta — stáhnout z GitHubu.** Workflow **WinPak Connector** sestaví
hotový balík pro Windows při každém pushnutí do `main` a u vydání ho přiloží
k releasu:

- u vydání: *Releases → `vX.Y.Z` → `AcsWinPakConnector-<verze>-win-x64.zip`*,
- jinak: *Actions → WinPak Connector → poslední běh → Artifacts*,
- nebo si běh spusťte ručně (*Run workflow*) a zadejte verzi.

U balíku je i soubor `.sha256`; na serveru si kopii ověřte:

```powershell
Get-FileHash AcsWinPakConnector-1.8.0-win-x64.zip -Algorithm SHA256
```

**Sestavení lokálně** (když nechcete GitHub):

```bash
dotnet publish src/Acs.WinPakConnector -c Release -r win-x64 --self-contained \
  -p:Version=1.8.0 -o publish/winpak-connector
```

Balík rozbalte (nebo obsah `publish/winpak-connector` zkopírujte) na WIN-PAK
server, doporučeně do `C:\Program Files\AcsWinPakConnector`. Verzi si pak
ověříte v administraci na Přehledu.

## Krok 4 — první konfigurace, služba a firewall

1. V `appsettings.json` nastavte jen dvě věci:
   - `Security:ApiKey` — silný náhodný klíč (`openssl rand -hex 32`, nebo si ho
     později vygenerujte v GUI). Bez klíče konektor odmítá všechny požadavky
     a nepustí vás ani do administrace.
   - `Kestrel:Endpoints:Http:Url` — ponechte `http://0.0.0.0:52001`, nebo omezte
     na konkrétní interní IP.

   Zbytek (režim, přihlášení k WIN-PAKu, účet, ProgID) se pohodlněji nastaví
   v GUI v kroku 6.

2. Zaregistrujte službu (PowerShell jako administrátor):

   ```powershell
   New-Service -Name "AcsWinPakConnector" `
     -BinaryPathName '"C:\Program Files\AcsWinPakConnector\Acs.WinPakConnector.exe"' `
     -DisplayName "ACS WinPak Connector" -StartupType Automatic
   Start-Service AcsWinPakConnector
   ```

3. Otevřete port jen aplikačním serverům ACS (a případně stanicím správců):

   ```powershell
   New-NetFirewallRule -DisplayName "ACS WinPak Connector" -Direction Inbound `
     -Protocol TCP -LocalPort 52001 -RemoteAddress 10.84.7.146,10.84.7.147 -Action Allow
   ```

4. Ověřte, že služba žije:

   ```powershell
   Invoke-RestMethod http://localhost:52001/health   # → status = ok
   ```

## Krok 5 — přihlásit se do administrace

Otevřete `http://<winpak-server>:52001/ui`. Přihlašuje se **API klíčem**
z kroku 4, protože samostatné heslo administrace zatím není nastavené.

V **Nastavení → Zabezpečení** si hned nastavte **Heslo administrace**, ať se
API klíč nemusí zadávat do prohlížeče. Po uložení se přihlašuje tímto heslem;
API klíč zůstává jen pro komunikaci s ACS.

## Krok 6 — přepnout na režim Com

V **Nastavení**:

1. **Režim** změňte na `Com`. Zobrazí se sekce „WIN-PAK přes COM“.
2. **Přihlášení operátora** — uživatel, heslo a doména z kroku 2. Doménu nechte
   prázdnou u lokálních účtů WIN-PAK.
3. **Účet** — název účtu WIN-PAK, ve kterém jsou karty a držitelé, případně
   i podúčet. Čtečky, karty i držitelé jsou po účtech oddělení, dotaz za jiný
   účet vrátí prázdno. Prázdné pole znamená „použij jediný účet WIN-PAKu“;
   má-li WIN-PAK účtů víc, musí se jeden vybrat. Diagnostika u kontroly „Účty“
   vypíše, se kterým účtem konektor pracuje a jestli si ho doplnil sám.
4. **Komunikační server** zapněte jen tehdy, když chcete ovládat dveře nebo
   odebírat události z panelů. Databázová část funguje i bez něj.
5. **ProgID** neměňte, pokud instalace neregistruje objekty jinak než výchozí
   `NCIHelper.*` a `ACCW.MTSCBServer`.
6. **Uložit nastavení.** Restart služby není potřeba — konektor se přestaví sám.

Nastavení se ukládá do `appsettings.Local.json` vedle programu. Ten soubor
obsahuje hesla, takže mu omezte přístup (ACL složky) na účet služby a
administrátory. Instalační `appsettings.json` zůstává nedotčený, takže se dá
kdykoli vrátit do výchozího stavu jeho smazáním.

## Krok 7 — ověřit, že konektor doopravdy čte z WIN-PAKu

Otevřete **Diagnostiku**. Všechny řádky mají mít zelené „ok“:

| Kontrola | Co znamená |
| --- | --- |
| Stav spojení | přihlášení a připojení k databázovému serveru prošlo |
| Účty | vidíte název svého účtu a jeho podúčty — podle toho zkontrolujte krok 6 |
| Čtečky | počet odpovídá tomu, co je ve WIN-PAKu |
| Přístupové úrovně | počet odpovídá WIN-PAKu |
| Držitelé karet | počet odpovídá WIN-PAKu |
| Systémové údaje | zdroj dat, časová zóna serveru, operátor, max. délka čísla karty |
| Časové zóny, Panely | číselníky a hardware se čtou |

Prázdné číselníky při zeleném spojení obvykle znamenají špatně zadaný účet —
kontrola „Účty“ ukazuje, se kterým účtem se pracuje. Když něco svítí červeně,
hláška začíná názvem volání, které WIN-PAK odmítl (`WIN-PAK IsConnected: …`),
a u chyb COM nese i HRESULT; hledejte ji v tabulce na konci tohoto dokumentu.
„Systémové údaje“ vrátí, co jde, a odmítnutá volání vypíše jmenovitě — instalace
se liší verzí i licencí a ne každé volání příručky je všude k dispozici.

Na **Přehledu** zkontrolujte, že „Zápis do WIN-PAK“ je **ano** — bez toho ACS
přístupy nepředá.

## Krok 8 — napojit ACS

V ACS jako administrátor otevřete **Nastavení → WIN-PAK konektor**:

1. **Adresa konektoru** — `http://<winpak-server>:52001`.
2. **API klíč** — týž klíč jako v konektoru.
3. Klikněte **Otestovat spojení**. Očekávaná odpověď: `OK — režim Com, verze …,
   zápis: ano`.
4. Zapněte **Automatická synchronizace čteček** (interval např. 60 minut).
5. Zapněte **Zpětná synchronizace stavu přístupů** (interval např. 15 minut),
   ať se změny provedené přímo ve WIN-PAKu propíší do ACS.
6. Uložte.

Pod nastavením je odkaz do administrace konektoru, ať se k ní správce dostane
z jednoho místa.

## Krok 9 — ověřit celou cestu

1. **Čtečky** → *Synchronizovat z WIN-PAK*. Musí se naimportovat čtečky
   a u nich zůstat vyplněný panel a popis.
2. U aspoň jedné čtečky doplňte **WIN-PAK access level** (Čtečky → Upravit →
   „WIN-PAK access level“), jinak ji nelze předat do WIN-PAKu.
3. Zkuste projít celý tok: podat žádost → schválit → ve **Frontě karet**
   *Předat do systému*. Ve WIN-PAKu se přístup musí objevit na kartě držitele.
4. V ACS otevřete **Automatizace** — self-diagnostika tam nesmí hlásit problém
   s konektorem ani se servery WIN-PAKu.

## Kontrolní seznam po zprovoznění

- [ ] `GET /health` na konektoru vrací `ok`
- [ ] Diagnostika konektoru je celá zelená
- [ ] Přehled hlásí „Zápis do WIN-PAK: ano“
- [ ] V administraci je nastavené **heslo administrace** (ne jen API klíč)
- [ ] Port 52001 je firewallem omezený na aplikační servery
- [ ] `appsettings.Local.json` má omezené ACL
- [ ] Test spojení v ACS vrací `OK — režim Com`
- [ ] Synchronizace čteček i zpětná synchronizace jsou zapnuté
- [ ] Čtečky mají vyplněný WIN-PAK access level
- [ ] Zkušební žádost prošla celým tokem až do WIN-PAKu

## Když to nejde

| Hláška / projev | Příčina | Co udělat |
| --- | --- | --- |
| `COM objekt '…' není na tomto stroji zaregistrován` | konektor neběží na WIN-PAK serveru, nebo chybí COM+ proxy | nasaďte konektor na WIN-PAK server, nebo nainstalujte application proxy (krok 1) |
| `Režim Com vyžaduje Windows` | konektor běží na Linuxu | režim Com jde jen na Windows; na Linuxu použijte Mock |
| `Přihlášení k WIN-PAK se nezdařilo — ověřte uživatele, heslo a doménu` | špatné údaje operátora, nebo se má použít doména | zkontrolujte krok 2 a 6; u doménového přihlášení vyplňte doménu |
| `Připojení k databázi WIN-PAK selhalo (status -2)` | databázový server WIN-PAK neodpovídá | zkontrolujte služby WIN-PAKu a MSSQL na serveru |
| `Registrace u komunikačního serveru WIN-PAK selhala (InitServer vrátil false)` | komunikační server neběží, nebo účet nemá práva | ověřte službu komunikačního serveru; nebo ho v nastavení vypněte, pokud dveře neovládáte |
| HTTP **401** z `/api/*` | ACS má jiný API klíč než konektor | srovnejte klíč v ACS a v konektoru |
| HTTP **503** z `/api/*` | konektor nemá nakonfigurovaný API klíč | doplňte `Security:ApiKey` a restartujte službu |
| HTTP **501** | operaci neumí zvolený režim (typicky Mssql) | přepněte na režim Com |
| HTTP **502** | konektor se nedostal k WIN-PAKu | podívejte se do Diagnostiky, hláška tam bude konkrétnější |
| HTTP **422** | WIN-PAK zápis odmítl | hláška nese jeho stavový kód, např. „číslo karty už existuje“ nebo „neplatná přístupová úroveň“ |
| Diagnostika zelená, ale číselníky prázdné | špatný název účtu nebo podúčtu | porovnejte s výsledkem kontroly „Účty“ — ta vypíše pracovní účet |
| `WIN-PAK má více účtů a v konfiguraci konektoru není vybraný žádný` | WIN-PAK má víc účtů | vyberte účet v Nastavení; hláška vyjmenuje, které jsou k dispozici |
| `WIN-PAK <metoda>: … (HRESULT 0x…)` | konkrétní volání COM odmítl WIN-PAK | text za dvojtečkou je původní hláška WIN-PAKu; u `IsConnected` a systémových údajů jde často o rozdíl verze API, zbytek funguje |
| `WIN-PAK <metoda>: Type mismatch. (0x80020005)` | typ argumentu při pozdní vazbě | konektor takové volání sám zopakuje s opravenými argumenty (výstupní řetězec místo null, 32bitové id); když hláška zůstane, pošlete název metody — signatura se v příručce liší |
| Držitelé a přístupové úrovně 0, ale čtečky a časové zóny fungují | podúčet | držitelé i úrovně jsou pod podúčtem; konektor doplní jediný podúčet účtu sám, u více podúčtů ho vyberte v Nastavení |
| `Cannot access a disposed object. Object name: 'System.Threading.SemaphoreSlim'` | provider zlikvidovaný DI kontejnerem po prvním požadavku (do v1.12.2) | aktualizovat konektor na v1.12.3 nebo novější |
| `WIN-PAK <vlastnost>: Unknown name … objekt má členy: …` | vlastnost se ve skutečné instalaci jmenuje jinak než v příručce | pošlete hlášku — vypisuje skutečné členy objektu, název se doplní do konektoru |
| `Method 'System.String.NoteFieldName' not found` | WIN-PAK vrátil pole vyhledávání jako prosté řetězce (do v1.12.3) | aktualizovat konektor; stránky Features od té verze při odmítnuté pomocné položce zobrazí zbytek |
| `WIN-PAK NoteField: Number of parameters specified does not match the expected number` | poznámkové pole držitele je indexované | od v1.12.2 konektor čte první poznámkové pole; výpis držitelů kvůli poznámce nepadá |

> **Bezpečnost:** `GetWPDSN` vrací ve skutečné instalaci celý připojovací řetězec
> k databázi WIN-PAKu včetně uživatele a hesla. Do v1.12.1 ho konektor zobrazoval
> v diagnostice i vracel přes `GET /api/v1/system`; od v1.12.2 jde ven jen název
> zdroje, serveru a databáze. Pokud diagnostika starší verze někde zůstala
> na screenshotu nebo v logu, heslo databázového účtu WIN-PAKu změňte.
| ACS hlásí „konektor je jen pro čtení“ | běží režim Mssql nebo Mock | přepněte na Com |

Logy služby: Prohlížeč událostí → Windows Logs → Application, zdroj
`AcsWinPakConnector`.

## Výkon dotazů

Každé volání WIN-PAKu jde přes COM+ a pozdní vazbu a stojí řádově milisekundy
až desítky. Rozhoduje proto počet volání, ne velikost odpovědi:

- **Čtečky**: jeden výpis čteček + jeden výpis zařízení (názvy panelů), ať je
  čteček 8 nebo 785. Do v1.12.4 se název zařízení dotahoval pro každou čtečku
  zvlášť, a po `Type mismatch` ještě jednou — u 785 čteček přes 1 500 volání.
- **Držitelé**: jeden výpis držitelů + jeden výpis karet účtu, karty se přiřadí
  podle `CardHolderID`. Dřív se pro každého držitele volaly zvlášť.
- **Tvar argumentů** naučený při prvním `Type mismatch` se u dané metody použije
  rovnou, opakování platí jednou.
- **Číselníky** (účty, čtečky, přístupové úrovně, časové zóny, panely, zařízení)
  si konektor drží 60 s v paměti; zápis do číselníku paměť zahodí. Karty
  a držitelé se necachují — ty ACS mění.

Když je dotaz pomalý i tak, podívejte se do Prohlížeče událostí na časy
jednotlivých volání: pomalé bývá samo COM+ (vzdálený WIN-PAK, přetížený SQL),
což konektor neovlivní.

## Aktualizace konektoru

1. Stáhněte (nebo publikujte) novou verzi — viz krok 3.
2. `Stop-Service AcsWinPakConnector`.
3. Přepište soubory programu. **`appsettings.Local.json` nechte** — je v něm
   nastavení včetně hesel.
4. `Start-Service AcsWinPakConnector`, pak zkontrolujte Diagnostiku.

**Pořadí vůči ACS:** konektor aktualizujte **dřív** než ACS. Nové verze si mezi
sebou předávají stav karty číselně, starý konektor ho posílal textem — kdyby šlo
ACS první, zpětná synchronizace přístupů by na starém konektoru skončila chybou.

## Dokud není licence API

Konektor jde provozovat i bez `SRVWPPAPI`, ale s omezením:

- **Režim `Mssql`** — čte přímo z databáze WIN-PAK (SQL login stačí s právem
  `SELECT`). Dotazy jsou konfigurovatelné v GUI, protože schéma WIN-PAKu je
  proprietární a mezi verzemi se liší. Zápis nefunguje: schválené přístupy
  zadává správce karet ve WIN-PAKu ručně a v ACS je jen potvrdí.
- **Režim `Mock`** — ukázková data v paměti. Slouží k vývoji a k předvedení ACS
  bez WIN-PAKu, do provozu nepatří.

Přepnutí na `Com` je později jen změna režimu v GUI; REST rozhraní se nemění,
takže ACS se nijak nepřekonfiguruje.
