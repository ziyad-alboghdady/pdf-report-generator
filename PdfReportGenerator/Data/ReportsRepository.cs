using System.Globalization;
using PdfReportGenerator.Models;

namespace PdfReportGenerator.Data;

/// <summary>
/// The reports table: bookkeeping about files we produced.
///
/// Every timestamp here is UTC, stored as a round-trip ISO 8601 string ("o").
/// SQLite's date('now') is also UTC, so the once-per-day check in Stage 5
/// compares like with like. Storing local time would make "today" mean
/// different things to the database and to the application - a bug that
/// only appears for users in some timezones, at some hours.
/// </summary>
public static class ReportsRepository
{
    /// <summary>
    /// Reserves an id by inserting the row before the PDF exists.
    ///
    /// The path is written back in <see cref="SetPath"/> once rendering has
    /// succeeded. Until then the row carries an empty path, and every read
    /// path in this class ignores rows like that - a half-born report is
    /// never handed to a client.
    /// </summary>
    public static int Insert(DateTime createdAtUtc)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO reports (path, created_at) VALUES ('', $created_at);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$created_at", createdAtUtc.ToString("o", CultureInfo.InvariantCulture));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static void SetPath(int id, string path)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE reports SET path = $path WHERE id = $id;";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes a reservation whose rendering failed, so it cannot block tomorrow's check.</summary>
    public static void Delete(int id)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reports WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public static ReportRecord? FindById(int id)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, path, created_at FROM reports WHERE id = $id AND path <> '';";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// Stage 5: today's report, if one already exists.
    ///
    /// date(created_at) truncates the stored timestamp to a day in UTC, and
    /// date('now') is today in UTC. Newest first, so a forced regeneration
    /// becomes the one a later plain request gets back.
    /// </summary>
    public static ReportRecord? FindToday()
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, path, created_at
            FROM reports
            WHERE date(created_at) = date('now') AND path <> ''
            ORDER BY id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static List<ReportRecord> ListAll()
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, path, created_at FROM reports WHERE path <> '' ORDER BY id DESC;";
        using var reader = command.ExecuteReader();

        var rows = new List<ReportRecord>();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    private static ReportRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(reader.GetInt32(0),
            reader.GetString(1),
            ParseUtc(reader.GetString(2)));

    /// <summary>
    /// Reads a timestamp written by DateTime.ToString("o").
    ///
    /// RoundtripKind is the ONLY style that may be used here: it reads the
    /// trailing "Z" and sets Kind = Utc from the text itself. Combining it
    /// with AdjustToUniversal or AssumeUniversal is rejected by .NET at
    /// runtime with an ArgumentException - the two ideas contradict each
    /// other, because one says "trust the string" and the other says
    /// "override what the string says".
    ///
    /// ToUniversalTime() is a belt-and-braces no-op for a value that is
    /// already Utc, and converts correctly in the one case it is not: a
    /// legacy row written before the "o" format was used.
    /// </summary>
    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToUniversalTime();
}
