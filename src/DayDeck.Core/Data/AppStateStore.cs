namespace DayDeck.Core.Data;

/// <summary>설정이 아닌 앱 상태(마지막 브리핑 날짜 등)를 DB에 저장한다. 데이터 폴더와 함께 옮겨진다.</summary>
public sealed class AppStateStore
{
    private readonly Database _db;

    public AppStateStore(Database db) => _db = db;

    public string? Get(string key)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string? value)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_state(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
