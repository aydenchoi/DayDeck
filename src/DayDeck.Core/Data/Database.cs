using Microsoft.Data.Sqlite;
using DayDeck.Core.Diagnostics;

namespace DayDeck.Core.Data;

/// <summary>
/// SQLite 데이터베이스 진입점. ORM 없이 연결만 관리한다.
/// 연결은 짧게 열고 닫는다 (Microsoft.Data.Sqlite가 내부적으로 풀링한다).
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // foreign_keys와 synchronous는 연결마다 설정해야 한다.
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 3000;");
        return connection;
    }

    public int GetSchemaVersion()
    {
        using var connection = Open();
        return GetUserVersion(connection);
    }

    /// <summary>적용되지 않은 마이그레이션을 순서대로 적용한다. 각 버전은 하나의 트랜잭션이다.</summary>
    public void Migrate()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);

        using var connection = Open();
        // WAL 대신 롤백 저널(DELETE)을 쓴다. WAL은 커밋된 데이터가 data.db-wal 파일에 남아 있을 수 있어
        // data.db만 복사하면 최근 변경이 빠진다. DELETE 모드는 커밋이 끝나면 모든 내용이 data.db 한 파일에 있다.
        // 이 앱은 단일 프로세스라 WAL의 동시 읽기/쓰기 이점이 거의 없다.
        // journal_mode는 DB 파일에 영구 저장되므로, 예전에 WAL로 만든 DB도 여기서 전환된다.
        Execute(connection, "PRAGMA journal_mode = DELETE;");

        var current = GetUserVersion(connection);
        if (current > Migrations.Latest)
        {
            throw new InvalidOperationException(
                $"데이터베이스 버전({current})이 앱이 지원하는 버전({Migrations.Latest})보다 높습니다. 앱을 업데이트하세요.");
        }

        foreach (var (version, sql) in Migrations.All)
        {
            if (version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            Execute(connection, sql, transaction);
            Execute(connection, $"PRAGMA user_version = {version};", transaction);
            transaction.Commit();
            Log.Info($"DB 마이그레이션 v{version} 적용");
        }
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
