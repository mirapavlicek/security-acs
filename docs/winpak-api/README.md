# Honeywell WIN-PAK 4.9 — API a jeho napojení na ACS

Tento dokument popisuje **skutečné** rozhraní WIN-PAKu podle oficiálních příruček
(Database Server API Guide a Communication Server API, build 1090.7.6) a to, jak
z něj ACS získává REST.

## Shrnutí: REST ve WIN-PAKu není

WIN-PAK 4.9 nemá REST ani jiné HTTP rozhraní. Obě dokumentovaná API jsou
**COM objekty vystavené přes DCOM** (COM+ aplikace), tedy Windows-only a
volatelné jen z počítače, který má nainstalovaný COM+ proxy balíček WIN-PAK serveru:

| API | Knihovna | COM+ aplikace | K čemu je |
| --- | --- | --- | --- |
| Database Server API | `NCIHelper.dll` | WIN-PAK CS DBServer Helper | karty, držitelé, přístupové úrovně, čtečky, časové zóny, svátky, účty |
| Communication Server API | `ACCW.dll` | WIN-PAK CS ComServer Helper | stav a ovládání hardwaru (dveře, výstupy, panely) + odběr událostí |

Z toho plyne architektura, kterou projekt používá:

```
ACS (Linux, .NET 10)  ──REST/HTTPS──▶  WinPak Connector (Windows služba na WIN-PAK serveru)  ──DCOM──▶  WIN-PAK
```

REST je tedy použitý všude, kde to jde — celá komunikace ACS ↔ konektor. Nativní
COM zůstává jen na posledním úseku uvnitř serveru, kde jinou možnost nemáme.
Konektor je v `src/Acs.WinPakConnector`.

## Database Server API (`NCIHelper.dll`)

COM objekty: `NCIHelper.Application`, `NCIHelper.Card`, `NCIHelper.CardHolder`,
`NCIHelper.AccessLevel`, `NCIHelper.TimeZone`, `NCIHelper.WPAccount`,
`NCIHelper.HWDevice`, `NCIHelper.NoteField`.

Veškerá funkčnost visí na objektu `Application` (přes 130 metod). Metody vracejí
`HRESULT` (`S_OK` = 0) a výsledky předávají `[out]` parametry — kolekce jako
`VARIANT` s polem COM objektů.

### Metody, které konektor volá

| COM volání (dokumentovaná signatura) | Použití v konektoru |
| --- | --- |
| `Login(user, password, domain, out userId)` | přihlášení; `userId > 0` = úspěch, `-1` = selhání |
| `ConnectWPDatabase(user, password, domain, out status, userId)` | připojení k DB serveru; `status = -2` = spojení selhalo |
| `IsConnected(out connected)` | health check |
| `Logout()`, `DisconnectWPDatabase()` | ukončení relace |
| `GetAccounts(out accounts)` | seznam účtů (`WPAccount.AccountID`, `AccountName`) |
| `GetSubAccountsByAccountID(accountId, out subAccounts)` | podúčty |
| `GetReadersByAccountName(account, out readers)` | čtečky (`HWDevice`) |
| `GetAccessLevelsByAccountName(account, subAccount, out levels)` | přístupové úrovně účtu |
| `GetAllAccessLevels(out levels)` | přístupové úrovně napříč účty |
| `GetCardHoldersByAccountName(account, subAccount, out holders)` | držitelé karet |
| `GetCardHolderByCardHolderID(id, out holder)` | jeden držitel |
| `GetCardsByCHID(cardHolderId, out cards)` | karty držitele |
| `GetCardbyCardNumber(cardNo, account, subAccount, out card)` | karta podle čísla |
| `AddCardHolder(cardHolder, out status)` | založení držitele |
| `EditCardHolder(cardHolderId, cardHolder, out status)` | úprava držitele |
| `AddUpdateCard(recordId, cardNo, accountId, subAccountId, status, issue, cardHolderId, pin, activation, expiration, backdrop1, backdrop2, multiple, accessLevelIds)` | založení i úprava karty jedním voláním (`recordId = 0` → nová karta) |
| `DeleteCard(cardNo, account, subAccount, out status)` | zrušení karty |

