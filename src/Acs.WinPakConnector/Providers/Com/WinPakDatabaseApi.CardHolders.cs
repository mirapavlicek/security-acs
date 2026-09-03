using Acs.WinPakConnector.Models;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Držitelé karet včetně vyhledávání, poznámkových polí, fotek a podpisů.</summary>
public sealed partial class WinPakDatabaseApi
{
    public IReadOnlyList<CardHolderDto> GetCardHolders()
        => CallList("GetCardHoldersByAccountName", MapCardHolder,
            AccountName, SubAccountName, null);

    public CardHolderDto? GetCardHolder(string cardHolderId)
    {
        var result = Call("GetCardHolderByCardHolderID", ComValue.ToLong(cardHolderId), null);
        var raw = ComValue.AsEnumerable(result[1]).FirstOrDefault();
        return raw is null ? null : MapCardHolder(_com.Wrap(raw));
    }

    private CardHolderDto MapCardHolder(IComDispatch holder)
    {
        var id = ComValue.ToStringOrEmpty(holder.GetProperty("CardHolderID"));
        var cards = GetCardsByCardHolder(id);

        return new CardHolderDto(
            Id: id,
            FirstName: ComValue.ToStringOrEmpty(holder.GetProperty("FirstName")),
            LastName: ComValue.ToStringOrEmpty(holder.GetProperty("LastName")),
            Note: ReadNote(holder),
            Cards: cards,
            // Držitel sám oprávnění nemá — ukazujeme sjednocení úrovní jeho karet.
            AccessLevelIds: cards.SelectMany(c => c.AccessLevelIds).Distinct().ToList());
    }

    public string AddCardHolder(UpsertCardHolderRequest request)
    {
        EnsureSession();
        var holder = _com.Create(_options.CardHolderProgId);
        ApplyCardHolder(holder, request);

        var args = new object?[] { holder.Target, 0 };
        App.Invoke("AddCardHolder", args);
        WinPakStatus.EnsureCardHolderSucceeded("Založení držitele karty", ComValue.ToInt(args[1]));

        // Příručka: po AddCardHolder nese objekt přidělené id v CardHolderID.
        return ComValue.ToStringOrEmpty(holder.GetProperty("CardHolderID"));
    }

    public void EditCardHolder(string cardHolderId, UpsertCardHolderRequest request)
    {
        EnsureSession();
        var holder = _com.Create(_options.CardHolderProgId);
        ApplyCardHolder(holder, request);
        holder.SetProperty("CardHolderID", ComValue.ToLong(cardHolderId));

        var args = new object?[] { ComValue.ToLong(cardHolderId), holder.Target, 0 };
        App.Invoke("EditCardHolder", args);
        WinPakStatus.EnsureCardHolderSucceeded("Úprava držitele karty", ComValue.ToInt(args[2]));
    }

    /// <summary>Smaže držitele; volitelně i jeho karty a obrázky.</summary>
    public void DeleteCardHolder(string cardHolderId, DeleteCardHolderOptions options)
    {
        var result = Call("DeleteCardHolder",
            ComValue.ToLong(cardHolderId),
            options.DeleteCards ? 1 : 0,
            options.DeleteImages ? 1 : 0,
            0);
        WinPakStatus.EnsureCardHolderSucceeded("Smazání držitele karty", ComValue.ToInt(result[3]));
    }

    private void ApplyCardHolder(IComDispatch holder, UpsertCardHolderRequest request)
    {
        holder.SetProperty("FirstName", request.FirstName);
        holder.SetProperty("LastName", request.LastName);
        holder.SetProperty("AccountName", AccountName);
        if (!string.IsNullOrWhiteSpace(SubAccountName))
            holder.SetProperty("SubAccountName", SubAccountName);
        if (request.Note is not null)
            WriteNote(holder, request.Note);
    }

    /// <summary>
    /// Poznámka držitele. Skutečný WIN-PAK má <c>NoteField</c> indexované — držitel má
    /// poznámkových polí několik a bez indexu volání odmítne („Number of parameters
    /// specified does not match“). Bere se první pole; když není ani tak, poznámka
    /// je jen prázdná a výpis držitelů kvůli ní nepadá.
    /// </summary>
    private static string? ReadNote(IComDispatch holder)
    {
        try
        {
            return ComValue.ToStringOrNull(holder.GetProperty("NoteField"));
        }
        catch (ComCallException)
        {
            try
            {
                return ComValue.ToStringOrNull(holder.GetProperty("NoteField", [1]));
            }
            catch (ComCallException)
            {
                return null;
            }
        }
    }

