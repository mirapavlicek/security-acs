using System.Net;
using System.Net.Mail;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
using Acs.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acs.Infrastructure.Notifications;

/// <summary>Rozhraní notifikací — workflow ho volá po změnách stavu.</summary>
public interface INotificationService
{
    /// <summary>Položka čeká na schválení — upozorni schvalovatele aktuální úrovně.</summary>
    Task NotifyPendingAsync(int itemId, CancellationToken ct = default);

    /// <summary>Položka byla finálně rozhodnuta / předána — upozorni žadatele.</summary>
    Task NotifyDecidedAsync(int itemId, CancellationToken ct = default);

    /// <summary>Položka čeká příliš dlouho — eskalace na administrátory.</summary>
    Task NotifyEscalationAsync(int itemId, int waitingDays, CancellationToken ct = default);

    /// <summary>
    /// Aktuální úroveň položky nemá po vyhodnocení žádného schvalovatele (typicky
    /// zaměstnanec bez nadřízeného) — administrátoři mají rozhodnout místo něj.
    /// </summary>
    Task NotifyNoApproverAsync(int itemId, string reason, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// E-mailové notifikace přes SMTP (konfigurace v GUI). Selhání odeslání
/// nikdy neshodí workflow — jen se zaloguje.
/// </summary>
public class EmailNotificationService(
    AcsDbContext db,
    SettingsService settings,
    ILogger<EmailNotificationService> logger) : INotificationService
{
    public async Task NotifyPendingAsync(int itemId, CancellationToken ct = default)
    {
        try
        {
            var item = await LoadAsync(itemId, ct);
            if (item?.MatrixId is null || item.Status != RequestStatus.Pending)
                return;

            // Příjemci = konkrétní uživatelé úrovně + nadřízený cílového zaměstnance.
            // Nadřízený bez účtu v ACS dostane e-mail ze záznamu zaměstnance.
            var resolution = await ResolveCurrentLevelAsync(item, ct);
            if (resolution is null)
                return;

            var activeUserIds = resolution.UserIds.ToList();
            var inactive = activeUserIds.Count == 0
                ? []
                : await db.Users.Where(u => activeUserIds.Contains(u.Id) && !u.IsActive).Select(u => u.Id).ToListAsync(ct);
            var approverEmails = resolution.Approvers
                .Where(a => a.UserId is null || !inactive.Contains(a.UserId.Value))
                .Select(a => a.Email)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (approverEmails.Count == 0)
                return;

            var role = resolution.Approvers.Any(a => a.Origin == ApproverOrigin.LineManager)
                ? " (jako nadřízený zaměstnance)"
                : "";
            await SendAsync(approverEmails,
                $"ACS: žádost #{item.RequestId} čeká na vaše schválení",
                $"Zaměstnanec: {item.Request!.TargetEmployee!.FullName}\n"
                + $"Položka: {ItemName(item)}\n"
                + $"Úroveň: {item.CurrentLevelOrder}{role}\n"
                + $"Zdůvodnění: {item.Request.Justification}\n\n"
                + $"Rozhodněte v aplikaci: http://acs.fnmh.network/Requests/Detail/{item.RequestId}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notifikace schvalovatelům (položka {ItemId}) se nepodařila odeslat.", itemId);
        }
    }

    public async Task NotifyDecidedAsync(int itemId, CancellationToken ct = default)
    {
        try
        {
            var item = await LoadAsync(itemId, ct);
            if (item is null)
                return;

            var statusText = item.Status switch
            {
                RequestStatus.Approved when item.IsParking => "schváleno — čeká na vydání správcem parkování",
                RequestStatus.Approved => "schváleno — čeká na zadání správcem karet",
                RequestStatus.Rejected => "zamítnuto",
                RequestStatus.PushedToWinPak => "zapsáno do WIN-PAK — přístup je aktivní",
                RequestStatus.ManuallyConfirmed => "zadáno do WIN-PAK (ručně) — přístup je aktivní",
                RequestStatus.Issued => "vydáno — parkovací povolení je platné",
                RequestStatus.Revoked when item.IsParking => "parkovací povolení odebráno",
                RequestStatus.Revoked => "přístup odebrán",
                RequestStatus.Cancelled => "žádost zrušena",
                _ => item.Status.ToString(),
            };

            var subject = $"ACS: žádost #{item.RequestId} — {statusText}";
            var body = $"Zaměstnanec: {item.Request!.TargetEmployee!.FullName}\n"
                     + $"Položka: {ItemName(item)}\n"
                     + $"Stav: {statusText}\n\n"
                     + $"Detail: http://acs.fnmh.network/Requests/Detail/{item.RequestId}";

            // Informuje se žadatel i samotný zaměstnanec (často jde o dvě různé osoby).
            var recipients = new List<string?>
            {
                item.Request.RequesterUser?.Email,
                item.Request.TargetEmployee.Email,
            };
            var to = recipients
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await SendAsync(to, subject, body, ct);

            // Jakmile je položka schválená, upozorni správce karet (resp. parkování), že jim něco přibylo.
            if (item.Status == RequestStatus.Approved)
                await NotifyQueueAdminsAsync(item, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notifikace o rozhodnutí (položka {ItemId}) se nepodařila odeslat.", itemId);
        }
    }

    /// <summary>Upozornění správcům karet (resp. parkování), že do jejich fronty přibyla schválená položka.</summary>
    private async Task NotifyQueueAdminsAsync(AccessRequestItem item, CancellationToken ct)
    {
        var role = item.IsParking ? AppRole.ParkingAdmin : AppRole.CardAdmin;
        var emails = await db.Users
            .Where(u => u.IsActive && u.Email != null
                        && ((u.Roles & role) == role
                            || (u.Roles & AppRole.Admin) == AppRole.Admin))
            .Select(u => u.Email!)
            .Distinct()
            .ToListAsync(ct);
        if (emails.Count == 0)
            return;

        var action = item.Request!.Kind == RequestKind.Revoke ? "odebrání" : "udělení";
        var what = item.IsParking ? "parkovacího povolení" : "přístupu";
        var queue = item.IsParking
            ? "Fronta správce parkování: http://acs.fnmh.network/Parking/Queue"
            : "Fronta správce karet: http://acs.fnmh.network/CardQueue";
        await SendAsync(emails,
            $"ACS: ve frontě čeká {action} {what} (#{item.RequestId})",
            $"Zaměstnanec: {item.Request.TargetEmployee!.FullName}\n"
            + $"Položka: {ItemName(item)}\n"
            + $"Typ: {action} {what}\n\n"
            + queue, ct);
    }

    /// <summary>Lidský popis předmětu položky — čtečka, skupina, nebo parkovací povolení.</summary>
    public static string ItemName(AccessRequestItem item)
    {
        if (item.ParkingPermit is { } permit)
        {
            var type = permit.PermitType?.Name ?? "parkovací povolení";
            return $"parkovací povolení {type} — {permit.SubjectText()} ({permit.SitesText()})";
        }

        return item.Reader?.Name ?? (item.ReaderGroup is null ? "—" : $"skupina {item.ReaderGroup.Name}");
    }

    public async Task NotifyEscalationAsync(int itemId, int waitingDays, CancellationToken ct = default)
    {
        try
        {
            var item = await LoadAsync(itemId, ct);
            if (item is null)
                return;

            var adminEmails = await db.Users
                .Where(u => u.IsActive && u.Email != null && (u.Roles & AppRole.Admin) == AppRole.Admin)
                .Select(u => u.Email!)
                .ToListAsync(ct);
            if (adminEmails.Count == 0)
                return;

            await SendAsync(adminEmails,
                $"ACS: eskalace — žádost #{item.RequestId} čeká {waitingDays} dní",
                $"Zaměstnanec: {item.Request!.TargetEmployee!.FullName}\n"
                + $"Položka: {ItemName(item)}\n"
                + $"Čeká na schválení: {waitingDays} dní (úroveň {item.CurrentLevelOrder})\n\n"
                + $"Detail: http://acs.fnmh.network/Requests/Detail/{item.RequestId}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Eskalační notifikace (položka {ItemId}) se nepodařila odeslat.", itemId);
        }
    }

    public async Task NotifyNoApproverAsync(int itemId, string reason, CancellationToken ct = default)
    {
        try
        {
            var item = await LoadAsync(itemId, ct);
            if (item is null || item.Status != RequestStatus.Pending)
                return;

            var adminEmails = await db.Users
                .Where(u => u.IsActive && u.Email != null && (u.Roles & AppRole.Admin) == AppRole.Admin)
                .Select(u => u.Email!)
                .ToListAsync(ct);
            if (adminEmails.Count == 0)
                return;

            await SendAsync(adminEmails,
                $"ACS: žádost #{item.RequestId} nemá schvalovatele — rozhodne správce",
                $"Zaměstnanec: {item.Request!.TargetEmployee!.FullName}\n"
                + $"Položka: {ItemName(item)}\n"
                + $"Úroveň {item.CurrentLevelOrder}: {reason}\n\n"
                + "Úroveň matice po vyhodnocení nemá žádného schvalovatele (typicky zaměstnanec bez nadřízeného).\n"
                + "Rozhodněte jako administrátor, nebo doplňte zaměstnanci nadřízeného (Katalog → Zaměstnanci).\n\n"
                + $"Detail: http://acs.fnmh.network/Requests/Detail/{item.RequestId}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notifikace o chybějícím schvalovateli (položka {ItemId}) se nepodařila odeslat.", itemId);
        }
    }

    private async Task<LevelResolution?> ResolveCurrentLevelAsync(AccessRequestItem item, CancellationToken ct)
    {
        var level = await db.ApprovalLevels.AsNoTracking()
            .Include(l => l.Approvers)
            .FirstOrDefaultAsync(l => l.MatrixId == item.MatrixId && l.Order == item.CurrentLevelOrder, ct);
        return level is null ? null : await new ApproverResolver(db).ResolveAsync(level, item, ct);
    }

    private Task<AccessRequestItem?> LoadAsync(int itemId, CancellationToken ct)
        => db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Include(i => i.Reader)
            .Include(i => i.ReaderGroup)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.PermitType)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Plates)
            .Include(i => i.ParkingPermit!).ThenInclude(p => p.Sites).ThenInclude(s => s.Site)
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);

    private async Task SendAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct)
    {
        var host = await settings.GetAsync(SettingKeys.SmtpHost, ct);
        if (string.IsNullOrWhiteSpace(host))
            return; // SMTP nenakonfigurováno — notifikace se tiše vynechají.

        var port = await settings.GetIntAsync(SettingKeys.SmtpPort, 25, ct);
        var from = await settings.GetAsync(SettingKeys.SmtpFrom, ct) ?? "acs@fnmh.network";
        var user = await settings.GetAsync(SettingKeys.SmtpUser, ct);
        var password = await settings.GetAsync(SettingKeys.SmtpPassword, ct);
        var useTls = await settings.GetBoolAsync(SettingKeys.SmtpUseTls, false, ct);

        using var client = new SmtpClient(host, port) { EnableSsl = useTls };
        if (!string.IsNullOrEmpty(user))
            client.Credentials = new NetworkCredential(user, password);

        using var message = new MailMessage { From = new MailAddress(from), Subject = subject, Body = body };
        foreach (var address in to)
            message.To.Add(address);

        await client.SendMailAsync(message, ct);
        logger.LogInformation("Notifikace odeslána: {Subject} → {Count} příjemců.", subject, to.Count);
    }
}