### Návratové stavy zápisových metod

`AddCard` / `EditCard` / `AddUpdateCard`:

| Kód | Význam |
| --- | --- |
| 0 | úspěch |
| 1 | operace se nezdařila |
| 101 | číslo karty už existuje |
| 102 | neplatné číslo karty |
| 103 | neplatný stav karty |
| 104 | neplatná přístupová úroveň |
| 105 | neplatný účet / podúčet |
| 106 | neplatný rok aktivace |
| 107 | neplatné datum aktivace |
| 108 | neplatná délka karty |
| 109 | neplatný PIN |
| 110 | neplatný typ přístupu |
| 111–115 | neplatná nastavení NetAXS (usage limit, expirace, typ karty, dočasná/omezená karta) |

`AddCardHolder` / `EditCardHolder`: 0 úspěch, 1 neúspěch, 105 neplatný účet,
301 neplatné jméno/příjmení (nebo id držitele), 302 neplatná délka jména.

Stav karty (`lCardStatus`): 1 = aktivní, 2 = neaktivní, 3 = trace, 4 = ztracená/odcizená.

### Důležitý detail datového modelu

**Přístupové úrovně ve WIN-PAKu patří kartě, ne držiteli.** Držitel je jen
jmenný záznam; nositelem oprávnění je karta (`Card.AccessLevels`, parametr
`alAccessLevelIDs` v `AddUpdateCard`). Konektor proto operaci „přiřaď držiteli
přístupovou úroveň“ provádí jako: načti karty držitele → u každé aktivní karty
přepočítej seznam úrovní → `AddUpdateCard`. Navenek zůstává REST rozhraní
orientované na držitele, protože tak o přístupech uvažuje schvalovací workflow.

## Communication Server API (`ACCW.dll`)

| COM volání | Použití v konektoru |
| --- | --- |
| `InitServer(caller, viewType, user, password, userId)` | registrace klienta |
| `InitServer2(caller, viewType, user, password, domain, userId)` | totéž při přihlašování doménovými údaji |
| `DoneServer(caller)` | odhlášení |
| `IsConnected2(serverType, out xmlStatus)` | stav serverů (XML `<NLZ>`) |
| `ListConnectedDevices(out devices)` | připojená zařízení (ADV) |
| `GetDoorStatus2(readerHid, out doorStatus)` | stav dveří (otevřeno/zavřeno + XML detail) |
| `EntryPointLockByID(hid)` / `EntryPointUnLockByID(hid)` | zamknout / odemknout vstup |
| `PulseByHID(hid)` / `TimedPulseByHID(hid, units, value)` | krátké otevření (units: 0 s, 1 min, 2 h) |
| `DoorModeByHID(hid, mode)` | režim dveří |

Režimy dveří: 1 zakázáno, 2 odemčeno, 3 zamčeno, 4 jen site code, 5 jen karta,
6 jen PIN, 7 karta a PIN, 8 karta nebo PIN.

Typ serveru pro `IsConnected2`: 0 všechny, 1 DB server, 2 komunikační server,
3 plánovač, 4 guard tour, 8 command file server, 9 muster server.

### Události v reálném čase

Komunikační server volá zpět metodu `GotMessage` klientského COM objektu a
předává XML zprávu ohraničenou `<NLZ>…</NLZ>`. Podstatné značky:

| Značka | Význam |
| --- | --- |
| `<Idx>` | > 0 = alarm, -1 = událost |
| `<AckStatus>` | 0 zrušený alarm, 1 nový, 2 potvrzený |
| `<EventID>` | číselný kód události (např. 701 platná karta, 405 výpadek napájení) |
| `<HID>` | id hardwarového zařízení — spojka na čtečku |
| `<CardNumber>`, `<FullName>` | karta a držitel |
| `<Date>`, `<Time>`, `<Prio>`, `<Status>`, `<RP>` | čas, priorita, popis stavu, čtečka/bod |
| `<Account>`, `<SubAccount>` | účet a podúčet |

