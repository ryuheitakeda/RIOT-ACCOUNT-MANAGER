using RiotAccounts.Core;
using Xunit;

public sealed class AccountNoteTests
{
    [Fact]
    public void NoteRoundTripsAndSurvivesProfileChanges()
    {
        using var fixture = new StoreFixture();
        var account = new RiotAccount(Guid.NewGuid(), "Main", [new("lol", "Player", "JP1", "JP1", "puuid")], "サブ用\n2行目");
        fixture.Store.Save(account);
        Assert.Equal("サブ用\n2行目", new Store(fixture.File, fixture.Protector).Accounts().Single().Note);
        fixture.Store.Save(account with { Profiles = [new("lol", "Renamed", "JP1", "JP1")] });
        Assert.Equal("サブ用\n2行目", fixture.Store.Accounts().Single().Note);
        fixture.Store.Save(account with { Note = null });
        Assert.Null(fixture.Store.Accounts().Single().Note);
    }

    [Fact]
    public void AccountSavedBeforeNotesLoadsWithoutNote()
    {
        using var fixture = new StoreFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new RiotAccount(id, "Main", [new("lol", "Player", "JP1", "JP1")]));
        using (var c = fixture.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE documents SET json=$json WHERE kind='account' AND id=$id";
            cmd.Parameters.AddWithValue("$json", $$"""{"id":"{{id}}","label":"Main","profiles":[]}""");
            cmd.Parameters.AddWithValue("$id", id.ToString());
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var loaded = fixture.Store.Accounts().Single();
        Assert.Null(loaded.Note);
        Assert.Equal("Main", loaded.Label);
    }
}
