using Acs.Domain.Entities;

namespace Acs.Web.Pages.Shared;

/// <summary>
/// Jednotné české popisky stavů a předmětů položek žádosti (přístupy i parkování).
/// Bez názvu přístupového systému (WIN-PAK) — texty vidí i běžní uživatelé.
/// </summary>
public static class StatusText
{
    public static string Label(RequestStatus status, bool isParking = false, bool isSecurity = false) => status switch
    {
        RequestStatus.Draft => "rozpracováno",
        RequestStatus.Pending => "čeká na schválení",
        RequestStatus.Approved when isParking => "schváleno — u správce parkování",
        RequestStatus.Approved when isSecurity => "schváleno — čeká na realizaci ICT",
        RequestStatus.Approved => "schváleno — u správce karet",
        RequestStatus.PushedToWinPak => "přístup aktivní",
        RequestStatus.ManuallyConfirmed when isSecurity => "realizováno",
        RequestStatus.ManuallyConfirmed => "přístup aktivní (zadáno ručně)",
        RequestStatus.Issued => "vydáno",
        RequestStatus.Rejected => "zamítnuto",
        RequestStatus.Revoked => "odebráno",
        RequestStatus.Cancelled => "zrušeno",
        _ => status.ToString(),
    };

    public static string Label(AccessRequestItem item) => Label(item.Status, item.IsParking, item.IsSecurity);

    /// <summary>CSS třída pro barevný „pill“ stavu.</summary>
    public static string PillClass(RequestStatus status) => status switch
    {
        RequestStatus.Pending or RequestStatus.Approved => "status-pill wait",
        RequestStatus.PushedToWinPak or RequestStatus.ManuallyConfirmed or RequestStatus.Issued => "status-pill ok",
        RequestStatus.Rejected or RequestStatus.Revoked or RequestStatus.Cancelled => "status-pill bad",
        _ => "status-pill",
    };

    /// <summary>Nadpis položky: čtečka, skupina, nebo parkovací povolení (vyžaduje načtené navigace).</summary>
    public static string ItemTitle(AccessRequestItem item)
    {
        if (item.SecurityRequest is { } security)
            return $"Kamery / EZS: {security.Title} ({security.KindLabel})";

        if (item.ParkingPermit is { } permit)
        {
            var type = permit.PermitType?.Name ?? "parkovací povolení";
            return $"Parkovací povolení: {type} — {permit.SubjectText()}";
        }

        return item.Reader?.Name ?? $"Skupina: {item.ReaderGroup?.Name}";
    }
}
