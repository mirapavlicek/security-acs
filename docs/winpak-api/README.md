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

### Pokrytí konektorem

Konektor implementuje **139 ze 147 metod** objektu `Application`, které příručka
popisuje včetně signatury, a **všech 42 funkcí** komunikačního serveru.
Nepokryté zůstávají jen ty, u kterých příručka uvádí název v seznamu metod, ale
neuvádí signaturu ani parametry — bez toho je nelze zavolat spolehlivě:

| Nepokryté volání | Důvod |
| --- | --- |
| `AddMasterHoliday`, `EditMasterHoliday`, `DeleteMasterHoliday`, `GetMasterHolidays`, `GetMasterHolidayByName` | příručka neuvádí signaturu; běžné svátky (`AddHoliday` a spol.) pokryté jsou |
| `ConfigureDoorSchedule` | příručka neuvádí signaturu; spuštění door schedule přes komunikační server (`ExecuteDoorSchedule`) pokryté je |
| `GetGrpIDForPanel` | příručka neuvádí signaturu |
| `SetOperatorID` | příručka neuvádí signaturu; čtení operátora (`GetCurrentOperator`) pokryté je |

Dvě položky v obsahu příručky jsou překlepy v názvu téže metody
(`GetAccessLevelForReassign` → `GetAccesslevelsForReassign`,
`GetConfiguredHolidayGrpousByPanel` → `GetConfiguredHolidayGroupsByPanel`);
obě jsou implementované pod skutečným názvem ze signatury.

### Relace a účty

| COM volání (dokumentovaná signatura) | Použití v konektoru |
| --- | --- |
| `Login(user, password, domain, out userId)` | přihlášení; `userId > 0` = úspěch, `-1` = selhání |
| `ConnectWPDatabase(user, password, domain, out status, userId)` | připojení k DB serveru; `status = -2` = spojení selhalo |
| `IsConnected(out connected)` | health check |
| `Logout()`, `DisconnectWPDatabase()`, `Disconnect()` | ukončení relace |
| `GetAccounts(out accounts)` | seznam účtů (`WPAccount.AccountID`, `AccountName`) |
| `GetSubAccountsByAccountID(accountId, out subAccounts)` | podúčty |
| `GetAccountByAcctID`, `GetAccountNameByAcctID`, `GetSubAccountNameBySubAcctID` | dohledání účtu podle id |
| `GetAcctIDByHID(hid, out accountId)` | účet, kterému patří zařízení |

### Karty a držitelé

| COM volání | Použití v konektoru |
| --- | --- |
| `GetCardsByAccountName`, `GetCardsWithoutCHIDByAcctID`, `GetCardsByCHID` | výpisy karet |
| `GetCardbyCardNumber(cardNo, account, subAccount, out card)` | karta podle čísla |
| `AddUpdateCard(recordId, cardNo, accountId, subAccountId, status, issue, cardHolderId, pin, activation, expiration, backdrop1, backdrop2, multiple, accessLevelIds)` | založení i úprava karty jedním voláním (`recordId = 0` → nová karta) |
| `AddUpdateCardEx(… + tempCard, cardType, usageLimit, limitedCard, trigger)` | totéž s NetAXS volbami |
| `AddCard`, `EditCard` | objektové varianty zápisu |
| `DeleteCard(cardNo, account, subAccount, out status)` | zrušení karty |
| `BulkAddCards`, `BulkDeleteCards` | rozsah karet najednou |
| `GetMaxCardNumberLength`, `GetCardNumeric` | pravidla čísel karet v instalaci |
| `GetCardHoldersByAccountName`, `GetCardHolderByCardHolderID` | držitelé |
| `AddCardHolder`, `EditCardHolder`, `DeleteCardHolder` | správa držitelů |
| `GetCardHolderSearchFieldsByAccountName`, `GetCardHoldersOnSearch` | vyhledávání v databázi WIN-PAK |
| `GetNoteFieldTemplateDetailsByAccount` | šablony poznámkových polí |
| `GetPhoto`, `GetPhotoSize`, `ImportPhoto`, `DeletePhoto` | fotky držitele |
| `GetSig`, `GetSigSize`, `ImportSig`, `DeleteSignature`, `DeleteSig` | podpisy držitele |

