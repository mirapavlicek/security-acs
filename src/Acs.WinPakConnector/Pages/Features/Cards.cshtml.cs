using Acs.WinPakConnector.Models;
using Acs.WinPakConnector.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Acs.WinPakConnector.Pages.Features;

/// <summary>Karty a jejich držitelé — to, co ACS z této oblasti při schvalování nedělá.</summary>
public class CardsModel(WinPakProviderCache providers) : FeaturePageModel(providers)
{
    public IReadOnlyList<CardHolderDto> CardHolders { get; private set; } = [];
    public IReadOnlyList<AccessLevelDto> AccessLevels { get; private set; } = [];
    public IReadOnlyList<CardHolderSearchFieldDto> SearchFields { get; private set; } = [];
    public IReadOnlyList<NoteFieldTemplateDto> NoteFields { get; private set; } = [];
    public IReadOnlyList<CardDto> CardsWithoutHolder { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? CardNumber { get; set; }
    [BindProperty(SupportsGet = true)] public string? HolderId { get; set; }

    /// <summary>Seznam držitelů a karet bez držitele se načítá jen na vyžádání — jsou to nejdelší volání do WIN-PAKu.</summary>
    [BindProperty(SupportsGet = true)] public bool List { get; set; }

    /// <summary>Fotka držitele jen na vyžádání — obrázkové API ostrého WIN-PAKu je nejméně spolehlivá část a detail držitele na něm nemá záviset.</summary>
    [BindProperty(SupportsGet = true)] public bool ShowPhoto { get; set; }

    public string? PhotoError { get; private set; }

    public CardDto? Card { get; private set; }
    public CardHolderDto? Holder { get; private set; }
    public CardHolderImageDto? Photo { get; private set; }
    public string? DetailError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Detail hledané karty nebo držitele jde první — kvůli němu se sem chodí
        // a je to jedno krátké volání; nemá čekat za seznamy.
        await LoadDetailAsync(ct);

        await LoadAsync(async () => AccessLevels = await Provider.GetAccessLevelsAsync(ct));
        // Každá pomocná položka zvlášť — jedna odmítnutá (třeba pole vyhledávání)
        // nemá shodit karty ani držitele, kvůli kterým se sem chodí.
        await LoadAsync(async () => SearchFields = await RequireCatalog().GetCardHolderSearchFieldsAsync(ct));
        await LoadAsync(async () => NoteFields = await RequireCatalog().GetNoteFieldTemplatesAsync(ct));

        if (!List)
            return;

        // Všichni držitelé se všemi kartami a karty bez držitele: na ostrých datech
        // desítky sekund, proto jen na kliknutí a ne při každém hledání karty.
        await LoadAsync(async () => CardHolders = await Provider.SearchCardHoldersAsync(null, ct));
        await LoadAsync(async () => CardsWithoutHolder = await RequireCatalog().GetCardsAsync(onlyWithoutHolder: true, ct));
    }

    private async Task LoadDetailAsync(CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(CardNumber))
                Card = await Provider.GetCardAsync(CardNumber, ct);

