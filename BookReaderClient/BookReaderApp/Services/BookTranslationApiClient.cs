using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using BookReaderApp.Helpers;
using BookReaderApp.Models;
using BookReaderApp.Resources;
using Microsoft.Extensions.Logging;

namespace BookReaderApp.Services;

public sealed class BookTranslationStartResult
{
  /// <summary>Идентификатор задачи перевода книги от прокси.</summary>
  public string? JobId { get; init; }
  /// <summary>Текст ошибки с прокси (HTTP-тело) или сеть при неудаче.</summary>
  public string? ErrorMessage { get; init; }
  /// <summary>Указатель: задан однозначный <see cref="JobId"/> без сообщения об ошибке.</summary>
  public bool Success => !string.IsNullOrEmpty(JobId);
}

/// <summary>REST-клиент сервиса перевода: асинхронный перевод книги и синхронный вызов перевода предложения.</summary>
public sealed class BookTranslationApiClient
{
  readonly HttpClient _http;
  readonly ILogger<BookTranslationApiClient>? _logger;

  /// <summary>Таймаут запроса перевода предложения (LibreTranslate может долго прогреваться).</summary>
  public const int SentenceRequestTimeoutSeconds = 120;

  /// <summary>Создаёт HTTP-клиент с увеличенным лимитом времени файлов перевода книги.</summary>
  public BookTranslationApiClient(ILogger<BookTranslationApiClient>? logger = null)
  {
    _logger = logger;
    _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
  }

  string Root => (TranslationServerConfig.ApiBaseUrl ?? "").Trim().TrimEnd('/');

  /// <summary>Загружает файл книги как multipart на <c>start</c>, возвращает <see cref="BookTranslationStartResult"/>.</summary>
  public async Task<BookTranslationStartResult> StartBookTranslationAsync(
      string filePath, string sourceIso, string targetIso, CancellationToken ct)
  {
    try
    {
      await using var fs = File.OpenRead(filePath);
      using var form = new MultipartFormDataContent();
      form.Add(new StreamContent(fs), "file", Path.GetFileName(filePath));
      form.Add(new StringContent(sourceIso), "sourceLanguage");
      form.Add(new StringContent(targetIso), "targetLanguage");

      using var resp =
          await _http.PostAsync($"{Root}/api/translation/book/start", form, ct).ConfigureAwait(false);
      if (!resp.IsSuccessStatusCode)
      {
        var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (TranslationApiErrorLocalizer.TryParseErrorBody(errBody, out var errCode, out _))
          return new BookTranslationStartResult { ErrorMessage = TranslationApiErrorLocalizer.GetUserMessage(errCode) };

        var hint = string.IsNullOrWhiteSpace(errBody)
            ? $"HTTP {(int)resp.StatusCode}"
            : (errBody.Length <= 400 ? errBody.Trim() : errBody[..400].Trim() + "…");
        return new BookTranslationStartResult { ErrorMessage = FriendlyStartError(hint) };
      }

      var dto = await resp.Content.ReadFromJsonAsync<BookStartResponse>(cancellationToken: ct).ConfigureAwait(false);
      if (string.IsNullOrEmpty(dto?.JobId))
        return new BookTranslationStartResult { ErrorMessage = Strings.TranslationApi_Start_ResponseIncomplete };
      return new BookTranslationStartResult { JobId = dto.JobId };
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      return new BookTranslationStartResult { ErrorMessage = FriendlyStartError(ex) };
    }
  }

  /// <summary>Сетевые сбои по типу (сообщения BCL могут быть локализованы и не содержать «refused»/«timed out»).</summary>
  static bool LooksLikeNetworkTransportFailure(Exception ex)
  {
    for (var e = ex; e != null; e = e.InnerException!)
    {
      if (e is HttpRequestException or SocketException)
        return true;
      if (e is TaskCanceledException)
        return true;
    }

    return false;
  }

  static string FriendlyStartError(Exception ex) =>
      LooksLikeNetworkTransportFailure(ex)
          ? Strings.Translation_Msg_ProxyUnreachable
          : FriendlyStartError(ex.Message);