### Přístupové úrovně

| COM volání | Použití v konektoru |
| --- | --- |
| `GetAccessLevelsByAccountName`, `GetAllAccessLevels`, `GetAccessLevelByName`, `GetAccessLevelNameByID`, `GetAccessLevelType` | čtení |
| `GetAccessTreeByName` | strom přístupů včetně časových zón |
| `CreateAccessLevel`, `AddAccessLevel`, `EditAccessLevel`, `AddUpdateAL` | zakládání a úprava |
| `ConfigureAccessLevel`, `ConfigureEntranceAccess` | přiřazení čteček, časových zón a skupin |
| `IsolateAccessLevel`, `GetAccesslevelsForReassign`, `ReassignAccessLevel` | přeřazení karet před zrušením úrovně |
| `DeleteAccessLevel`, `DeleteAL` | zrušení úrovně |

### Časové zóny a svátky

| COM volání | Použití v konektoru |
| --- | --- |
| `GetTimeZonesByAccountName`, `GetAllTimezones`, `GetTimeZoneByName`, `GetTimezoneNameByID` | čtení |
| `AddTimezone`, `CreateTimezone`, `EditTimeZone`, `DeleteTimeZone` | správa zón |
| `ConfigureTimeZoneRange`, `GetTimeZoneRangesByTZID`, `DeleteTimeZoneRange` | intervaly zóny |
| `Isolate*ForTZReassign` (operátoři, panely, úrovně, akční skupiny, karty, zařízení) | kdo zónu používá |
| `GetTZsForReassign`, `GetTZsForOperatorReassign` | kandidáti na náhradu |
| `Reassign*TZ` (operátoři, úrovně, akční skupiny, karty, zařízení) | přeřazení na jinou zónu |
| `IsolatePanelsForTZDelete`, `DeletePanelTZ` | odebrání zóny z panelů |
| `GetReaderTZDetailsByAccountId`, `LoopTimeZoneByAccountId`, `GetDirectPointTZDetailsofReader` | souhrny zón u čteček |
| `AddHoliday`, `EditHoliday`, `DeleteHoliday`, `GetHolidayByID` | svátky |
| `AddHolidayGroup`, `EditHolidayGroup`, `DeleteHolidayGroup`, `GetHolidayGroupsByAcctID`, `GetHolidaysByHolidayGroupID` | skupiny svátků |
| `IsolatePanelsForHGDelete`, `ConfigurePanelHolidayGroup`, `GetConfiguredHolidayGroupsByPanel` | svátky na panelech |

### Hardware a systém

| COM volání | Použití v konektoru |
| --- | --- |
| `GetReadersByAccountName`, `GetADVDetailsByAccountName`, `GetDeviceNameByHWDeviceID`, `GetDevNameByDeviceID` | čtečky a zařízení |
| `GetPanelsByAcctID`, `GetOutputsByPanelID`, `GetGroupsByPanelID`, `IsGroupChecked` | panely, výstupy, skupiny |
| `ConfigureOutputTimezone`, `ConfigureOutputTimezoneEx`, `ConfigureGroupTimezone`, `ConfigurePanelTimeZone` | časové zóny hardwaru |
| `GetAssociatedTimezoneOfOutput`, `GetAssociatedTimezoneOfOutputEX`, `GetAssociatedTimezoneOfGroup`, `GetAvailableTimezonesOfPanel`, `GetConfiguredTimezonesByPanel` | čtení nastavení |
| `GetAccessAreaBranchesByAccountName`, `GetReadersInAccessAreaBranch`, `GetAvailableTimezonesOfBranch` | přístupové oblasti |
| `GetAvailableTimeZonesOfReader`, `GetAvailableTimeZonesOfAccessReader`, `GetAssociatedTimeZoneOfReader`, `GetAvailableGroupsofReader`, `GetAssociatedGroupofReader` | konfigurace čteček |
| `GetWPDSN`, `GetWPDBServerTZ`, `GetWPDBServerTZoffset`, `GetCurrentOperator`, `GetConfiguredWPDomains`, `GetAccountEmailIDs` | systémové údaje |
| `GetSchedule`, `AddEditSchedule`, `DeleteSchedule` | plány reportů |
| `GetTemplate`, `AddEditTemplate`, `DeleteTemplate` | šablony reportů |
| `GetBadgeData`, `GetBadgeDimension` | odznaky pro tisk karet |

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

