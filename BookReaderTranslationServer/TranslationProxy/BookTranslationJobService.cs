using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

namespace TranslationProxy;

/// <summary>
/// Сервис фонового перевода книги: приём файла, определение формата, вызов <see cref="StructuredBookTranslator"/>,
/// хранение файлов в <c>translation-jobs</c>, учёт задач в памяти и в SQLite (переживание перезапуска для завершённых задач),
/// отмена, TTL результата и фоновая очистка.
/// </summary>
public sealed class BookTranslationJobService
{
  readonly ConcurrentDictionary<string, BookTranslationJob> _jobs = new();
  readonly StructuredBookTranslator _translator;
  readonly IConfiguration _cfg;
  readonly string _storageRoot;
  readonly ILogger<BookTranslationJobService> _logger;
  readonly TranslationJobSqliteStore _store;

  public BookTranslationJobService(
      StructuredBookTranslator translator,
      IConfiguration cfg,
      ILogger<BookTranslationJobService> logger,
      TranslationJobSqliteStore store)
  {
    _translator = translator;
    _cfg = cfg;
    _logger = logger;
    _store = store;
    _storageRoot = Path.Combine(AppContext.BaseDirectory, "translation-jobs");
    Directory.CreateDirectory(_storageRoot);

    _store.EnsureSchemaCreated();
    RecoverStoredJobsAfterRestart();

    _ = Task.Run(ExpirationLoopAsync);
  }

  public string CreateJob(Stream fileStream, string originalFileName, string sourceIso, string targetIso)
  {
    var id = Guid.NewGuid().ToString("N");
    var dir = Path.Combine(_storageRoot, id);
    Directory.CreateDirectory(dir);
    var inPath = Path.Combine(dir, "input" + ExtensionFromName(originalFileName));
    using (var fs = File.Create(inPath))
      fileStream.CopyTo(fs);

    var kind = BookFormatDetector.DetectFromFilePath(inPath, originalFileName);
    if (kind == BookFormatKind.Unknown)
    {
      try { Directory.Delete(dir, true); } catch { /* ignore */ }
      throw new InvalidOperationException(
          "Неподдерживаемый формат файла (ожидается FB2, EPUB или архив .fb2.zip / .epub.zip).");
    }

    var job = new BookTranslationJob
    {
      JobId = id,
      Status = BookTranslationJobStatus.Queued,
      Progress = 0,
      Message = "Задача в очереди",
      SourcePath = inPath,
      FormatKind = kind,
      SourceIso = sourceIso.Trim().ToLowerInvariant(),
      TargetIso = targetIso.Trim().ToLowerInvariant(),
      OriginalFileName = originalFileName,
      CreatedUtc = DateTime.UtcNow
    };
    job.Cts = new CancellationTokenSource();
    _jobs[id] = job;
    PersistJob(job);
    _ = Task.Run(() => ProcessJobAsync(job));
    return id;
  }

  public bool TryGetStatus(string jobId, out BookTranslationJobDto dto)
  {
    dto = default!;
    var key = jobId.Trim();

    if (_jobs.TryGetValue(key, out var jFromMem))
    {
      ApplyExpirationIfNeeded(jFromMem);
      dto = new BookTranslationJobDto(jFromMem.JobId, jFromMem.Status, jFromMem.Progress, jFromMem.Message ?? "");
      return true;
    }

    if (!_store.TryGet(key, out var snap))
      return false;

    if (ShouldTreatCompletedAsExpired(snap))
    {
      ExpireStoredJobAndFiles(snap.JobId);
      dto = ExpiredDto(snap.JobId);
      return true;
    }

    if (BookTranslationJobStatusParser.TryParse(snap.Status, out var stCompleted) &&
        stCompleted == BookTranslationJobStatus.Completed &&
        !string.IsNullOrEmpty(snap.ResultPath) && File.Exists(snap.ResultPath))
    {
      var hydrated = HydrateRunningJobFromSnapshot(snap);
      _jobs[hydrated.JobId] = hydrated;
      ApplyExpirationIfNeeded(hydrated);
      dto = new BookTranslationJobDto(hydrated.JobId, hydrated.Status, hydrated.Progress, hydrated.Message ?? "");
      return true;
    }

    dto = new BookTranslationJobDto(snap.JobId, BookTranslationJobStatusParser.ParseOrDefault(snap.Status), snap.Progress, snap.Message ?? "");
    return true;
  }