Stejný formát `<NLZ>` vracejí i `GetDoorStatus2` (`<Door_IsOpen>`,
`<Door_IsShunted>`, `<Door_ForcedOpen>`, `<Door_Ajar>`, `<ADV_Hid>`,
`<ADV_DeviceName>`) a `IsConnected2` (`<SrvId>`, `<Server>`, `<Connected>`,
`<SerType>`). Hodnoty stavů: 0 = ne, 1 = ano, -1 = neznámo.

Konektor tyto zprávy parsuje v `Providers/Com/NlzMessage.cs`.

## Instalace na WIN-PAK serveru

1. WIN-PAK musí být nainstalovaný s volbou **Web** — pak se automaticky nasadí
   `DatabaseAPIServer`. API je součástí edic SE/PE (licence `SRVWPPAPI`), v XE není.
2. V `dcomcnfg` → Component Services → COM+ Applications ověřte, že existují
   **WIN-PAK CS DBServer Helper** a **WIN-PAK CS ComServer Helper**.
3. Pokud konektor běží na jiném stroji než WIN-PAK, exportujte z obou COM+
   aplikací **Application proxy** a nainstalujte ho na stroj s konektorem.
   Doporučené nasazení je ale přímo na WIN-PAK serveru — odpadne DCOM přes síť
   i s tím spojené firewallové a autentizační starosti.
4. Účet, pod kterým konektor běží, musí mít práva na volání COM+ aplikací a
   platné přihlašovací údaje operátora WIN-PAK (ty konektor předává v `Login`).
5. Podporované systémy podle příručky: Windows Server 2019/2016/2012 R2/2008 R2,
   Windows 10 a 7 (64bit).

## Mapování na REST konektoru

| REST konektoru | COM volání |
| --- | --- |
| `GET /api/v1/info` | — (metadata konektoru) |
| `GET /api/v1/status` | `IsConnected`, `IsConnected2` |
| `GET /api/v1/accounts` | `GetAccounts`, `GetSubAccountsByAccountID` |
| `GET /api/v1/readers` | `GetReadersByAccountName` |
| `GET /api/v1/access-levels` | `GetAccessLevelsByAccountName` / `GetAllAccessLevels` |
| `GET /api/v1/cardholders` | `GetCardHoldersByAccountName` + `GetCardsByCHID` |
| `GET /api/v1/cardholders/{id}` | `GetCardHolderByCardHolderID` + `GetCardsByCHID` |
| `POST /api/v1/cardholders` | `AddCardHolder` |
| `PUT /api/v1/cardholders/{id}` | `EditCardHolder` |
| `GET /api/v1/cards/{cardNumber}` | `GetCardbyCardNumber` |
| `PUT /api/v1/cards/{cardNumber}` | `AddUpdateCard` |
| `DELETE /api/v1/cards/{cardNumber}` | `DeleteCard` |
| `POST /api/v1/cardholders/{id}/access-levels` | `GetCardsByCHID` + `AddUpdateCard` |
| `DELETE /api/v1/cardholders/{id}/access-levels/{alId}` | `GetCardsByCHID` + `AddUpdateCard` |
| `GET /api/v1/devices` | `ListConnectedDevices` |
| `GET /api/v1/doors/{hid}` | `GetDoorStatus2` |
| `POST /api/v1/doors/{hid}/pulse` | `PulseByHID` / `TimedPulseByHID` |
| `POST /api/v1/doors/{hid}/lock` | `EntryPointLockByID` |
| `POST /api/v1/doors/{hid}/unlock` | `EntryPointUnLockByID` |
| `POST /api/v1/doors/{hid}/mode` | `DoorModeByHID` |

## Zdrojové příručky

Příručky jsou pod NDA a nejsou v repozitáři. Pracovalo se s:

- WIN-PAK 4.9 Build 1090.7.6 — Database Server API Guide (rev. 1.6, prosinec 2020)
- WIN-PAK 4.9 Build 1090.7.6 — Communication Server API (rev. 1.6, září 2021)

Veřejné datasheety v tomto adresáři:

| Soubor | Obsah |
| --- | --- |
| `winpak-pe-api-datasheet-se4-pe4.pdf` | přehled Database a Communication API |
| `winpak-4.9-datasheet.pdf` | edice, systémové požadavky, licence `SRVWPPAPI` |