Konektor pokrývá všech 42 dokumentovaných funkcí.

| COM volání | Použití v konektoru |
| --- | --- |
| `InitServer(caller, viewType, user, password, userId)` | registrace klienta |
| `InitServer2(caller, viewType, user, password, domain, userId)` | totéž při přihlašování doménovými údaji |
| `DoneServer(caller)` | odhlášení |
| `GetConfiguredWPDomains(out domains)` | domény; jediné volání použitelné před registrací |
| `IsConnected(out status)`, `IsConnected2(serverType, out xmlStatus)` | stav serverů (XML `<NLZ>`) |
| `ListConnectedDevices(out devices)` | připojená zařízení (ADV) |
| `GetStatus(hid, deviceType, out statusId)`, `GetDefaultACRMode(hid, out mode)` | stav a výchozí režim zařízení |
| `GetDoorStatus(readerHid, out code)`, `GetDoorStatus2(readerHid, out doorStatus)` | stav dveří (číselný i XML detail) |
| `EntryPointLockByID(hid)` / `EntryPointUnLockByID(hid)` | zamknout / odemknout vstup |
| `EntryPointLock(hid, point)` / `EntryPointUnLock(hid, point)` | totéž pro konkrétní bod |
| `PulseByHID(hid)` / `TimedPulseByHID(hid, units, value)` | krátké otevření (units: 0 s, 1 min, 2 h) |
| `DoorModeByHID(hid, mode)` | režim dveří |
| `LockUnLockAllDoors(accountId, isLock)`, `RefreshDoorsByAccId(accountId, out status)` | hromadné operace se dveřmi |
| `ExecuteDoorSchedule(panelHid, panelType, entranceId, entrancePointId, tzId)` | spuštění door schedule |
| `GetNetAXSDoorModeByHID`, `SetNetAXSDoorModeByHID` | režim dveří NetAXS panelu |
| `AckAlarm(hid, point)`, `ClrAlarm(hid, point)`, `AddNote(hid, point, note)` | práce s alarmy |
| `GetDetailsByID(hid, point, out details)` | detail transakce |
| `AlarmShuntByHID`, `AlarmUnShuntByHID`, `AlarmUnShunt(hid, point)`, `AlarmPulse` | shuntování alarmů |
| `BufferByHID(hid, mode)`, `UnBufferByHID(hid, mode)` | bufferování transakcí (0 hard, 1 soft) |
| `Energize(hid)`, `DeEnergize(hid)` | spínání výstupů |
| `PanelInitialize(hid, type, tasks)`, `PanelCancelInitialize(hid)`, `PanelRefreshTZByHID(hid)` | inicializace panelů |
| `RestoreTZByHID(hid)` | návrat zařízení pod kontrolu časové zóny |
| `AddFilterHID`, `RemoveFilterHID`, `GetFilterHIDs` | filtr odebíraných událostí podle zařízení |
| `AddFilterCommServerID`, `RemoveFilterCommServerID`, `GetFilterCommServerIDs` | filtr podle komunikačního serveru |
| `GetMusterElemenets(out xml, areaId, accountId, sortField, sortOrder, out status)` | muster report |
| `ExecCustomCommand(hid, command)` | vlastní příkaz pro zařízení |

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