  static string FriendlyStartError(string technical)
  {
    if (string.IsNullOrWhiteSpace(technical))
      return Strings.Translation_Msg_ProxyUnreachable;
    var t = technical.Trim();
    if (t.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("отверг", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("время ожидания", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("тайм-аут", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("недоступен", StringComparison.OrdinalIgnoreCase))
      return Strings.Translation_Msg_ProxyUnreachable;
    if (t.Contains("jobId", StringComparison.OrdinalIgnoreCase) || t.Length > 120)
      return Strings.TranslationApi_Start_Rejected;
    return Strings.TranslationApi_Start_FailedGeneric;
  }

  /// <summary>
  /// Не бросает при сетевой ошибке: возвращает <see cref="BookTranslationPollOutcome.TransientFailure"/>.
  /// </summary>
  public async Task<(BookTranslationPollOutcome Outcome, BookTranslationStatus? Status)> PollJobStatusAsync(string jobId, CancellationToken ct)
  {
    try
    {
      using var resp = await _http
          .GetAsync($"{Root}/api/translation/book/status?jobId={Uri.EscapeDataString(jobId)}", ct)
          .ConfigureAwait(false);
      if (resp.StatusCode == HttpStatusCode.NotFound)
        return (BookTranslationPollOutcome.NotFoundOnServer, null);
      if (!resp.IsSuccessStatusCode)
        return (BookTranslationPollOutcome.TransientFailure, null);
      var dto =
          await resp.Content.ReadFromJsonAsync<BookTranslationStatus>(cancellationToken: ct).ConfigureAwait(false);
      return (BookTranslationPollOutcome.Ok, dto);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch
    {
      return (BookTranslationPollOutcome.TransientFailure, null);
    }
  }

  /// <summary>Скачивает готовый бинарный результат по <paramref name="jobId"/> во временный файл.</summary>
  public async Task<bool> DownloadResultToFileAsync(string jobId, string destinationPath, CancellationToken ct)
  {
    using var resp = await _http.GetAsync($"{Root}/api/translation/book/result?jobId={Uri.EscapeDataString(jobId)}", ct).ConfigureAwait(false);
    if (!resp.IsSuccessStatusCode)
      return false;
    await using var outStream = File.Create(destinationPath);
    await resp.Content.CopyToAsync(outStream, ct).ConfigureAwait(false);
    return true;
  }

  /// <summary>Отмена задачи на стороне прокси; неудача сети подавляется.</summary>
  public async Task CancelAsync(string jobId, CancellationToken ct)
  {
    try
    {
      using var resp = await _http.PostAsJsonAsync($"{Root}/api/translation/book/cancel", new { jobId }, ct).ConfigureAwait(false);
      _ = resp;
    }
    catch
    {
      // ignore
    }
  }

  /// <summary>
  /// Перевод фрагмента через тот же сервис, что и книги (<c>POST /api/translation/sentence</c>).
  /// Успех — непустой текст; ошибка ответа/сети — <c>null</c>; отмена — <see cref="OperationCanceledException"/>.
  /// </summary>
  public async Task<string?> TranslateSentenceViaProxyAsync(
      string text,
      string sourceIso,
      string targetIso,
      CancellationToken cancellationToken)
  {
    var baseUrl = TranslationServerConfig.ApiBaseUrl?.Trim();
    if (string.IsNullOrEmpty(baseUrl))
      return null;

    try
    {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      cts.CancelAfter(TimeSpan.FromSeconds(SentenceRequestTimeoutSeconds));
      using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(SentenceRequestTimeoutSeconds) };
      var url = $"{baseUrl.TrimEnd('/')}/api/translation/sentence";
      TranslationDiagnostics.Log($"HTTP POST {url} (таймаут {SentenceRequestTimeoutSeconds}s)");
      var sw = Stopwatch.StartNew();
      using var response = await http.PostAsJsonAsync(
          url,
          new { text, sourceLanguage = sourceIso, targetLanguage = targetIso },
          cts.Token).ConfigureAwait(false);
      sw.Stop();
      TranslationDiagnostics.Log($"ответ за {sw.ElapsedMilliseconds}ms: HTTP {(int)response.StatusCode}");
      _logger?.LogInformation("Sentence translate HTTP {Status} in {Ms}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

      if (response.IsSuccessStatusCode)
      {
        var ok = await response.Content.ReadFromJsonAsync<SentenceApiOkResponse>(cancellationToken: cts.Token).ConfigureAwait(false);
        if (ok != null && !string.IsNullOrWhiteSpace(ok.TranslatedText))
        {
          TranslationDiagnostics.Log($"успех предложения, длина={ok.TranslatedText.Trim().Length}");
          return ok.TranslatedText.Trim();
        }

        TranslationDiagnostics.Log("ошибка: в ответе нет translatedText");
      }
      else
      {
        var errBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        string logPart;
        if (TranslationApiErrorLocalizer.TryParseErrorBody(errBody, out var ec, out var det))
        {
          logPart = TranslationApiErrorLocalizer.GetUserMessage(ec);
          if (!string.IsNullOrEmpty(det))
            logPart += " — " + det;
        }
        else
        {
          logPart = errBody.Length <= 300 ? errBody : errBody.Substring(0, 300);
        }

        TranslationDiagnostics.Log($"HTTP ошибка: {(int)response.StatusCode} {logPart}");
      }
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      if (ex.InnerException is OperationCanceledException oce)
        throw oce;
      TranslationDiagnostics.Log($"исключение: {ex.GetType().Name}: {ex.Message}");
      _logger?.LogWarning(ex, "Translate sentence HTTP failed");
    }

    cancellationToken.ThrowIfCancellationRequested();
    return null;
  }

  sealed class BookStartResponse
  {
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }
  }

  sealed class SentenceApiOkResponse
  {
    [JsonPropertyName("translatedText")]
    public string? TranslatedText { get; set; }
  }
}

public sealed class BookTranslationStatus
{
  [JsonPropertyName("status")]
  [JsonConverter(typeof(JsonStringEnumConverter))]
  public BookTranslationJobStatus? Status { get; set; }

  [JsonPropertyName("progress")]
  public int Progress { get; set; }

  [JsonPropertyName("message")]
  public string? Message { get; set; }
}