    private static void WriteNote(IComDispatch holder, string note)
    {
        try
        {
            holder.SetProperty("NoteField", note);
        }
        catch (ComCallException)
        {
            holder.SetProperty("NoteField", [1], note);
        }
    }

    /// <summary>Pole, podle kterých umí WIN-PAK v tomto účtu vyhledávat držitele.</summary>
    public IReadOnlyList<CardHolderSearchFieldDto> GetCardHolderSearchFields()
        => CallList("GetCardHolderSearchFieldsByAccountName",
            field => new CardHolderSearchFieldDto(
                ComValue.ToStringOrEmpty(field.GetProperty("NoteFieldName")),
                ComValue.ToInt(field.GetProperty("FieldIndex"))),
            AccountName, SubAccountName, null);

    /// <summary>Vyhledání držitelů přímo v databázi WIN-PAK (<c>GetCardHoldersOnSearch</c>).</summary>
    public IReadOnlyList<CardHolderDto> SearchCardHolders(CardHolderSearchRequest request)
    {
        var fields = request.Criteria.Select(object? (c) => c.Field).ToArray();
        var values = request.Criteria.Select(object? (c) => c.Value).ToArray();
        var comparisons = request.Criteria.Select(object? (c) => c.ComparisonType).ToArray();

        return CallList("GetCardHoldersOnSearch", MapCardHolder,
            AccountName, SubAccountName, fields, values, comparisons, null);
    }

    /// <summary>Šablony poznámkových polí účtu (<c>GetNoteFieldTemplateDetailsByAccount</c>).</summary>
    public IReadOnlyList<NoteFieldTemplateDto> GetNoteFieldTemplates()
        => CallList("GetNoteFieldTemplateDetailsByAccount",
            template => new NoteFieldTemplateDto(
                ComValue.ToStringOrEmpty(template.GetProperty("NoteFieldName")),
                ComValue.ToInt(template.GetProperty("FieldIndex")),
                ComValue.ToStringOrNull(template.GetProperty("FieldDefinition"))),
            AccountName, SubAccountName, null);

    // ---------- Fotky a podpisy ----------

    public CardHolderImageDto GetPhoto(string cardHolderId, int index)
        => GetImage(cardHolderId, index, "GetPhotoSize", "GetPhoto");

    public CardHolderImageDto GetSignature(string cardHolderId, int index)
        => GetImage(cardHolderId, index, "GetSigSize", "GetSig");

    private CardHolderImageDto GetImage(string cardHolderId, int index, string sizeMethod, string dataMethod)
    {
        var id = ComValue.ToLong(cardHolderId);
        var size = ComValue.ToLong(Call(sizeMethod, id, index, 0)[2]);
        var data = Call(dataMethod, id, index, null)[2];

        return new CardHolderImageDto(cardHolderId, index, size, ToBase64(data));
    }

    /// <summary>Obrázky chodí jako VARIANT s polem bytů; pro REST je vracíme v base64.</summary>
    private static string? ToBase64(object? value)
        => value switch
        {
            null => null,
            byte[] bytes => bytes.Length == 0 ? null : Convert.ToBase64String(bytes),
            string text => text.Length == 0 ? null : text,
            System.Collections.IEnumerable sequence =>
                Convert.ToBase64String(sequence.Cast<object?>().Select(b => (byte)ComValue.ToInt(b)).ToArray()),
            _ => null,
        };

    public void ImportPhoto(string cardHolderId, int index, string contentBase64)
        => Call("ImportPhoto", ComValue.ToLong(cardHolderId), index, Convert.FromBase64String(contentBase64));

    public void ImportSignature(string cardHolderId, int index, string contentBase64)
        => Call("ImportSig", ComValue.ToLong(cardHolderId), index, Convert.FromBase64String(contentBase64));

    public void DeletePhoto(string cardHolderId, int index)
    {
        var result = Call("DeletePhoto", ComValue.ToLong(cardHolderId), index, 0);
        WinPakStatus.EnsureCardHolderSucceeded("Smazání fotky", ComValue.ToInt(result[2]));
    }

    public void DeleteSignature(string cardHolderId, int index)
    {
        var result = Call("DeleteSignature", ComValue.ToLong(cardHolderId), index, 0);
        WinPakStatus.EnsureCardHolderSucceeded("Smazání podpisu", ComValue.ToInt(result[2]));
    }

    /// <summary>
    /// Kratší varianta bez stavového kódu (<c>DeleteSig</c>). Příručka ji uvádí
    /// vedle <c>DeleteSignature</c>; některé instalace mají jen jednu z nich.
    /// </summary>
    public void DeleteSignatureShort(string cardHolderId, int index)
        => Call("DeleteSig", ComValue.ToLong(cardHolderId), index);
}
