using System.Text.Json;
using RiotAccounts.Core;
using Xunit;

public sealed class AccountOrderTests
{
    private static RiotAccount Account(string label) => new(Guid.NewGuid(), label,
        [new("lol", "Player " + label, "JP1", "JP1", "puuid-" + label)]);

    private static RiotAccount[] Seed(Store store)
    {
        var accounts = new[] { Account("Alpha"), Account("Bravo"), Account("Charlie"), Account("Delta") };
        foreach (var account in accounts.Reverse()) store.Save(account);
        return accounts;
    }

    private static Guid[] Ids(Store store) => store.Accounts().Select(a => a.Id).ToArray();
    private static Store Reopen(StoreFixture fixture) => new(fixture.File, fixture.Protector);

    [Fact]
    public void UnconfiguredOrderRemainsAlphabeticalAfterAddAndRename()
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        Assert.Equal(accounts.Select(a => a.Id), Ids(fixture.Store));
        fixture.Store.Save(accounts[3] with { Label = "Aardvark" });
        Assert.Equal(new[] { accounts[3].Id, accounts[0].Id, accounts[1].Id, accounts[2].Id }, Ids(Reopen(fixture)));
        Assert.Null(fixture.Store.Read<List<Guid>>("setting", "accountOrder"));
    }

    [Theory]
    [InlineData(2, 0, false, "2013")]
    [InlineData(3, 1, false, "0312")]
    [InlineData(0, 3, true, "1230")]
    [InlineData(0, 1, true, "1023")]
    public void MoveToFirstMiddleOrEndPersists(int source, int target, bool after, string positions)
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        fixture.Store.MoveAccount(accounts[source].Id, accounts[target].Id, after);
        var expected = positions.Select(p => accounts[p - '0'].Id).ToArray();
        Assert.Equal(expected, Ids(fixture.Store));
        Assert.Equal(expected, Ids(Reopen(fixture)));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, true)]
    public void NoOpDoesNotPinAnAlphabeticalList(int source, int target, bool after)
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        fixture.Store.MoveAccount(accounts[source].Id, accounts[target].Id, after);
        Assert.Null(fixture.Store.Read<List<Guid>>("setting", "accountOrder"));
        fixture.Store.Save(accounts[3] with { Label = "Aardvark" });
        Assert.Equal(accounts[3].Id, Ids(Reopen(fixture))[0]);
    }

    [Fact]
    public void SavingACompleteOrderPersists()
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        var expected = new[] { accounts[2].Id, accounts[0].Id, accounts[3].Id, accounts[1].Id };
        fixture.Store.SaveAccountOrder(expected);
        Assert.Equal(expected, Ids(Reopen(fixture)));
    }

    [Fact]
    public void AddsAppendAndDeletionKeepsTheOtherAccountsInPlace()
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        fixture.Store.MoveAccount(accounts[2].Id, accounts[0].Id, false);
        var newest = Account("Aardvark");
        fixture.Store.Save(newest);
        Assert.Equal(new[] { accounts[2].Id, accounts[0].Id, accounts[1].Id, accounts[3].Id, newest.Id }, Ids(Reopen(fixture)));
        fixture.Store.Delete(accounts[0].Id);
        var expected = new[] { accounts[2].Id, accounts[1].Id, accounts[3].Id, newest.Id };
        Assert.Equal(expected, Ids(Reopen(fixture)));
        Assert.Equal(expected, fixture.Store.Read<List<Guid>>("setting", "accountOrder"));
    }

    [Fact]
    public void RenameCredentialsAndProfileUpdatesPreserveManualOrder()
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        fixture.Store.MoveAccount(accounts[2].Id, accounts[0].Id, false);
        var expected = Ids(fixture.Store);
        fixture.Store.Save(accounts[2] with { Label = "Zulu" }, new("test-user", "test-password"));
        fixture.Store.Save(accounts[0] with { Profiles = [accounts[0].Lol with { Puuid = "resolved-puuid" }] });
        Assert.Equal(expected, Ids(Reopen(fixture)));
        Assert.Equal("Zulu", fixture.Store.Accounts()[0].Label);
        Assert.Equal(new Credentials("test-user", "test-password"), fixture.Store.Credentials(accounts[2].Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidCompleteOrdersDoNotMutateStorage(bool hasManualOrder)
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        if (hasManualOrder) fixture.Store.MoveAccount(accounts[2].Id, accounts[0].Id, false);
        var ids = Ids(fixture.Store);
        var before = Snapshot(fixture);
        var invalidOrders = new Guid[][]
        {
            [], ids[..^1], [ids[0], ids[0], ids[2], ids[3]],
            [ids[0], ids[1], ids[2], Guid.NewGuid()],
            [ids[0], ids[1], ids[2], Guid.Empty], [.. ids, Guid.NewGuid()]
        };
        foreach (var invalid in invalidOrders)
        {
            Assert.Throws<ArgumentException>(() => fixture.Store.SaveAccountOrder(invalid));
            Assert.Equal(before, Snapshot(fixture));
            Assert.Equal(ids, Ids(Reopen(fixture)));
        }
        Assert.Throws<ArgumentNullException>(() => fixture.Store.SaveAccountOrder(null!));
        Assert.Equal(before, Snapshot(fixture));
    }

    [Fact]
    public void InvalidMoveIdsDoNotMutateStorage()
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        fixture.Store.MoveAccount(accounts[2].Id, accounts[0].Id, false);
        var before = Snapshot(fixture);
        foreach (var invalid in new[] { Guid.Empty, Guid.NewGuid() })
        {
            Assert.Throws<ArgumentException>(() => fixture.Store.MoveAccount(invalid, accounts[0].Id, false));
            Assert.Throws<ArgumentException>(() => fixture.Store.MoveAccount(accounts[0].Id, invalid, true));
            Assert.Throws<ArgumentException>(() => fixture.Store.MoveAccount(invalid, invalid, false));
            Assert.Equal(before, Snapshot(fixture));
        }
    }

    [Fact]
    public void ReorderingOnlyWritesOrderAndPreservesAccountsSecretsAndGameData()
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        var time = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        fixture.Store.SetSecret("api-key", "test-api-key");
        foreach (var account in accounts)
        {
            fixture.Store.Save(account, new("test-" + account.Label, "password-" + account.Label));
            fixture.Store.SaveCache(account.Id, new()
            {
                MatchesUpdatedAt = time,
                QueueUpdatedAt = new() { [Queues.Normal] = time },
                Ranks = [new(time, [new(Queues.Solo, "GOLD", "IV", 50, 10, 5)])],
                Matches = [new("JP1_" + account.Label, 490, time, 1800, false, [new(account.Lol.Puuid!, 100, "Ahri", "MIDDLE", 5, 2, 7, 200, 20, true)])],
                Opponents = [new("opponent", Queues.Solo, time, new(Queues.Solo, "SILVER", "I", 50, 10, 5))],
                Forecasts = [new(Queues.Solo, time, 10, 50, 50, "SILVER I", "GOLD IV", "GOLD III", "test")]
            });
        }
        var before = Snapshot(fixture, excludeOrder: true);
        fixture.Store.MoveAccount(accounts[3].Id, accounts[0].Id, false);
        fixture.Store.SaveAccountOrder(accounts.Reverse().Select(a => a.Id).ToArray());
        var restarted = Reopen(fixture);
        Assert.Equal(before, Snapshot(fixture, excludeOrder: true));
        Assert.Equal("test-api-key", restarted.GetSecret("api-key"));
        foreach (var account in accounts)
        {
            Assert.Equal("password-" + account.Label, restarted.Credentials(account.Id).Password);
            Assert.Equal(490, restarted.Cache(account.Id).Matches.Single().QueueId);
            Assert.Equal(time, restarted.Cache(account.Id).QueueUpdatedAt[Queues.Normal]);
        }
    }

    [Fact]
    public void EmptyManualListAppendsNewAccountsInCreationOrder()
    {
        using var fixture = new StoreFixture();
        fixture.Store.SaveAccountOrder([]);
        var first = Account("Zulu"); var second = Account("Alpha");
        fixture.Store.Save(first); fixture.Store.Save(second);
        Assert.Equal(new[] { first.Id, second.Id }, Ids(Reopen(fixture)));
        fixture.Store.Delete(first.Id); fixture.Store.Delete(second.Id);
        fixture.Store.Save(first); fixture.Store.Save(second);
        Assert.Equal(new[] { first.Id, second.Id }, Ids(Reopen(fixture)));
    }

    [Fact]
    public void OrderWriteFailureRollsBackAccountAdditionAndDeletion()
    {
        using var fixture = new StoreFixture();
        var accounts = Seed(fixture.Store);
        fixture.Store.MoveAccount(accounts[2].Id, accounts[0].Id, false);
        fixture.Store.Save(accounts[0], new("test-user", "test-password"));
        using (var connection = fixture.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_account_order BEFORE INSERT ON documents
                WHEN NEW.kind='setting' AND NEW.id='accountOrder'
                BEGIN SELECT RAISE(ABORT, 'simulated order write failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        var before = Snapshot(fixture);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Store.Save(Account("New"), new("new-user", "new-password")));
        Assert.Equal(before, Snapshot(fixture));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Store.Delete(accounts[0].Id));
        Assert.Equal(before, Snapshot(fixture));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Store.MoveAccount(accounts[3].Id, accounts[0].Id, false));
        Assert.Equal(before, Snapshot(fixture));
    }

    // Compare persisted rows, including encrypted bytes, rather than reserialized model values.
    private static string[] Snapshot(StoreFixture fixture, bool excludeOrder = false)
    {
        using var connection = fixture.Open();
        var result = new List<string>();
        var queries = new[]
        {
            "SELECT kind,id,json FROM documents" + (excludeOrder ? " WHERE NOT(kind='setting' AND id='accountOrder')" : "") + " ORDER BY kind,id",
            "SELECT id,hex(cipher) FROM secrets ORDER BY id",
            "SELECT account_id,game,json FROM game_profiles ORDER BY account_id,game",
            "SELECT account_id,game,kind,record_id,json FROM game_records ORDER BY account_id,game,kind,record_id"
        };
        foreach (var query in queries)
        {
            using var command = connection.CreateCommand(); command.CommandText = query;
            using var rows = command.ExecuteReader();
            while (rows.Read()) result.Add(JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount).Select(rows.GetString)));
        }
        return result.ToArray();
    }
}