            if (!string.IsNullOrWhiteSpace(HolderId))
                Holder = await Provider.GetCardHolderAsync(HolderId, ct);
        }
        catch (Exception ex)
        {
            DetailError = ex.Message;
        }

        if (Holder is null || !ShowPhoto || Catalog is not { } catalog)
            return;

        try
        {
            Photo = await catalog.GetCardHolderImageAsync(Holder.Id, 0, signature: false, ct);
        }
        catch (Exception ex)
        {
            PhotoError = ex.Message;
        }
    }

    // ---------- Karty ----------

    public IActionResult OnPostFindCard(string cardNumber) => RedirectToPage(new { cardNumber });

    public Task<IActionResult> OnPostSaveCardAsync(
        string cardNumber, string? cardHolderId, CardStatus status, int issue,
        DateTime? activationDate, DateTime? expirationDate, string? pin, string[]? accessLevelIds,
        CancellationToken ct)
        => ActAsync($"Uložení karty {cardNumber}", () => Provider.UpsertCardAsync(cardNumber,
            new UpsertCardRequest(cardHolderId, status, issue, activationDate, expirationDate, pin, accessLevelIds), ct));

    public Task<IActionResult> OnPostSaveCardNetAxsAsync(
        string cardNumber, string? cardHolderId, CardStatus status,
        bool temporaryCard, int cardType, int usageLimit, bool limitedCard, long trigger,
        CancellationToken ct)
        => ActAsync($"Uložení karty {cardNumber} s NetAXS volbami",
            () => RequireCatalog().UpsertCardExAsync(cardNumber, new UpsertCardExRequest(
                new UpsertCardRequest(cardHolderId, status),
                new NetAxsCardOptions(temporaryCard, cardType, usageLimit, limitedCard, trigger)), ct));

    public Task<IActionResult> OnPostDeleteCardAsync(string cardNumber, CancellationToken ct)
        => ActAsync($"Zrušení karty {cardNumber}", () => Provider.DeleteCardAsync(cardNumber, ct));

    public Task<IActionResult> OnPostBulkAddAsync(
        string startNumber, string stopNumber, CardStatus status,
        DateTime? activationDate, DateTime? expirationDate, string[]? accessLevelIds, CancellationToken ct)
        => ActAsync($"Hromadné založení karet {startNumber}–{stopNumber}",
            () => RequireCatalog().BulkAddCardsAsync(
                new BulkAddCardsRequest(startNumber, stopNumber, status, activationDate, expirationDate, accessLevelIds), ct));

    public Task<IActionResult> OnPostBulkDeleteAsync(string startNumber, string stopNumber, CancellationToken ct)
        => ActAsync($"Hromadné zrušení karet {startNumber}–{stopNumber}",
            () => RequireCatalog().BulkDeleteCardsAsync(new BulkDeleteCardsRequest(startNumber, stopNumber), ct));

    // ---------- Držitelé ----------

    public IActionResult OnPostOpenHolder(string holderId) => RedirectToPage(new { holderId });

    public Task<IActionResult> OnPostAddHolderAsync(string firstName, string lastName, string? note, CancellationToken ct)
        => ActAsync("Založení držitele",
            async () => $"id {await Provider.AddCardHolderAsync(new UpsertCardHolderRequest(firstName, lastName, note), ct)}");

    public Task<IActionResult> OnPostEditHolderAsync(string holderId, string firstName, string lastName, string? note, CancellationToken ct)
        => ActAsync($"Úprava držitele {holderId}",
            () => Provider.EditCardHolderAsync(holderId, new UpsertCardHolderRequest(firstName, lastName, note), ct));

    public Task<IActionResult> OnPostDeleteHolderAsync(string holderId, bool deleteCards, bool deleteImages, CancellationToken ct)
        => ActAsync($"Smazání držitele {holderId}",
            () => RequireCatalog().DeleteCardHolderAsync(holderId, new DeleteCardHolderOptions(deleteCards, deleteImages), ct));

    public Task<IActionResult> OnPostSearchHoldersAsync(string field, string value, int comparisonType, CancellationToken ct)
        => ActAsync($"Vyhledání držitelů podle {field}", async () =>
        {
            var found = await RequireCatalog().SearchCardHoldersAsync(
                new CardHolderSearchRequest([new CardHolderSearchCriterion(field, value, comparisonType)]), ct);
            return found.Count == 0
                ? "nikdo nenalezen"
                : string.Join(", ", found.Select(h => $"{h.LastName} {h.FirstName} ({h.Id})"));
        });

    // ---------- Obrázky ----------

    public async Task<IActionResult> OnPostUploadImageAsync(
        string holderId, int index, bool signature, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            ErrorMessage = "Vyberte soubor s obrázkem.";
            return RedirectToPage(new { holderId });
        }

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, ct);
        var content = Convert.ToBase64String(stream.ToArray());

        return await ActAsync($"Nahrání {(signature ? "podpisu" : "fotky")} držitele {holderId}",
            () => RequireCatalog().ImportCardHolderImageAsync(holderId, index, signature, content, ct));
    }

    public Task<IActionResult> OnPostDeleteImageAsync(string holderId, int index, bool signature, CancellationToken ct)
        => ActAsync($"Smazání {(signature ? "podpisu" : "fotky")} držitele {holderId}",
            () => RequireCatalog().DeleteCardHolderImageAsync(holderId, index, signature, shortVariant: false, ct));
}