  public bool TryGetResultPath(string jobId, out string path, out bool expired)
  {
    path = "";
    expired = false;
    var key = jobId.Trim();

    if (_jobs.TryGetValue(key, out var jMem))
    {
      ApplyExpirationIfNeeded(jMem);
      if (jMem.Status == BookTranslationJobStatus.Expired)
      {
        expired = true;
        return false;
      }

      if (jMem.Status != BookTranslationJobStatus.Completed || string.IsNullOrEmpty(jMem.ResultPath) || !File.Exists(jMem.ResultPath))
        return false;
      path = jMem.ResultPath;
      return true;
    }

    if (!_store.TryGet(key, out var snap))
      return false;

    if (ShouldTreatCompletedAsExpired(snap))
    {
      ExpireStoredJobAndFiles(snap.JobId);
      expired = true;
      return false;
    }

    if (!BookTranslationJobStatusParser.TryParse(snap.Status, out var stSnap) ||
        stSnap != BookTranslationJobStatus.Completed ||
        string.IsNullOrEmpty(snap.ResultPath) || !File.Exists(snap.ResultPath))
      return false;

    path = snap.ResultPath;
    return true;
  }

  public bool TryCancel(string jobId)
  {
    var key = jobId.Trim();

    if (_jobs.TryGetValue(key, out var j))
    {
      if (j.Status == BookTranslationJobStatus.Completed)
        return true;
      j.Cts.Cancel();
      if (j.Status is BookTranslationJobStatus.Queued or BookTranslationJobStatus.InProgress)
      {
        j.Status = BookTranslationJobStatus.Canceled;
        j.Message = "Перевод отменён";
        j.Progress = 0;
        j.DbErrorMessage = null;
        PurgeJobDirectory(j.JobId);
        j.SourcePath = "";
        j.ResultPath = null;
        PersistJob(j);
      }

      return true;
    }

    if (!_store.TryGet(key, out var snap))
      return false;
    return BookTranslationJobStatusParser.TryParse(snap.Status, out var st) &&
        st is BookTranslationJobStatus.Completed
            or BookTranslationJobStatus.Failed
            or BookTranslationJobStatus.Canceled
            or BookTranslationJobStatus.Expired;
  }

