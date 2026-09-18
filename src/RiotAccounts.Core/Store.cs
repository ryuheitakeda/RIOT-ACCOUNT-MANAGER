using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RiotAccounts.Core;

public interface ISecretProtector { byte[] Protect(byte[] data); byte[] Unprotect(byte[] data); }
public interface IGameStatsProvider
{
    Task RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null);
    Task RefreshAnalysisAsync(RiotAccount account, string queue, int count, IProgress<string> progress, CancellationToken cancellationToken);
}

public sealed class Store
{
    private readonly string connectionString;
    private readonly ISecretProtector protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string AccountOrderKey = "accountOrder";
    public Store(string file, ISecretProtector protector)
    {
        this.protector = protector;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        using var c = Open();
        Execute(c, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS documents(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS secrets(id TEXT PRIMARY KEY,cipher BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS game_profiles(account_id TEXT NOT NULL,game TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(account_id,game));
            CREATE TABLE IF NOT EXISTS game_records(account_id TEXT NOT NULL,game TEXT NOT NULL,kind TEXT NOT NULL,record_id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(account_id,game,kind,record_id));
            PRAGMA user_version=2;
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private static void Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Key, object Value)[] parameters)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }

    public List<RiotAccount> Accounts()
    {
        using var c = Open();
        return ReadAccounts(c, null);
    }

    private static List<RiotAccount> ReadAccounts(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT json FROM documents WHERE kind='account'";
        using var r = cmd.ExecuteReader(); var result = new List<RiotAccount>();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<RiotAccount>(r.GetString(0), Json)!);
        r.Close();
        foreach (var account in result)
        {
            cmd.CommandText = "SELECT json FROM game_profiles WHERE account_id=$id ORDER BY game";
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", account.Id.ToString());
            using var profiles = cmd.ExecuteReader();
            var values = new List<GameProfile>();
            while (profiles.Read()) values.Add(JsonSerializer.Deserialize<GameProfile>(profiles.GetString(0), Json)!);
            // Older files embedded profiles in the account document. Read them until the next save migrates it.
            if (values.Count > 0) { account.Profiles.Clear(); account.Profiles.AddRange(values); }
        }
        var alphabetical = result.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        var order = ReadAccountOrder(c, tx);
        if (order == null) return alphabetical;
        var positions = order.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        // IDs absent from an older order are appended; labels only break ties for these new accounts.
        return alphabetical.OrderBy(a => positions.GetValueOrDefault(a.Id, int.MaxValue)).ToList();
    }

    private static List<Guid>? ReadAccountOrder(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT json FROM documents WHERE kind='setting' AND id=$id";
        cmd.Parameters.AddWithValue("$id", AccountOrderKey);
        if (cmd.ExecuteScalar() is not string json) return null;
        List<Guid>? order;
        try { order = JsonSerializer.Deserialize<List<Guid>>(json, Json); }
        catch (JsonException error) { throw new InvalidOperationException("保存されたアカウントの並び順が不正です。", error); }
        if (order == null || order.Contains(Guid.Empty) || order.Distinct().Count() != order.Count)
            throw new InvalidOperationException("保存されたアカウントの並び順が不正です。");
        return order;
    }

