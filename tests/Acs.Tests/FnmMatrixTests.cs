using Acs.Domain.Entities;
using Acs.Infrastructure.Audit;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Organization;
using Acs.Infrastructure.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>
/// Schvalovací matice FN Motol (dokument „Schvalovací matice – EKV, parkování, kamery, EZS“):
/// každý řádek tabulek jako scénář — kategorie zaměstnance × vlastní / cizí úsek → očekávaný
/// řetěz schvalovatelů, schválení bez stupně pro vedení, odpovědná osoba cílového úseku,
/// eskalace samoschválení (vedoucí OVBKŘ → jeho nadřízený), odvození kategorií a úseků.
/// </summary>
public sealed class FnmMatrixTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly RequestWorkflowService _workflow;
    private readonly MatrixTemplateResult _matrices;

    // Hierarchie: ředitel → náměstek LPP → primář chirurgie → sestra; ředitel → náměstek PT → vedoucí OVBKŘ.
    private readonly Employee _director, _deputyLpp, _deputyPt, _chiefSurgery, _chiefInternal, _nurse, _ovbkr, _dostalova;
    private readonly AppUser _admin, _directorUser, _deputyLppUser, _deputyPtUser, _chiefSurgeryUser, _chiefInternalUser, _nurseUser, _ovbkrUser;
    private readonly OrgUnit _unitLpp, _unitSurgery, _unitInternal, _unitOvbkr;
    private readonly Reader _surgeryStore, _internalStore, _unknownDoor;
    private readonly ParkingPermitType _permitType;
    private readonly Site _site;

    public FnmMatrixTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var audit = new AuditService(_db);
        _workflow = new RequestWorkflowService(_db, audit);

        _director = new Employee { FirstName = "Ředitel", LastName = "Nemocnice", Department = "Ředitelství" };
        _deputyLpp = new Employee { FirstName = "Náměstek", LastName = "LPP", Department = "Úsek LPP", Manager = _director };
        _deputyPt = new Employee { FirstName = "Náměstek", LastName = "PT", Department = "Provozně-technický úsek", Manager = _director };
        _dostalova = new Employee { FirstName = "Marie", LastName = "Dostálová", Department = "Kancelář ředitele", Manager = _director };
        _chiefSurgery = new Employee { FirstName = "Primář", LastName = "Chirurgie", Department = "Chirurgická klinika", Manager = _deputyLpp };
        _chiefInternal = new Employee { FirstName = "Primář", LastName = "Interna", Department = "Interní klinika", Manager = _deputyLpp };
        _nurse = new Employee { FirstName = "Jana", LastName = "Sestra", Department = "Chirurgická klinika – JIP", Manager = _chiefSurgery };
        _ovbkr = new Employee { FirstName = "Vedoucí", LastName = "OVBKŘ", Department = "OVBKŘ", Manager = _deputyPt };
        _db.Employees.AddRange(_director, _deputyLpp, _deputyPt, _dostalova, _chiefSurgery, _chiefInternal, _nurse, _ovbkr);

        _admin = new AppUser { UserName = "admin", Roles = AppRole.Admin };
        _directorUser = new AppUser { UserName = "reditel", Employee = _director };
        _deputyLppUser = new AppUser { UserName = "namestek.lpp", Employee = _deputyLpp };
        _deputyPtUser = new AppUser { UserName = "namestek.pt", Employee = _deputyPt };
        _chiefSurgeryUser = new AppUser { UserName = "primar.chir", Employee = _chiefSurgery };
        _chiefInternalUser = new AppUser { UserName = "primar.int", Employee = _chiefInternal };
        _nurseUser = new AppUser { UserName = "sestra", Employee = _nurse };
        _ovbkrUser = new AppUser { UserName = "ovbkr", Employee = _ovbkr, DisplayName = "Vedoucí OVBKŘ" };
        _db.Users.AddRange(_admin, _directorUser, _deputyLppUser, _deputyPtUser, _chiefSurgeryUser, _chiefInternalUser, _nurseUser, _ovbkrUser);

        _unitLpp = new OrgUnit { Name = "Úsek LPP", Code = "LPP", HeadEmployee = _deputyLpp, DepartmentPatterns = "Úsek LPP" };
        _unitSurgery = new OrgUnit { Name = "Chirurgická klinika", Code = "CHIR", Parent = _unitLpp, HeadEmployee = _chiefSurgery, DepartmentPatterns = "Chirurgická klinika*" };
        _unitInternal = new OrgUnit { Name = "Interní klinika", Code = "INT", Parent = _unitLpp, HeadEmployee = _chiefInternal, DepartmentPatterns = "Interní klinika" };
        _unitOvbkr = new OrgUnit { Name = "OVBKŘ", HeadEmployee = _ovbkr, DepartmentPatterns = "OVBKŘ" };
        _db.OrgUnits.AddRange(_unitLpp, _unitSurgery, _unitInternal, _unitOvbkr);

        // Prostory: sklad chirurgie (úsek přes místnost), sklad interny (úsek přímo na čtečce), dveře bez vlastníka.
        var building = new Building { Name = "MOC" };
        var floor = new Floor { Building = building, Name = "1" };
        var surgeryRoom = new Room { Floor = floor, Name = "Sklad CHIR", OrgUnit = _unitSurgery };
        _surgeryStore = new Reader { Name = "Sklad chirurgie", IsActive = true, Room = surgeryRoom };
        _internalStore = new Reader { Name = "Sklad interny", IsActive = true, OrgUnit = _unitInternal };
        _unknownDoor = new Reader { Name = "Dveře bez vlastníka", IsActive = true };
        _db.AddRange(building, floor, surgeryRoom, _surgeryStore, _internalStore, _unknownDoor);

        _site = new Site { Name = "Motol", Code = "MOT" };
        _permitType = new ParkingPermitType { Name = "Zaměstnanec", Binding = PermitBinding.LicensePlate };
        _db.AddRange(_site, _permitType);
        _db.SaveChanges();

        new OrgStructureService(_db).RecalculateAsync(null).GetAwaiter().GetResult();
        _matrices = new MatrixTemplateService(_db, audit).CreateFnmAsync(_ovbkrUser.Id, null).GetAwaiter().GetResult();
        _db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<AccessRequestItem> RequestAsync(AppUser requester, Employee target, Reader reader)
    {
        var request = await _workflow.CreateRequestAsync(requester.Id, target.Id, [reader.Id], "test", requesterCanActForOthers: true);
        return request.Items.Single();
    }

    private async Task<AccessRequestItem> ReloadAsync(int itemId)
        => await _db.AccessRequestItems.AsNoTracking().Include(i => i.Decisions).Include(i => i.Stages).SingleAsync(i => i.Id == itemId);

    /// <summary>Kdo teď rozhoduje (id zaměstnanců) a jak se úroveň jmenuje.</summary>
    private async Task<(string? Level, List<int> Employees, bool AdminFallback)> WhoDecidesAsync(int itemId)
    {
        var item = await ReloadAsync(itemId);
        var resolution = await _workflow.ResolveCurrentLevelAsync(item);
        return resolution is null
            ? (null, [], false)
            : (resolution.Level.Name, resolution.Approvers.Select(a => a.EmployeeId ?? -1).ToList(), resolution.RequiresAdminFallback);
    }

    // ---------- Odvození kategorií a úseků ----------

    [Fact]
    public async Task Kategorie_se_odvodi_z_hierarchie()
    {
        var employees = await _db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id);
        Assert.Equal(EmployeeRank.Director, employees[_director.Id].Rank);
        Assert.Equal(EmployeeRank.Executive, employees[_deputyLpp.Id].Rank);
        Assert.Equal(EmployeeRank.Executive, employees[_deputyPt.Id].Rank);
        Assert.Equal(EmployeeRank.Executive, employees[_dostalova.Id].Rank); // přímo pod ředitelem, i bez podřízených
        Assert.Equal(EmployeeRank.Manager, employees[_chiefSurgery.Id].Rank);
        Assert.Equal(EmployeeRank.Manager, employees[_ovbkr.Id].Rank);      // bez podřízených, ale vede úsek OVBKŘ
        Assert.Equal(EmployeeRank.Staff, employees[_nurse.Id].Rank);
    }

    [Fact]
    public async Task Usek_se_priradi_podle_mapovani_oddeleni_a_rucni_zadani_se_nepreprisuje()
    {
        var employees = await _db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id);
        Assert.Equal(_unitSurgery.Id, employees[_nurse.Id].OrgUnitId);        // předpona „Chirurgická klinika*“
        Assert.Equal(_unitSurgery.Id, employees[_chiefSurgery.Id].OrgUnitId);
        Assert.Equal(_unitInternal.Id, employees[_chiefInternal.Id].OrgUnitId);
        Assert.Equal(_unitLpp.Id, employees[_deputyLpp.Id].OrgUnitId);
        Assert.Null(employees[_director.Id].OrgUnitId);                       // „Ředitelství“ nikdo nemapuje

        var nurse = await _db.Employees.SingleAsync(e => e.Id == _nurse.Id);
        nurse.OrgUnitId = _unitInternal.Id;
        nurse.OrgUnitManual = true;
        await _db.SaveChangesAsync();
        var result = await new OrgStructureService(_db).RecalculateAsync(null);
        Assert.Equal(_unitInternal.Id, (await _db.Employees.AsNoTracking().SingleAsync(e => e.Id == _nurse.Id)).OrgUnitId);
        Assert.Contains("Ředitelství", result.UnmatchedDepartments);
    }

    [Fact]
    public void Presna_shoda_oddeleni_ma_prednost_pred_predponou()
    {
        Assert.True(OrgUnit.MatchDepartment("Chirurgická klinika", "chirurgická klinika") > OrgUnit.MatchDepartment("Chir*", "Chirurgická klinika"));
        Assert.Equal(0, OrgUnit.MatchDepartment("Interní klinika", "Chirurgická klinika"));
        Assert.True(OrgUnit.MatchDepartment("Chirurgická klinika*", "Chirurgická klinika – JIP") > 0);
    }

    [Fact]
    public async Task Rucne_zadana_kategorie_se_neprepisuje()
    {
        var nurse = await _db.Employees.SingleAsync(e => e.Id == _nurse.Id);
        nurse.Rank = EmployeeRank.Manager;
        nurse.RankManual = true;
        await _db.SaveChangesAsync();
        await new OrgStructureService(_db).RecalculateAsync(null);
        Assert.Equal(EmployeeRank.Manager, (await _db.Employees.AsNoTracking().SingleAsync(e => e.Id == _nurse.Id)).Rank);
    }

    // ---------- Průvodce ----------

    [Fact]
    public async Task Pruvodce_zalozi_tri_matice_a_nastavi_vychozi_a_parkovaci()
    {
        var matrices = await _db.ApprovalMatrices.AsNoTracking().Include(m => m.Levels).ThenInclude(l => l.Approvers).ToListAsync();
        var access = matrices.Single(m => m.Name == MatrixTemplateService.AccessName);
        Assert.True(access.IsDefault);
        Assert.True(access.AutoApproveWhenNoLevels);
        Assert.Equal(4, access.Levels.Count);
        Assert.Contains(access.Levels, l => l.Approvers.Any(a => a.Kind == ApproverKind.AreaOwner));
        Assert.Contains(access.Levels, l => l.Approvers.Any(a => a.Kind == ApproverKind.User && a.UserId == _ovbkrUser.Id));

        var type = await _db.ParkingPermitTypes.AsNoTracking().SingleAsync(t => t.Id == _permitType.Id);
        Assert.Equal(_matrices.Parking.Id, type.ApprovalMatrixId);

        // Opakované spuštění nic nepřepíše.
        var again = await new MatrixTemplateService(_db, new AuditService(_db)).CreateFnmAsync(null, null);
        Assert.Equal(_matrices.Access.Id, again.Access.Id);
        Assert.Contains(again.Notes, n => n.Contains("už existuje"));
    }

    // ---------- EKV: řádky tabulky ----------

    [Fact]
    public async Task Radovy_vlastni_usek_schvaluje_jen_nadrizeny()
    {
        var item = await RequestAsync(_nurseUser, _nurse, _surgeryStore);
        var (level, who, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Nadřízený zaměstnance", level);
        Assert.Equal([_chiefSurgery.Id], who);

        await _workflow.DecideAsync(item.Id, _chiefSurgeryUser.Id, true, "ok");
        var done = await ReloadAsync(item.Id);
        Assert.Equal(RequestStatus.Approved, done.Status); // úrovně 2–4 pro vlastní úsek neplatí
        Assert.False(done.AutoApproved);
    }

    [Fact]
    public async Task Radovy_mimo_usek_nadrizeny_odpovedna_osoba_ciloveho_useku_a_ovbkr()
    {
        var item = await RequestAsync(_nurseUser, _nurse, _internalStore);

        var (level1, who1, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Nadřízený zaměstnance", level1);
        Assert.Equal([_chiefSurgery.Id], who1);
        await _workflow.DecideAsync(item.Id, _chiefSurgeryUser.Id, true, null);

        var (level3, who3, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Odpovědná osoba cílového úseku", level3);
        Assert.Equal([_chiefInternal.Id], who3);
        // Primář chirurgie (už rozhodl) nesmí rozhodovat za interní kliniku.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _workflow.DecideAsync(item.Id, _chiefSurgeryUser.Id, true, null));
        await _workflow.DecideAsync(item.Id, _chiefInternalUser.Id, true, null);

        var (level4, who4, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Vedoucí OVBKŘ", level4);
        Assert.Equal([_ovbkr.Id], who4);
        await _workflow.DecideAsync(item.Id, _ovbkrUser.Id, true, null);

        var done = await ReloadAsync(item.Id);
        Assert.Equal(RequestStatus.Approved, done.Status);
        Assert.Equal(3, done.Decisions.Count);
    }

    [Fact]
    public async Task Vedouci_vlastni_usek_schvaluje_prislusny_namestek()
    {
        var item = await RequestAsync(_chiefSurgeryUser, _chiefSurgery, _surgeryStore);
        var (level, who, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Nadřízený zaměstnance", level);
        Assert.Equal([_deputyLpp.Id], who);
        await _workflow.DecideAsync(item.Id, _deputyLppUser.Id, true, null);
        Assert.Equal(RequestStatus.Approved, (await ReloadAsync(item.Id)).Status);
    }

    [Fact]
    public async Task Vedouci_mimo_usek_namestek_a_ovbkr_bez_odpovedne_osoby()
    {
        var item = await RequestAsync(_chiefSurgeryUser, _chiefSurgery, _internalStore);
        var (_, who1, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal([_deputyLpp.Id], who1);
        await _workflow.DecideAsync(item.Id, _deputyLppUser.Id, true, null);

        // Úroveň 3 (odpovědná osoba) platí jen pro řadové — přeskočí se rovnou na OVBKŘ.
        var (level, who2, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Vedoucí OVBKŘ", level);
        Assert.Equal([_ovbkr.Id], who2);
        await _workflow.DecideAsync(item.Id, _ovbkrUser.Id, true, null);
        Assert.Equal(RequestStatus.Approved, (await ReloadAsync(item.Id)).Status);
    }

    [Fact]
    public async Task Namestek_ve_vlastnim_useku_bez_schvalovaciho_stupne()
    {
        // Chirurgie je podřízený úsek úseku LPP → vlastní úsek náměstka.
        var item = await RequestAsync(_deputyLppUser, _deputyLpp, _surgeryStore);
        Assert.Equal(RequestStatus.Approved, item.Status);
        Assert.True(item.AutoApproved);
        Assert.NotNull(item.DecidedAt);
        // Položka je rovnou ve frontě správce karet.
        var queue = await new CardAdminService(_db, null!, new AuditService(_db)).GetQueueAsync();
        Assert.Contains(queue, i => i.Id == item.Id);
    }

    [Fact]
    public async Task Namestek_mimo_usek_schvaluje_reditel()
    {
        var item = await RequestAsync(_deputyPtUser, _deputyPt, _surgeryStore);
        var (level, who, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Ředitel (nadřízený náměstka)", level);
        Assert.Equal([_director.Id], who);
        await _workflow.DecideAsync(item.Id, _directorUser.Id, true, null);
        Assert.Equal(RequestStatus.Approved, (await ReloadAsync(item.Id)).Status); // OVBKŘ pro náměstka neplatí
    }

    [Fact]
    public async Task Reditel_vstupy_obecne_primo_realizace()
    {
        var item = await RequestAsync(_directorUser, _director, _internalStore);
        Assert.Equal(RequestStatus.Approved, item.Status);
        Assert.True(item.AutoApproved);
    }

    [Fact]
    public async Task Bez_povoleni_schvalit_bez_stupne_rozhoduje_administrator()
    {
        var access = await _db.ApprovalMatrices.SingleAsync(m => m.Id == _matrices.Access.Id);
        access.AutoApproveWhenNoLevels = false;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var item = await RequestAsync(_directorUser, _director, _internalStore);
        Assert.Equal(RequestStatus.Pending, item.Status);
        Assert.Null(item.MatrixId);
        Assert.Contains(await _workflow.GetPendingForApproverAsync(_admin.Id, isAdmin: true), i => i.Id == item.Id);
        Assert.DoesNotContain(await _workflow.GetPendingForApproverAsync(_directorUser.Id), i => i.Id == item.Id);
    }

    [Fact]
    public async Task Prostor_bez_vlastnika_se_bere_jako_vlastni_usek_pokud_matice_nerekne_jinak()
    {
        var item = await RequestAsync(_nurseUser, _nurse, _unknownDoor);
        var (_, who, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal([_chiefSurgery.Id], who);
        await _workflow.DecideAsync(item.Id, _chiefSurgeryUser.Id, true, null);
        Assert.Equal(RequestStatus.Approved, (await ReloadAsync(item.Id)).Status);

        // Přísnější režim: neurčený prostor = mimo úsek → úroveň „odpovědná osoba“ bez schvalovatele → administrátor.
        var access = await _db.ApprovalMatrices.SingleAsync(m => m.Id == _matrices.Access.Id);
        access.TreatUnknownUnitAsOutside = true;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Jiný řadový zaměstnanec (na tytéž dveře už sestra žádost má).
        var other = new Employee { FirstName = "Petr", LastName = "Sanitář", Department = "Chirurgická klinika", ManagerId = _chiefSurgery.Id, Rank = EmployeeRank.Staff };
        _db.Employees.Add(other);
        await _db.SaveChangesAsync();
        var strict = await RequestAsync(_admin, other, _unknownDoor);
        await _workflow.DecideAsync(strict.Id, _chiefSurgeryUser.Id, true, null);
        var (level, _, adminFallback) = await WhoDecidesAsync(strict.Id);
        Assert.Equal("Odpovědná osoba cílového úseku", level);
        Assert.True(adminFallback);
    }

    [Fact]
    public async Task Vlastnictvi_se_dedi_z_patra_a_odpovedna_osoba_je_vedouci_useku()
    {
        var floor = await _db.Floors.SingleAsync();
        floor.OrgUnitId = _unitInternal.Id;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var door = new Reader { Name = "Dveře na patře", IsActive = true, Corridor = new Corridor { FloorId = floor.Id, Name = "A100" } };
        _db.Readers.Add(door);
        await _db.SaveChangesAsync();

        var resolver = new ApprovalContextResolver(_db);
        var area = await resolver.ResolveReaderOwnershipAsync(door.Id);
        Assert.Equal(_unitInternal.Id, area.OrgUnitId);
        Assert.Equal(_chiefInternal.Id, area.ResponsibleEmployeeId);
        Assert.Contains("patro", area.Source);

        var context = await resolver.ForReaderAsync(_nurse.Id, door.Id);
        Assert.Equal(UnitRelation.Outside, context.Relation);
        var own = await resolver.ForReaderAsync(_chiefInternal.Id, door.Id);
        Assert.Equal(UnitRelation.Inside, own.Relation);
    }

    [Fact]
    public async Task Vlastni_usek_podle_linie_nadrizenych_bez_useku()
    {
        // Prostor jen s odpovědnou osobou (primář chirurgie), bez úseku; sestra ho má v linii nadřízených.
        var door = new Reader { Name = "Ambulance", IsActive = true, ResponsibleEmployeeId = _chiefSurgery.Id };
        _db.Readers.Add(door);
        await _db.SaveChangesAsync();

        var resolver = new ApprovalContextResolver(_db);
        Assert.Equal(UnitRelation.Inside, (await resolver.ForReaderAsync(_nurse.Id, door.Id)).Relation);
        Assert.Equal(UnitRelation.Inside, (await resolver.ForReaderAsync(_deputyLpp.Id, door.Id)).Relation); // náměstek řídí primáře
        Assert.Equal(UnitRelation.Outside, (await resolver.ForReaderAsync(_chiefInternal.Id, door.Id)).Relation);
    }

    // ---------- Parkování ----------

    private Task<AccessRequest> ParkingAsync(AppUser requester, Employee target)
        => _workflow.CreateParkingRequestAsync(requester.Id, target.Id,
            new ParkingRequestInput(_permitType.Id, false, [_site.Id], ["1AB2345"], null, null, null, "parkování"),
            requesterCanActForOthers: true);

    [Fact]
    public async Task Parkovani_radovy_a_vedouci_schvaluje_nadrizeny()
    {
        var nurse = (await ParkingAsync(_nurseUser, _nurse)).Items.Single();
        var (_, who, _) = await WhoDecidesAsync(nurse.Id);
        Assert.Equal([_chiefSurgery.Id], who);
        await _workflow.DecideAsync(nurse.Id, _chiefSurgeryUser.Id, true, null);
        Assert.Equal(RequestStatus.Approved, (await ReloadAsync(nurse.Id)).Status);

        var chief = (await ParkingAsync(_chiefSurgeryUser, _chiefSurgery)).Items.Single();
        var (_, whoChief, _) = await WhoDecidesAsync(chief.Id);
        Assert.Equal([_deputyLpp.Id], whoChief);
    }

    [Fact]
    public async Task Parkovani_namestek_a_reditel_primo_usek_parkovani()
    {
        var deputy = (await ParkingAsync(_deputyLppUser, _deputyLpp)).Items.Single();
        Assert.Equal(RequestStatus.Approved, deputy.Status);
        Assert.True(deputy.AutoApproved);

        var director = (await ParkingAsync(_directorUser, _director)).Items.Single();
        Assert.Equal(RequestStatus.Approved, director.Status);

        var queue = await new ParkingAdminService(_db, new AuditService(_db)).GetQueueAsync();
        Assert.Contains(queue, i => i.Id == deputy.Id);
        Assert.Contains(queue, i => i.Id == director.Id);
    }

    // ---------- Kamery / EZS ----------

    private Task<AccessRequest> SecurityAsync(AppUser requester, Employee target)
        => _workflow.CreateSecurityRequestAsync(requester.Id, target.Id,
            new SecurityRequestInput(SecurityRequestKind.Camera, "Kamera u skladu", "2 kamery", null, null, null, "chodba", "krádeže"),
            _matrices.Security.Id, requesterCanActForOthers: true);

    [Fact]
    public async Task Kamery_vedouci_useku_schvaluje_ovbkr_a_realizuje_ict()
    {
        var item = (await SecurityAsync(_chiefSurgeryUser, _chiefSurgery)).Items.Single();
        Assert.True(item.IsSecurity);
        var (level, who, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Vedoucí OVBKŘ", level);
        Assert.Equal([_ovbkr.Id], who);
        await _workflow.DecideAsync(item.Id, _ovbkrUser.Id, true, "oprávněné");

        var audit = new AuditService(_db);
        var ict = new SecurityAdminService(_db, audit);
        Assert.Contains(await ict.GetQueueAsync(), i => i.Id == item.Id);
        // Kamery nepatří do fronty správce karet.
        Assert.DoesNotContain(await new CardAdminService(_db, null!, audit).GetQueueAsync(), i => i.Id == item.Id);

        await ict.ImplementAsync(item.Id, _admin.Id, "nainstalováno", "admin");
        var done = await _db.AccessRequestItems.AsNoTracking().Include(i => i.SecurityRequest).SingleAsync(i => i.Id == item.Id);
        Assert.Equal(RequestStatus.ManuallyConfirmed, done.Status);
        Assert.NotNull(done.SecurityRequest!.ImplementedAt);
        Assert.Equal(_admin.Id, done.SecurityRequest.ImplementedByUserId);
        Assert.Empty(await ict.GetQueueAsync());
        Assert.Contains(await ict.GetImplementedAsync(), i => i.Id == item.Id);
    }

    [Fact]
    public async Task Kamery_zada_sam_vedouci_ovbkr_rozhoduje_jeho_nadrizeny()
    {
        var item = (await SecurityAsync(_ovbkrUser, _ovbkr)).Items.Single();
        var resolution = await _workflow.ResolveCurrentLevelAsync(await ReloadAsync(item.Id));
        Assert.NotNull(resolution);
        var approver = Assert.Single(resolution!.Approvers);
        Assert.Equal(_deputyPt.Id, approver.EmployeeId);
        Assert.Equal(ApproverOrigin.EscalatedManager, approver.Origin);

        // Vedoucí OVBKŘ sám rozhodnout nesmí, provozně-technický náměstek ano.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _workflow.DecideAsync(item.Id, _ovbkrUser.Id, true, null));
        await _workflow.DecideAsync(item.Id, _deputyPtUser.Id, true, null);
        Assert.Equal(RequestStatus.Approved, (await ReloadAsync(item.Id)).Status);
    }

    [Fact]
    public async Task Kamery_zamitnuti_ve_fronte_ict_vyzaduje_duvod()
    {
        var item = (await SecurityAsync(_chiefSurgeryUser, _chiefSurgery)).Items.Single();
        await _workflow.DecideAsync(item.Id, _ovbkrUser.Id, true, null);
        var ict = new SecurityAdminService(_db, new AuditService(_db));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ict.RejectAsync(item.Id, _admin.Id, "", "admin"));
        await ict.RejectAsync(item.Id, _admin.Id, "nelze napojit", "admin");
        Assert.Equal(RequestStatus.Rejected, (await ReloadAsync(item.Id)).Status);
    }

    [Fact]
    public async Task Kamery_bez_nastavene_matice_pouzije_vychozi_matici()
    {
        var request = await _workflow.CreateSecurityRequestAsync(_chiefSurgeryUser.Id, _chiefSurgery.Id,
            new SecurityRequestInput(SecurityRequestKind.Alarm, "EZS skladu", "čidla", null, null, null, null, null),
            matrixId: 999_999, requesterCanActForOthers: true);
        var item = request.Items.Single();
        // Neexistující matice → výchozí matice (EKV): vedoucí → nadřízený (náměstek LPP); úrovně s podmínkou úseku se bez prostoru neuplatní.
        var (level, who, _) = await WhoDecidesAsync(item.Id);
        Assert.Equal("Nadřízený zaměstnance", level);
        Assert.Equal([_deputyLpp.Id], who);
    }

    [Fact]
    public async Task Kamery_bez_popisu_a_zduvodneni_se_nepodaji()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _workflow.CreateSecurityRequestAsync(
            _chiefSurgeryUser.Id, _chiefSurgery.Id,
            new SecurityRequestInput(SecurityRequestKind.Camera, "Kamera", null, null, null, null, null, null),
            _matrices.Security.Id, requesterCanActForOthers: true));
    }

    // ---------- Podmínky úrovní samostatně ----------

    [Fact]
    public void Podminka_urovne_kategorie_a_rozsah()
    {
        var level = new ApprovalLevel { Order = 1, AppliesToRanks = RankFlags.Staff | RankFlags.Manager, Scope = LevelScope.OutsideOwnUnit };
        var area = new AreaOwnership(1, "X", null, null, "test");

        Assert.True(new ApprovalContext(EmployeeRank.Staff, area, UnitRelation.Outside).Applies(level));
        Assert.False(new ApprovalContext(EmployeeRank.Staff, area, UnitRelation.Inside).Applies(level));
        Assert.False(new ApprovalContext(EmployeeRank.Executive, area, UnitRelation.Outside).Applies(level));
        Assert.False(ApprovalContext.ForEmployeeOnly(EmployeeRank.Staff).Applies(level)); // bez prostoru
        Assert.False(new ApprovalContext(EmployeeRank.Staff, area, UnitRelation.Unknown).Applies(level));
        Assert.True(new ApprovalContext(EmployeeRank.Staff, area, UnitRelation.Unknown).Applies(level, treatUnknownUnitAsOutside: true));

        var unconditional = new ApprovalLevel { Order = 1 };
        Assert.True(ApprovalContext.ForEmployeeOnly(EmployeeRank.Director).Applies(unconditional));
        Assert.False(unconditional.IsConditional);
        Assert.True(level.IsConditional);
    }
}