### Základ (používá hlavní ACS aplikace)

| REST konektoru | COM volání |
| --- | --- |
| `GET /api/v1/info` | — (metadata konektoru) |
| `GET /api/v1/status` | `IsConnected`, `IsConnected2` |
| `GET /api/v1/accounts`, `GET /api/v1/accounts/{id}` | `GetAccounts`, `GetSubAccountsByAccountID`, `GetAccountByAcctID` |
| `GET /api/v1/readers` | `GetReadersByAccountName` |
| `GET /api/v1/access-levels` | `GetAccessLevelsByAccountName` / `GetAllAccessLevels` |
| `GET /api/v1/cardholders`, `GET /api/v1/cardholders/{id}` | `GetCardHoldersByAccountName`, `GetCardHolderByCardHolderID` + `GetCardsByCHID` |
| `POST /api/v1/cardholders`, `PUT /api/v1/cardholders/{id}`, `DELETE /api/v1/cardholders/{id}` | `AddCardHolder`, `EditCardHolder`, `DeleteCardHolder` |
| `GET/PUT/DELETE /api/v1/cards/{cardNumber}` | `GetCardbyCardNumber`, `AddUpdateCard`, `DeleteCard` |
| `POST` / `DELETE /api/v1/cardholders/{id}/access-levels[/{alId}]` | `GetCardsByCHID` + `AddUpdateCard` |
| `GET /api/v1/devices` | `ListConnectedDevices` |
| `GET /api/v1/doors/{hid}`, `.../status-code` | `GetDoorStatus2`, `GetDoorStatus` |
| `POST /api/v1/doors/{hid}/pulse`, `/lock`, `/unlock`, `/mode` | `PulseByHID` / `TimedPulseByHID`, `EntryPointLockByID`, `EntryPointUnLockByID`, `DoorModeByHID` |
| `GET /api/v1/events` | zpětné volání `GotMessage` |

### Karty a držitelé (rozšířené)

| REST konektoru | COM volání |
| --- | --- |
| `GET /api/v1/cards?withoutHolder=` | `GetCardsByAccountName`, `GetCardsWithoutCHIDByAcctID` |
| `PUT /api/v1/cards/{cardNumber}/netaxs` | `AddUpdateCardEx` |
| `POST` / `PUT /api/v1/cards/{cardNumber}/object` | `AddCard`, `EditCard` |
| `POST /api/v1/cards/bulk`, `/bulk-delete` | `BulkAddCards`, `BulkDeleteCards` |
| `GET /api/v1/cardholders/search-fields`, `POST /api/v1/cardholders/search` | `GetCardHolderSearchFieldsByAccountName`, `GetCardHoldersOnSearch` |
| `GET /api/v1/note-field-templates` | `GetNoteFieldTemplateDetailsByAccount` |
| `GET/PUT/DELETE /api/v1/cardholders/{id}/photo/{index}` | `GetPhoto` + `GetPhotoSize`, `ImportPhoto`, `DeletePhoto` |
| `GET/PUT/DELETE /api/v1/cardholders/{id}/signature/{index}[?shortVariant=true]` | `GetSig` + `GetSigSize`, `ImportSig`, `DeleteSignature` / `DeleteSig` |

### Přístupové úrovně

| REST konektoru | COM volání |
| --- | --- |
| `GET /api/v1/access-levels/{name}`, `/tree` | `GetAccessLevelByName`, `GetAccessTreeByName` |
| `POST /api/v1/access-levels`, `/object`, `PUT /api/v1/access-levels/{id}`, `/by-name/{name}` | `CreateAccessLevel`, `AddAccessLevel`, `AddUpdateAL`, `EditAccessLevel` |
| `POST /api/v1/access-levels/{name}/readers`, `/entrance` | `ConfigureAccessLevel`, `ConfigureEntranceAccess` |
| `GET /api/v1/access-levels/{name}/cards`, `/reassign-candidates`, `POST .../reassign` | `IsolateAccessLevel`, `GetAccesslevelsForReassign`, `ReassignAccessLevel` |
| `DELETE /api/v1/access-levels/{name}`, `POST /{id}/delete-with-replacement` | `DeleteAccessLevel`, `DeleteAL` |