  async Task ProcessJobAsync(BookTranslationJob job)
  {
    var sw = Stopwatch.StartNew();
    var maxMinutes = _cfg.GetValue("Translation:MaxJobDurationMinutes", 120);
    var ttlHours = _cfg.GetValue("Translation:ResultTtlHours", 48);
    var maxMs = Math.Max(5, maxMinutes) * 60_000;

    try
    {
      await Task.Yield();
      job.Status = BookTranslationJobStatus.InProgress;
      job.Message = "Выполняется перевод…";
      PersistJob(job);

      using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token);
      linked.CancelAfter(maxMs);

      var dir = Path.GetDirectoryName(job.SourcePath)!;
      var outPath = Path.Combine(dir, "output" + ResultExtension(job.FormatKind, job.OriginalFileName));

      var subProgress = new Progress<double>(p =>
      {
        job.Progress = (int)Math.Clamp(Math.Round(p * 100), 0, 99);
        job.Message = $"Перевод: {job.Progress}%";
      });

      switch (job.FormatKind)
      {
        case BookFormatKind.Fb2:
          await _translator.TranslateFb2PathAsync(job.SourcePath, outPath, job.SourceIso, job.TargetIso, subProgress, linked.Token)
              .ConfigureAwait(false);
          break;
        case BookFormatKind.Fb2Zip:
          await _translator.RepackFb2ZipAsync(job.SourcePath, outPath, job.SourceIso, job.TargetIso, subProgress, linked.Token)
              .ConfigureAwait(false);
          break;
        case BookFormatKind.Epub:
          {
            var work = Path.Combine(dir, "epub-work");
            if (Directory.Exists(work))
              Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            ZipFile.ExtractToDirectory(job.SourcePath, work, overwriteFiles: true);
            await _translator.TranslateEpubTreeAsync(work, job.SourceIso, job.TargetIso, subProgress, linked.Token).ConfigureAwait(false);
            if (File.Exists(outPath))
              File.Delete(outPath);
            ZipFile.CreateFromDirectory(work, outPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            try { Directory.Delete(work, true); } catch { /* ignore */ }
            break;
          }
        case BookFormatKind.EpubZip:
          await _translator.RepackEpubZipAsync(job.SourcePath, outPath, job.SourceIso, job.TargetIso, subProgress, linked.Token)
              .ConfigureAwait(false);
          break;
        default:
          throw new InvalidOperationException("Формат не поддерживается.");
      }

      linked.Token.ThrowIfCancellationRequested();

      job.ResultPath = outPath;
      job.Status = BookTranslationJobStatus.Completed;
      job.Progress = 100;
      job.Message = "Перевод завершён";
      job.CompletedUtc = DateTime.UtcNow;
      job.ExpiresUtc = job.CompletedUtc.Value.AddHours(Math.Max(1, ttlHours));
      job.DbErrorMessage = null;
      _logger.LogInformation("Job {JobId} completed in {Ms}ms", job.JobId, sw.ElapsedMilliseconds);
      PersistJob(job);
    }
    catch (OperationCanceledException)
    {
      job.Status = job.Cts.IsCancellationRequested ? BookTranslationJobStatus.Canceled : BookTranslationJobStatus.Failed;
      job.DbErrorMessage = null;
      job.Message = job.Cts.IsCancellationRequested
          ? "Перевод отменён"
          : string.Format(CultureInfo.InvariantCulture, "Перевод остановлен по превышению времени ({0} мин).", maxMinutes);
      job.Progress = 0;
      PurgeJobDirectory(job.JobId);
      job.SourcePath = "";
      job.ResultPath = null;
      _logger.LogWarning("Job {JobId} stopped: {Msg}", job.JobId, job.Message);
      PersistJob(job);
    }
    catch (Exception ex)
    {
      job.Status = BookTranslationJobStatus.Failed;
      job.Message = "Ошибка перевода: " + ex.Message;
      job.DbErrorMessage = ex.Message;
      job.Progress = 0;
      PurgeJobDirectory(job.JobId);
      job.SourcePath = "";
      job.ResultPath = null;
      _logger.LogError(ex, "Job {JobId} failed", job.JobId);
      PersistJob(job);
    }
  }

  bool ShouldTreatCompletedAsExpired(in TranslationJobStoredRow snap)
  {
    if (!BookTranslationJobStatusParser.TryParse(snap.Status, out var st) || st != BookTranslationJobStatus.Completed)
      return false;
    if (!string.IsNullOrEmpty(snap.ResultPath) && !File.Exists(snap.ResultPath))
      return true;
    if (!snap.ResultExpirationAtUtc.HasValue)
      return false;
    return DateTime.UtcNow > snap.ResultExpirationAtUtc.Value;
  }

  /// <summary>После TTL удаляются и входной файл, и результат (вся папка задачи).</summary>
  void ApplyExpirationIfNeeded(BookTranslationJob job)
  {
    if (job.Status != BookTranslationJobStatus.Completed)
      return;
    if (job.ExpiresUtc == null || DateTime.UtcNow <= job.ExpiresUtc)
      return;
    ExpireCompletedJob(job);
  }

