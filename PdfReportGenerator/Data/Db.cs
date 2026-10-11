using Microsoft.Data.Sqlite;

namespace PdfReportGenerator.Data;

/// <summary>
/// SQLite is a file, not a server: there is nothing to install and nothing
/// to connect to. Open() hands out a fresh connection per unit of work -
/// connections are cheap here, and a long-lived shared one would serialise
/// everything behind a single lock for no benefit.
/// </summary>
public static class Db
{
    public static SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={Paths.Database}");
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Creates both tables if they are missing. Safe to call on every start.
    ///
    /// Two tables, two jobs: "books" is the data being reported on, "reports"
    /// is the bookkeeping about reports we produced. The reports row stores a
    /// PATH, never the bytes - see the README for why that matters.
    /// </summary>
    public static void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS books (
                id       INTEGER PRIMARY KEY,
                title    TEXT    NOT NULL,
                price    REAL    NOT NULL,
                rating   INTEGER NOT NULL,
                url      TEXT    NOT NULL,
                category TEXT    NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS reports (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                path       TEXT    NOT NULL,
                created_at TEXT    NOT NULL
            );

            -- The once-per-day lookup in Stage 5 filters on the date part of
            -- created_at, so give it something to read.
            CREATE INDEX IF NOT EXISTS ix_reports_created_at ON reports (created_at);
            """;
        command.ExecuteNonQuery();
    }
}