### Časové zóny a svátky

| REST konektoru | COM volání |
| --- | --- |
| `GET /api/v1/time-zones`, `/by-name/{name}` | `GetTimeZonesByAccountName` / `GetAllTimezones`, `GetTimeZoneByName` |
| `POST /api/v1/time-zones`, `/simple`, `PUT /by-name/{name}`, `DELETE /{id}` | `AddTimezone`, `CreateTimezone`, `EditTimeZone`, `DeleteTimeZone` |
| `GET/PUT /api/v1/time-zones/{id}/ranges`, `DELETE .../ranges/{rangeId}` | `GetTimeZoneRangesByTZID`, `ConfigureTimeZoneRange`, `DeleteTimeZoneRange` |
| `GET /api/v1/time-zones/{id}/usage` | všechna `Isolate*ForTZReassign` a `IsolatePanelsForTZDelete` |
| `GET /api/v1/time-zones/{id}/reassign-candidates` | `GetTZsForReassign`, `GetTZsForOperatorReassign` |
| `POST /api/v1/time-zones/reassign` | všechna `Reassign*TZ` |
| `POST /api/v1/time-zones/{id}/remove-from-panels` | `DeletePanelTZ` |
| `GET /api/v1/holidays/{id}`, `POST /api/v1/holidays`, `PUT /by-name/{name}`, `DELETE /{id}` | `GetHolidayByID`, `AddHoliday`, `EditHoliday`, `DeleteHoliday` |
| `GET /api/v1/holiday-groups`, `/{id}/holidays`, `/{id}/panels`, `POST`, `PUT /by-name/{name}`, `DELETE /{id}` | `GetHolidayGroupsByAcctID`, `GetHolidaysByHolidayGroupID`, `AddHolidayGroup`, `EditHolidayGroup`, `DeleteHolidayGroup` |

### Hardware

| REST konektoru | COM volání |
| --- | --- |
| `GET /api/v1/hardware` | `GetADVDetailsByAccountName` |
| `GET /api/v1/panels`, `/{id}/outputs`, `/{id}/groups` | `GetPanelsByAcctID`, `GetOutputsByPanelID`, `GetGroupsByPanelID` |
| `GET/PUT /api/v1/panels/{id}/time-zones` | `GetAvailableTimezonesOfPanel` / `GetConfiguredTimezonesByPanel`, `ConfigurePanelTimeZone` |
| `GET/PUT /api/v1/panels/{id}/holiday-groups` | `GetConfiguredHolidayGroupsByPanel`, `ConfigurePanelHolidayGroup` |
| `PUT /api/v1/panels/{id}/outputs/{outputId}/time-zone` | `ConfigureOutputTimezone` / `ConfigureOutputTimezoneEx` |
| `PUT /api/v1/panels/{id}/groups/{groupId}/time-zone` | `ConfigureGroupTimezone` |
| `GET /api/v1/access-areas`, `/{branch}/readers`, `/{branch}/time-zones` | `GetAccessAreaBranchesByAccountName`, `GetReadersInAccessAreaBranch`, `GetAvailableTimezonesOfBranch` |
| `GET /api/v1/readers/{name}/time-zones[?forAccount=true]`, `/groups` | `GetAvailableTimeZonesOfReader` / `GetAvailableTimeZonesOfAccessReader`, `GetAvailableGroupsofReader` |
| `GET /api/v1/associated-time-zone`, `/associated-group` | `GetAssociatedTimeZoneOfReader`, `GetAssociatedTimezoneOfOutput(EX)`, `GetAssociatedTimezoneOfGroup`, `GetAssociatedGroupofReader` |
| `GET /api/v1/lookup/{kind}` | `GetDeviceNameByHWDeviceID`, `GetAcctIDByHID`, `GetAccessLevelNameByID`, `GetTimezoneNameByID`, `GetAccountNameByAcctID`, `GetSubAccountNameBySubAcctID`, `GetAccountEmailIDs`, `GetReaderTZDetailsByAccountId`, `LoopTimeZoneByAccountId`, `GetDirectPointTZDetailsofReader`, `IsGroupChecked` |

