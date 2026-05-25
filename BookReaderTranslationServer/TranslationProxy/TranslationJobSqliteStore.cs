using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TranslationProxy;

/// <summary>
/// Хранилище SQLite для задач перевода книги (<c>/api/translation/book/*</c>): статус, пути к файлам, срок годности результата.
/// Перевод отдельных предложений (<c>/api/translation/sentence</c>) сюда не сохраняется.
/// </summary>
public sealed class TranslationJobSqliteStore
{
  readonly string _connString;
  readonly object _gate = new();
  readonly ILogger<TranslationJobSqliteStore> _logger;

  public TranslationJobSqliteStore(IConfiguration configuration, ILogger<TranslationJobSqliteStore> logger)
  {
    _logger = logger;
    var custom = configuration["Translation:JobsDatabasePath"];
    var path = string.IsNullOrWhiteSpace(custom)
        ? Path.Combine(AppContext.BaseDirectory, "translation-jobs.db")
        : Path.IsPathRooted(custom!)
            ? custom!
            : Path.Combine(AppContext.BaseDirectory, custom!);
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir))
      Directory.CreateDirectory(dir);
    _connString = new SqliteConnectionStringBuilder
    {
      DataSource = path,
      Mode = SqliteOpenMode.ReadWriteCreate,
      Cache = SqliteCacheMode.Shared
    }.ToString();
    _logger.LogInformation("Translation jobs SQLite: {Path}", path);
  }

  public void EnsureSchemaCreated()
  {
    lock (_gate)
    {
      using var c = Open();
      using var cmd = c.CreateCommand();
      cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS TranslationJob (
                          JobId TEXT NOT NULL PRIMARY KEY,
                          Status TEXT NOT NULL,
                          Progress INTEGER NOT NULL DEFAULT 0,
                          Message TEXT,
                          SourceLanguage TEXT NOT NULL,
                          TargetLanguage TEXT NOT NULL,
                          SourceFormat TEXT NOT NULL,
                          CreatedAt TEXT NOT NULL,
                          CompletedAt TEXT,
                          ResultExpirationAt TEXT,
                          SourcePath TEXT NOT NULL,
                          ResultPath TEXT,
                          ErrorMessage TEXT,
                          OriginalFileName TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS IX_TranslationJob_Expiration ON TranslationJob (ResultExpirationAt);
                        CREATE INDEX IF NOT EXISTS IX_TranslationJob_Status ON TranslationJob (Status);
                        """;
      cmd.ExecuteNonQuery();
    }
  }

  public void InsertOrReplace(in TranslationJobStoredRow row)
  {
    lock (_gate)
    {
      using var c = Open();
      using var cmd = c.CreateCommand();
      cmd.CommandText = """
                        INSERT OR REPLACE INTO TranslationJob (
                          JobId, Status, Progress, Message,
                          SourceLanguage, TargetLanguage, SourceFormat,
                          CreatedAt, CompletedAt, ResultExpirationAt,
                          SourcePath, ResultPath,
                          ErrorMessage, OriginalFileName
                        ) VALUES (
                          @id, @st, @pr, @msg,
                          @srcL, @tgtL, @fmt,
                          @created, @completed, @expires,
                          @srcPath, @resPath,
                          @err, @origName
                        )
                        """;
      AddParams(cmd, row);
      cmd.ExecuteNonQuery();
    }
  }

  public bool TryGet(string jobId, out TranslationJobStoredRow row)
  {
    row = default;
    if (string.IsNullOrWhiteSpace(jobId))
      return false;
    lock (_gate)
    {
      using var c = Open();
      using var cmd = c.CreateCommand();
      cmd.CommandText = """
                        SELECT JobId, Status, Progress, Message,
                               SourceLanguage, TargetLanguage, SourceFormat,
                               CreatedAt, CompletedAt, ResultExpirationAt,
                               SourcePath, ResultPath,
                               ErrorMessage, OriginalFileName
                        FROM TranslationJob WHERE JobId = @id
                        LIMIT 1
                        """;
      cmd.Parameters.AddWithValue("@id", jobId.Trim());
      using var reader = cmd.ExecuteReader();
      if (!reader.Read())
        return false;
      row = ReadRow(reader);
      return true;
    }
  }

  public IReadOnlyList<TranslationJobStoredRow> SelectAllJobs()
  {
    lock (_gate)
    {
      using var c = Open();
      using var cmd = c.CreateCommand();
      cmd.CommandText = """
                        SELECT JobId, Status, Progress, Message,
                               SourceLanguage, TargetLanguage, SourceFormat,
                               CreatedAt, CompletedAt, ResultExpirationAt,
                               SourcePath, ResultPath,
                               ErrorMessage, OriginalFileName
                        FROM TranslationJob
                        ORDER BY datetime(CreatedAt) ASC
                        """;
      using var reader = cmd.ExecuteReader();
      var list = new List<TranslationJobStoredRow>();
      while (reader.Read())
        list.Add(ReadRow(reader));
      return list;
    }
  }

  /// <remarks>Столбцы в порядке SELECT выше.</remarks>
  static TranslationJobStoredRow ReadRow(SqliteDataReader reader)
  {
    return new TranslationJobStoredRow(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        ParseUtc(reader.GetString(7)),
        reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8)),
        reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
        reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetString(13));
  }

  static string FmtUtc(DateTime dt) =>
      DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

  static DateTime ParseUtc(string s)
  {
    var dt = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
  }

  static void AddParams(SqliteCommand cmd, in TranslationJobStoredRow row)
  {
    cmd.Parameters.AddWithValue("@id", row.JobId);
    cmd.Parameters.AddWithValue("@st", row.Status);
    cmd.Parameters.AddWithValue("@pr", row.Progress);
    cmd.Parameters.AddWithValue("@msg", (object?)row.Message ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@srcL", row.SourceLanguage);
    cmd.Parameters.AddWithValue("@tgtL", row.TargetLanguage);
    cmd.Parameters.AddWithValue("@fmt", row.SourceFormat);
    cmd.Parameters.AddWithValue("@created", FmtUtc(row.CreatedAtUtc));
    cmd.Parameters.AddWithValue("@completed", row.CompletedAtUtc.HasValue ? FmtUtc(row.CompletedAtUtc.Value) : DBNull.Value);
    cmd.Parameters.AddWithValue("@expires", row.ResultExpirationAtUtc.HasValue ? FmtUtc(row.ResultExpirationAtUtc.Value) : DBNull.Value);
    cmd.Parameters.AddWithValue("@srcPath", row.SourcePath ?? "");
    cmd.Parameters.AddWithValue("@resPath", (object?)row.ResultPath ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@err", (object?)row.ErrorMessage ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@origName", row.OriginalFileName ?? "");
  }

  SqliteConnection Open()
  {
    var conn = new SqliteConnection(_connString);
    conn.Open();
    using (var prag = conn.CreateCommand())
    {
      prag.CommandText = "PRAGMA journal_mode=WAL;";
      prag.ExecuteNonQuery();
    }

    return conn;
  }

  internal static TranslationJobStoredRow SnapshotFromRunningJob(BookTranslationJob j) =>
      new TranslationJobStoredRow(
          j.JobId,
          j.Status.ToString(),
          j.Progress,
          j.Message,
          j.SourceIso,
          j.TargetIso,
          j.FormatKind.ToString(),
          j.CreatedUtc,
          j.CompletedUtc,
          j.ExpiresUtc,
          j.SourcePath,
          j.ResultPath,
          string.IsNullOrEmpty(j.DbErrorMessage) ? null : j.DbErrorMessage,
          j.OriginalFileName);
}

/// <summary>Снимок строки TranslationJob (SQLite).</summary>
public readonly record struct TranslationJobStoredRow(
    string JobId,
    string Status,
    int Progress,
    string? Message,
    string SourceLanguage,
    string TargetLanguage,
    string SourceFormat,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? ResultExpirationAtUtc,
    string SourcePath,
    string? ResultPath,
    string? ErrorMessage,
    string OriginalFileName);