  void ExpireCompletedJob(BookTranslationJob job)
  {
    PurgeJobDirectory(job.JobId);
    job.ResultPath = null;
    job.SourcePath = "";
    job.Status = BookTranslationJobStatus.Expired;
    job.Message = "Результат удалён по истечении срока хранения. Запустите перевод снова.";
    job.Progress = 0;
    PersistJob(job);
    _jobs.TryRemove(job.JobId, out _);
  }

  void ExpireStoredJobAndFiles(string jobId)
  {
    PurgeJobDirectory(jobId);
    if (!_store.TryGet(jobId, out var snap))
      return;

    var expired = new TranslationJobStoredRow(
        snap.JobId,
        BookTranslationJobStatus.Expired.ToString(),
        0,
        "Результат удалён по истечении срока хранения. Запустите перевод снова.",
        snap.SourceLanguage,
        snap.TargetLanguage,
        snap.SourceFormat,
        snap.CreatedAtUtc,
        snap.CompletedAtUtc,
        snap.ResultExpirationAtUtc,
        "",
        null,
        null,
        snap.OriginalFileName);
    _store.InsertOrReplace(expired);
    _jobs.TryRemove(jobId, out _);
    _logger.LogInformation("Expired job cleaned (TTL): {JobId}", jobId);
  }

  void RecoverStoredJobsAfterRestart()
  {
    foreach (var snap in _store.SelectAllJobs())
    {
      if (BookTranslationJobStatusParser.TryParse(snap.Status, out var stComp) &&
          stComp == BookTranslationJobStatus.Completed)
      {
        if (ShouldTreatCompletedAsExpired(snap))
        {
          ExpireStoredJobAndFiles(snap.JobId);
          continue;
        }

        if (string.IsNullOrEmpty(snap.ResultPath) || !File.Exists(snap.ResultPath) ||
            string.IsNullOrEmpty(snap.SourcePath) || !File.Exists(snap.SourcePath))
        {
          _logger.LogWarning("Completed job missing files after restart — marking Expired / cleaning: {JobId}", snap.JobId);
          ExpireStoredJobAndFiles(snap.JobId);
          continue;
        }

        var j = HydrateRunningJobFromSnapshot(snap);
        _jobs[j.JobId] = j;
        continue;
      }

      if (BookTranslationJobStatusParser.TryParse(snap.Status, out var orphanSt) &&
          (orphanSt == BookTranslationJobStatus.Queued || orphanSt == BookTranslationJobStatus.InProgress))
      {
        PurgeJobDirectory(snap.JobId);
        var failed = new TranslationJobStoredRow(
            snap.JobId,
            BookTranslationJobStatus.Failed.ToString(),
            0,
            "Перевод прерван из-за перезапуска сервера.",
            snap.SourceLanguage,
            snap.TargetLanguage,
            snap.SourceFormat,
            snap.CreatedAtUtc,
            null,
            null,
            "",
            null,
            "ServerRestart",
            snap.OriginalFileName);
        _store.InsertOrReplace(failed);

        var j = HydrateRunningJobFromSnapshot(failed);
        _jobs[j.JobId] = j;
        _logger.LogWarning("Orphan translation job terminated after restart: {JobId}", snap.JobId);
      }
    }
  }

static BookTranslationJobDto ExpiredDto(string jobId) =>
    new(jobId, BookTranslationJobStatus.Expired, 0, "Результат удалён по истечении срока хранения. Запустите перевод снова.");

  static BookTranslationJob HydrateRunningJobFromSnapshot(in TranslationJobStoredRow r)
  {
    if (!Enum.TryParse(r.SourceFormat, ignoreCase: true, out BookFormatKind kind))
      kind = BookFormatKind.Unknown;

    var j = new BookTranslationJob
    {
      JobId = r.JobId,
      Status = BookTranslationJobStatusParser.ParseOrDefault(r.Status),
      Progress = r.Progress,
      Message = r.Message,
      SourcePath = r.SourcePath ?? "",
      FormatKind = kind,
      SourceIso = r.SourceLanguage,
      TargetIso = r.TargetLanguage,
      OriginalFileName = r.OriginalFileName ?? "",
      CreatedUtc = r.CreatedAtUtc,
      CompletedUtc = r.CompletedAtUtc,
      ExpiresUtc = r.ResultExpirationAtUtc,
      ResultPath = r.ResultPath,
      DbErrorMessage = r.ErrorMessage
    };
    j.Cts = new CancellationTokenSource();
    return j;
  }

