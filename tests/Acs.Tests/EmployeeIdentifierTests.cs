using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Acs.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Acs.Tests;

/// <summary>Více identifikátorů na osobu (karty, SPZ) a jejich platnost.</summary>
public sealed class EmployeeIdentifierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AcsDbContext _db;
    private readonly Employee _employee;

    public EmployeeIdentifierTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _employee = new Employee { FirstName = "Jan", LastName = "Novák", AdAccount = "jnovak" };
        _db.Employees.Add(_employee);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Employee_CanHaveMultipleCardsAndPlates()
    {
        _db.EmployeeIdentifiers.AddRange(
            new EmployeeIdentifier { EmployeeId = _employee.Id, Type = IdentifierType.Card, Value = "100234" },
            new EmployeeIdentifier { EmployeeId = _employee.Id, Type = IdentifierType.Card, Value = "100235" },
            new EmployeeIdentifier { EmployeeId = _employee.Id, Type = IdentifierType.Card, Value = "100236" },
            new EmployeeIdentifier { EmployeeId = _employee.Id, Type = IdentifierType.LicensePlate, Value = "1AB2345" },
            new EmployeeIdentifier { EmployeeId = _employee.Id, Type = IdentifierType.LicensePlate, Value = "5CD6789" });
        await _db.SaveChangesAsync();

        var loaded = await _db.Employees.Include(e => e.Identifiers).SingleAsync();
        Assert.Equal(3, loaded.Identifiers.Count(i => i.Type == IdentifierType.Card));
        Assert.Equal(2, loaded.Identifiers.Count(i => i.Type == IdentifierType.LicensePlate));
    }

    [Theory]
    [InlineData("1AB 2345", "1AB2345")]
    [InlineData("100-234", "100234")]
    [InlineData(" abc123 ", "ABC123")]
    public void Normalize_RemovesSpacesDashesAndUppercases(string input, string expected)
        => Assert.Equal(expected, EmployeeIdentifier.Normalize(input));

    [Fact]
    public void IsValidAt_RespectsActiveFlagAndValidity()
    {
        var now = new DateTime(2026, 6, 1);
        Assert.True(new EmployeeIdentifier { Value = "A", IsActive = true }.IsValidAt(now));
        Assert.False(new EmployeeIdentifier { Value = "A", IsActive = false }.IsValidAt(now));
        Assert.False(new EmployeeIdentifier
        {
            Value = "A", IsActive = true, ValidTo = now.AddDays(-1),
        }.IsValidAt(now));
        Assert.False(new EmployeeIdentifier
        {
            Value = "A", IsActive = true, ValidFrom = now.AddDays(1),
        }.IsValidAt(now));
        Assert.True(new EmployeeIdentifier
        {
            Value = "A", IsActive = true, ValidFrom = now.AddDays(-1), ValidTo = now.AddDays(1),
        }.IsValidAt(now));
    }

    [Theory]
    [InlineData("Card", IdentifierType.Card)]
    [InlineData("karta", IdentifierType.Card)]
    [InlineData("SPZ", IdentifierType.LicensePlate)]
    [InlineData("licenseplate", IdentifierType.LicensePlate)]
    [InlineData("RZ", IdentifierType.LicensePlate)]
    [InlineData("pin", IdentifierType.Pin)]
    [InlineData("čip", IdentifierType.Tag)]
    [InlineData("nesmysl", IdentifierType.Other)]
    [InlineData(null, IdentifierType.Card)]
    public void ParseType_UnderstandsCzechAndEnglishNames(string? raw, IdentifierType expected)
        => Assert.Equal(expected, CardSyncService.ParseType(raw));

    [Fact]
    public async Task Search_FindsEmployeeByIdentifierValue()
    {
        _db.EmployeeIdentifiers.Add(new EmployeeIdentifier
        {
            EmployeeId = _employee.Id, Type = IdentifierType.LicensePlate, Value = "1AB2345",
        });
        await _db.SaveChangesAsync();

        var term = EmployeeIdentifier.Normalize("1ab 2345");
        var found = await _db.Employees
            .Where(e => e.Identifiers.Any(i => i.Value.Contains(term)))
            .ToListAsync();

        Assert.Single(found);
        Assert.Equal(_employee.Id, found[0].Id);
    }

    [Fact]
    public async Task DeletingEmployee_RemovesIdentifiers()
    {
        _db.EmployeeIdentifiers.Add(new EmployeeIdentifier
        {
            EmployeeId = _employee.Id, Type = IdentifierType.Card, Value = "100234",
        });
        await _db.SaveChangesAsync();

        _db.Employees.Remove(_employee);
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.EmployeeIdentifiers.ToListAsync());
    }
}
