using System.Net;
using System.Net.Mail;
using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Settings;
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

            var approverEmails = await db.Approvers
                .Where(a => a.Level!.MatrixId == item.MatrixId
                            && a.Level.Order == item.CurrentLevelOrder
                            && a.User != null && a.User.Email != null && a.User.IsActive)
                .Select(a => a.User!.Email!)
                .Distinct()
                .ToListAsync(ct);
            if (approverEmails.Count == 0)
                return;

            await SendAsync(approverEmails,
                $"ACS: žádost #{item.RequestId} čeká na vaše schválení",
                $"Zaměstnanec: {item.Request!.TargetEmployee!.FullName}\n"
                + $"Položka: {ItemName(item)}\n"
                + $"Úroveň: {item.CurrentLevelOrder}\n"
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
                RequestStatus.Approved => "schváleno — čeká na zadání správcem karet",
                RequestStatus.Rejected => "zamítnuto",
                RequestStatus.PushedToWinPak => "zapsáno do WIN-PAK — přístup je aktivní",
                RequestStatus.ManuallyConfirmed => "zadáno do WIN-PAK (ručně) — přístup je aktivní",
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

            // Jakmile je položka schválená, upozorni správce karet, že jim něco přibylo.
            if (item.Status == RequestStatus.Approved)
                await NotifyCardAdminsAsync(item, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notifikace o rozhodnutí (položka {ItemId}) se nepodařila odeslat.", itemId);
        }
    }

    /// <summary>Upozornění správcům karet, že do fronty přibyla schválená položka.</summary>
    private async Task NotifyCardAdminsAsync(AccessRequestItem item, CancellationToken ct)
    {
        var emails = await db.Users
            .Where(u => u.IsActive && u.Email != null
                        && ((u.Roles & AppRole.CardAdmin) == AppRole.CardAdmin
                            || (u.Roles & AppRole.Admin) == AppRole.Admin))
            .Select(u => u.Email!)
            .Distinct()
            .ToListAsync(ct);
        if (emails.Count == 0)
            return;

        var action = item.Request!.Kind == RequestKind.Revoke ? "odebrání" : "udělení";
        await SendAsync(emails,
            $"ACS: ve frontě čeká {action} přístupu (#{item.RequestId})",
            $"Zaměstnanec: {item.Request.TargetEmployee!.FullName}\n"
            + $"Položka: {ItemName(item)}\n"
            + $"Typ: {action} přístupu\n\n"
            + "Fronta správce karet: http://acs.fnmh.network/CardQueue", ct);
    }

    private static string ItemName(AccessRequestItem item)
        => item.Reader?.Name ?? (item.ReaderGroup is null ? "—" : $"skupina {item.ReaderGroup.Name}");

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

    private Task<AccessRequestItem?> LoadAsync(int itemId, CancellationToken ct)
        => db.AccessRequestItems
            .Include(i => i.Request!).ThenInclude(r => r.TargetEmployee)
            .Include(i => i.Request!).ThenInclude(r => r.RequesterUser)
            .Include(i => i.Reader)
            .Include(i => i.ReaderGroup)
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