### Systém a povely

| REST konektoru | COM volání |
| --- | --- |
| `GET /api/v1/system` | `GetWPDSN`, `GetWPDBServerTZ`, `GetWPDBServerTZoffset`, `GetMaxCardNumberLength`, `GetCardNumeric`, `GetAccessLevelType`, `GetCurrentOperator`, `GetConfiguredWPDomains` |
| `GET/PUT/DELETE /api/v1/schedules/{id}` | `GetSchedule`, `AddEditSchedule`, `DeleteSchedule` |
| `GET/PUT/DELETE /api/v1/templates/{id}` | `GetTemplate`, `AddEditTemplate`, `DeleteTemplate` |
| `GET /api/v1/badges/{id}` | `GetBadgeData`, `GetBadgeDimension` |
| `POST /api/v1/devices/{hid}/alarm/acknowledge`, `/alarm/clear`, `/note` | `AckAlarm`, `ClrAlarm`, `AddNote` |
| `GET /api/v1/devices/{hid}/transaction`, `/status` | `GetDetailsByID`, `GetStatus` |
| `POST /api/v1/devices/{hid}/shunt`, `/unshunt`, `/unshunt-point` | `AlarmShuntByHID`, `AlarmUnShuntByHID`, `AlarmUnShunt` |
| `POST /api/v1/devices/{hid}/entry-point/lock`, `/unlock` | `EntryPointLock`, `EntryPointUnLock` |
| `POST /api/v1/devices/{hid}/buffer`, `/unbuffer` | `BufferByHID`, `UnBufferByHID` |
| `POST /api/v1/devices/{hid}/energize`, `/de-energize`, `/restore-time-zone`, `/command` | `Energize`, `DeEnergize`, `RestoreTZByHID`, `ExecCustomCommand` |
| `POST /api/v1/panels/{hid}/initialize`, `/cancel-initialize`, `/refresh-time-zones` | `PanelInitialize`, `PanelCancelInitialize`, `PanelRefreshTZByHID` |
| `POST /api/v1/doors/lock-all`, `/refresh`, `/schedule` | `LockUnLockAllDoors`, `RefreshDoorsByAccId`, `ExecuteDoorSchedule` |
| `GET/PUT /api/v1/doors/{hid}/netaxs-mode` | `GetNetAXSDoorModeByHID`, `SetNetAXSDoorModeByHID` |
| `GET /api/v1/readers/{hid}/default-mode` | `GetDefaultACRMode` |
| `GET/POST/DELETE /api/v1/event-filters[/{id}]` | `GetFilterHIDs`, `AddFilterHID`, `RemoveFilterHID` (a varianty `*CommServerID`) |
| `GET /api/v1/muster` | `GetMusterElemenets` |

## Zdrojové příručky

Příručky jsou pod NDA a nejsou v repozitáři. Pracovalo se s:

- WIN-PAK 4.9 Build 1090.7.6 — Database Server API Guide (rev. 1.6, prosinec 2020)
- WIN-PAK 4.9 Build 1090.7.6 — Communication Server API (rev. 1.6, září 2021)

Veřejné datasheety v tomto adresáři:

| Soubor | Obsah |
| --- | --- |
| `winpak-pe-api-datasheet-se4-pe4.pdf` | přehled Database a Communication API |
| `winpak-4.9-datasheet.pdf` | edice, systémové požadavky, licence `SRVWPPAPI` |