  void PurgeJobDirectory(string jobId)
  {
    if (string.IsNullOrWhiteSpace(jobId))
      return;
    var dir = Path.Combine(_storageRoot, jobId.Trim());
    try
    {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to purge job directory for {JobId}", jobId);
    }
  }

  void PersistJob(BookTranslationJob job)
  {
    try
    {
      var row = TranslationJobSqliteStore.SnapshotFromRunningJob(job);
      _store.InsertOrReplace(row);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Persist job snapshot failed for {JobId}", job.JobId);
    }
  }

  async Task ExpirationLoopAsync()
  {
    while (true)
    {
      try
      {
        await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        foreach (var j in _jobs.Values)
          ApplyExpirationIfNeeded(j);

        ExpireStaleRowsFromLoop();
      }
      catch (Exception ex)
      {
        _logger.LogTrace(ex, "Expiration loop iteration");
      }
    }
  }

  /// <summary>
  /// После перезапуска «Completed» задачи уже с истёкшим TTL есть только в БД без кэша в памяти — вычищаем их по циклу.
  /// </summary>
  void ExpireStaleRowsFromLoop()
  {
    foreach (var snap in _store.SelectAllJobs())
    {
      if (BookTranslationJobStatusParser.TryParse(snap.Status, out var loopSt) &&
          loopSt == BookTranslationJobStatus.Completed &&
          ShouldTreatCompletedAsExpired(snap))
      {
        ExpireStoredJobAndFiles(snap.JobId);
      }
    }
  }

  static string ExtensionFromName(string? name)
  {
    if (string.IsNullOrEmpty(name))
      return ".bin";
    var n = name.ToLowerInvariant();
    if (n.EndsWith(".fb2.zip", StringComparison.Ordinal))
      return ".fb2.zip";
    if (n.EndsWith(".epub.zip", StringComparison.Ordinal))
      return ".epub.zip";
    return Path.GetExtension(name);
  }

  static string ResultExtension(BookFormatKind kind, string? original)
  {
    return kind switch
    {
      BookFormatKind.Fb2 => ".fb2",
      BookFormatKind.Fb2Zip => ".fb2.zip",
      BookFormatKind.Epub => ".epub",
      BookFormatKind.EpubZip => ".epub.zip",
      _ => Path.GetExtension(original ?? "") is { Length: > 0 } e ? e : ".bin"
    };
  }
}

/// <summary>Состояние одной задачи перевода книги на сервере (файлы, статус, отмена через <see cref="CancellationTokenSource"/>).</summary>
public sealed class BookTranslationJob
{
  public string JobId { get; init; } = "";
  public BookTranslationJobStatus Status { get; set; } = BookTranslationJobStatus.Queued;
  public int Progress { get; set; }
  public string? Message { get; set; }
  public string SourcePath { get; set; } = "";
  public BookFormatKind FormatKind { get; init; }
  public string SourceIso { get; init; } = "";
  public string TargetIso { get; init; } = "";
  public string OriginalFileName { get; init; } = "";
  public DateTime CreatedUtc { get; init; }
  public DateTime? CompletedUtc { get; set; }
  public DateTime? ExpiresUtc { get; set; }
  public string? ResultPath { get; set; }
  /// <summary>Последнее исключение при <see cref="BookTranslationJobStatus.Failed"/> (для столбца ErrorMessage в SQLite).</summary>
  public string? DbErrorMessage { get; set; }
  public CancellationTokenSource Cts { get; set; } = null!;
}

/// <summary>Данные статуса задачи для клиента (<c>GET /api/translation/book/status</c>).</summary>
public sealed record BookTranslationJobDto(string JobId, BookTranslationJobStatus Status, int Progress, string Message);