    public void SaveAccountOrder(IReadOnlyList<Guid> accountIds)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var order = accountIds.ToList();
        using var c = Open(); using var tx = c.BeginTransaction();
        var existing = ReadAccounts(c, tx).Select(a => a.Id).ToHashSet();
        if (order.Contains(Guid.Empty) || order.Distinct().Count() != order.Count || order.Count != existing.Count || !existing.SetEquals(order))
            throw new ArgumentException("並び順には登録済みの全アカウントを重複なく指定してください。", nameof(accountIds));
        WriteDocument(c, tx, "setting", AccountOrderKey, order);
        tx.Commit();
    }

    public void MoveAccount(Guid accountId, Guid relativeToId, bool after)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var original = ReadAccounts(c, tx).Select(a => a.Id).ToList();
        if (accountId == Guid.Empty || !original.Contains(accountId))
            throw new ArgumentException("移動するアカウントが見つかりません。", nameof(accountId));
        if (relativeToId == Guid.Empty || !original.Contains(relativeToId))
            throw new ArgumentException("移動先のアカウントが見つかりません。", nameof(relativeToId));
        if (accountId == relativeToId) return;
        var order = original.ToList();
        order.Remove(accountId);
        order.Insert(order.IndexOf(relativeToId) + (after ? 1 : 0), accountId);
        if (order.SequenceEqual(original)) return;
        WriteDocument(c, tx, "setting", AccountOrderKey, order);
        tx.Commit();
    }

    public T? Read<T>(string kind, string id)
    {
        if (kind == "cache" && typeof(T) == typeof(AccountCache)) return (T)(object)Cache(Guid.Parse(id));
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM documents WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string value ? JsonSerializer.Deserialize<T>(value, Json) : default;
    }

    public void Write<T>(string kind, string id, T value)
    {
        if (kind == "cache" && value is AccountCache cache) { SaveCache(Guid.Parse(id), cache); return; }
        using var c = Open();
        WriteDocument(c, null, kind, id, value);
    }

    private static void WriteDocument<T>(SqliteConnection c, SqliteTransaction? tx, string kind, string id, T value) =>
        Execute(c, tx, "INSERT INTO documents VALUES($kind,$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json",
            ("$kind", kind), ("$id", id), ("$json", JsonSerializer.Serialize(value, Json)));

    public AccountCache Cache(Guid id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM documents WHERE kind='cache' AND id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var cache = cmd.ExecuteScalar() is string legacy ? JsonSerializer.Deserialize<AccountCache>(legacy, Json)! : new();
        cmd.CommandText = "SELECT kind,json FROM game_records WHERE account_id=$id AND game='lol' ORDER BY record_id";
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var json = rows.GetString(1);
            switch (rows.GetString(0))
            {
                case "rank": cache.Ranks.Add(JsonSerializer.Deserialize<RankSnapshot>(json, Json)!); break;
                case "match": cache.Matches.Add(JsonSerializer.Deserialize<MatchRecord>(json, Json)!); break;
                case "opponent": cache.Opponents.Add(JsonSerializer.Deserialize<OpponentRankObservation>(json, Json)!); break;
                case "forecast": cache.Forecasts.Add(JsonSerializer.Deserialize<Forecast>(json, Json)!); break;
            }
        }
        cache.Ranks = cache.Ranks.OrderBy(x => x.ObservedAt).ToList();
        cache.Matches = cache.Matches.DistinctBy(x => x.Id).OrderByDescending(x => x.StartedAt).ToList();
        cache.Opponents = cache.Opponents.OrderBy(x => x.ObservedAt).ToList();
        cache.Forecasts = cache.Forecasts.OrderBy(x => x.CreatedAt).ToList();
        return cache;
    }

    public void SaveCache(Guid id, AccountCache cache)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        SaveCache(c, tx, id, cache);
        tx.Commit();
    }

    /// <summary>
    /// Swaps the LoL PUUID for the same account obtained from another source (OP.GG's PUUID is encrypted differently
    /// from the Riot API's) and re-keys saved matches, instead of discarding them as <see cref="Save"/> does on identity changes.
    /// </summary>
    public void ReplaceLolPuuid(Guid id, string previous, string replacement)
    {
        var account = Accounts().Single(a => a.Id == id);
        if (account.Lol.Puuid != previous) throw new InvalidOperationException("アカウントのPUUIDが更新されています。もう一度実行してください。");
        var cache = Cache(id);
        cache.Matches = cache.Matches.Select(m => m.Participants.Any(p => p.Puuid == previous)
            ? m with { Participants = m.Participants.Select(p => p.Puuid == previous ? p with { Puuid = replacement } : p).ToList() }
            : m).ToList();
        using var c = Open(); using var tx = c.BeginTransaction();
        var profile = account.Lol with { Puuid = replacement };
        Execute(c, tx, "UPDATE game_profiles SET json=$json WHERE account_id=$id AND game='lol'",
            ("$id", id.ToString()), ("$json", JsonSerializer.Serialize(profile, Json)));
        SaveCache(c, tx, id, cache);
        tx.Commit();
    }

    private static void SaveCache(SqliteConnection c, SqliteTransaction tx, Guid id, AccountCache cache)
    {
        var key = id.ToString();
        Execute(c, tx, "DELETE FROM game_records WHERE account_id=$id AND game='lol'", ("$id", key));
        void SaveRecord<T>(string kind, string recordId, T value) => Execute(c, tx,
            "INSERT OR REPLACE INTO game_records VALUES($id,'lol',$kind,$record,$json)",
            ("$id", key), ("$kind", kind), ("$record", recordId), ("$json", JsonSerializer.Serialize(value, Json)));
        foreach (var rank in cache.Ranks) SaveRecord("rank", rank.ObservedAt.UtcTicks.ToString(), rank);
        foreach (var match in cache.Matches) SaveRecord("match", match.Id, match);
        foreach (var opponent in cache.Opponents) SaveRecord("opponent", $"{opponent.QueueType}:{opponent.Puuid}:{opponent.ObservedAt.UtcTicks}", opponent);
        foreach (var forecast in cache.Forecasts) SaveRecord("forecast", $"{forecast.QueueType}:{forecast.CreatedAt.UtcTicks}", forecast);
        WriteDocument(c, tx, "cache", key, new AccountCache { MatchesUpdatedAt = cache.MatchesUpdatedAt, QueueUpdatedAt = cache.QueueUpdatedAt });
    }

    public void Save(RiotAccount account, Credentials? credentials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Label);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Lol.GameName);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Lol.TagLine);
        _ = Regions.Regional(account.Lol.Platform);
        if (account.Profiles.Select(p => p.Game).Distinct().Count() != account.Profiles.Count)
            throw new ArgumentException("ゲームプロフィールはゲームごとに1件登録してください。");
        var previous = Accounts().SingleOrDefault(a => a.Id == account.Id);
        var oldProfile = previous?.Lol;
        var current = account.Lol;
        var identityChanged = oldProfile != null &&
            (!string.Equals(oldProfile.Platform, current.Platform, StringComparison.OrdinalIgnoreCase) ||
             (oldProfile.Puuid != null && current.Puuid != null && oldProfile.Puuid != current.Puuid) ||
             oldProfile.GameName != current.GameName || oldProfile.TagLine != current.TagLine);
        if (identityChanged)
            account = account with { Profiles = account.Profiles.Select(p => p.Game == "lol" ? p with { Puuid = null } : p).ToList() };
        byte[]? cipher = null;
        if (credentials != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Username);
            ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Password);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials, Json);
            try { cipher = protector.Protect(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        using var c = Open(); using var tx = c.BeginTransaction();
        var key = account.Id.ToString();
        WriteDocument(c, tx, "account", key, account with { Profiles = [] });
        Execute(c, tx, "DELETE FROM game_profiles WHERE account_id=$id", ("$id", key));
        foreach (var profile in account.Profiles)
            Execute(c, tx, "INSERT INTO game_profiles VALUES($id,$game,$json)",
                ("$id", key), ("$game", profile.Game), ("$json", JsonSerializer.Serialize(profile, Json)));
        if (identityChanged)
        {
            Execute(c, tx, "DELETE FROM game_records WHERE account_id=$id AND game='lol'", ("$id", key));
            Execute(c, tx, "DELETE FROM documents WHERE kind='cache' AND id=$id", ("$id", key));
        }
        if (cipher != null) Execute(c, tx, "INSERT INTO secrets VALUES($id,$cipher) ON CONFLICT(id) DO UPDATE SET cipher=excluded.cipher",
            ("$id", key), ("$cipher", cipher));
        var order = ReadAccountOrder(c, tx);
        if (order != null && !order.Contains(account.Id))
        {
            order.Add(account.Id);
            WriteDocument(c, tx, "setting", AccountOrderKey, order);
        }
        tx.Commit();
    }

    public Credentials Credentials(Guid id) => JsonSerializer.Deserialize<Credentials>(GetSecret(id.ToString()) ?? throw new InvalidOperationException("ログイン情報を登録してください。"), Json)!;
    public string? GetSecret(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT cipher FROM secrets WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not byte[] cipher) return null;
        var bytes = protector.Unprotect(cipher);
        try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void SetSecret(string id, string value)
    {
        var plain = Encoding.UTF8.GetBytes(value); byte[] cipher;
        try { cipher = protector.Protect(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        using var c = Open();
        Execute(c, null, "INSERT INTO secrets VALUES($id,$cipher) ON CONFLICT(id) DO UPDATE SET cipher=excluded.cipher", ("$id", id), ("$cipher", cipher));
    }
    public void Delete(Guid id)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Execute(c, tx, "DELETE FROM documents WHERE id=$id; DELETE FROM secrets WHERE id=$id; DELETE FROM game_profiles WHERE account_id=$id; DELETE FROM game_records WHERE account_id=$id;", ("$id", id.ToString()));
        var order = ReadAccountOrder(c, tx);
        if (order != null && order.Remove(id)) WriteDocument(c, tx, "setting", AccountOrderKey, order);
        tx.Commit();
    }
}
