using System.Text.Json.Serialization;
using TranslationProxy;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("LibreTranslate", (sp, client) =>
{
  var baseUrl = builder.Configuration["LibreTranslate:BaseUrl"] ?? "http://127.0.0.1:5000";
  client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
  client.Timeout = TimeSpan.FromSeconds(300);
});

builder.Services.AddSingleton<LibreTranslateClient>();
var maxChunk = builder.Configuration.GetValue("Translation:MaxChunkChars", 3500);
builder.Services.AddSingleton(sp => new StructuredBookTranslator(
    sp.GetRequiredService<LibreTranslateClient>(),
    maxChunk));
builder.Services.AddSingleton<TranslationJobSqliteStore>();
builder.Services.AddSingleton<BookTranslationJobService>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
  o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:8080");

var app = builder.Build();

var maxLen = builder.Configuration.GetValue("Translation:MaxTextPerSentenceApi", 8000);

app.MapGet("/", () => Results.Text(
    "BookReader TranslationProxy\n" +
    "POST /api/translation/sentence — JSON: text, sourceLanguage, targetLanguage\n" +
    "POST /api/translation/book/start — multipart: file, sourceLanguage, targetLanguage\n" +
    "GET /api/translation/book/status?jobId=\n" +
    "GET /api/translation/book/result?jobId=\n" +
    "POST /api/translation/book/cancel — JSON: jobId\n",
    "text/plain; charset=utf-8"));

app.MapPost("/api/translation/sentence", async (SentenceRequest req, IHttpClientFactory httpFactory) =>
{
  if (string.IsNullOrWhiteSpace(req.Text))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InvalidRequestEmptyText), statusCode: 400);
  if (string.IsNullOrWhiteSpace(req.SourceLanguage) || string.IsNullOrWhiteSpace(req.TargetLanguage))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InvalidLanguageMissing), statusCode: 400);
  var t = req.Text.Trim();
  if (t.Length > maxLen)
    t = t.Substring(0, maxLen);

  try
  {
    var client = httpFactory.CreateClient("LibreTranslate");
    var ltBody = new LibreTranslateRequest(t, req.SourceLanguage, req.TargetLanguage);
    using var response = await client.PostAsJsonAsync("translate", ltBody).ConfigureAwait(false);
    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
    {
      var d = body.Length > 200 ? body.Substring(0, 200) : body;
      return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.UpstreamError, d), statusCode: 502);
    }

    var parsed = System.Text.Json.JsonSerializer.Deserialize<LibreTranslateOk>(body);
    if (parsed?.TranslatedText == null)
      return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InternalErrorEmptyTranslation), statusCode: 502);
    return Results.Json(new OkDto(parsed.TranslatedText));
  }
  catch (Exception ex)
  {
    Console.Error.WriteLine($"[TranslationProxy] {ex}");
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InternalError, ex.Message), statusCode: 500);
  }
});

app.MapPost("/api/translation/book/start", async (HttpRequest request, BookTranslationJobService jobs) =>
{
  if (!request.HasFormContentType)
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InvalidRequestMultipartExpected), statusCode: 400);
  var form = await request.ReadFormAsync().ConfigureAwait(false);
  var file = form.Files["file"];
  var src = form["sourceLanguage"].ToString();
  var tgt = form["targetLanguage"].ToString();
  if (file == null || file.Length == 0)
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InvalidRequestNoFile), statusCode: 400);
  if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InvalidLanguageMissing), statusCode: 400);

  try
  {
    await using var stream = file.OpenReadStream();
    var id = jobs.CreateJob(stream, file.FileName, src, tgt);
    return Results.Json(new { jobId = id });
  }
  catch (Exception ex)
  {
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.InternalError, ex.Message), statusCode: 500);
  }
});

app.MapGet("/api/translation/book/status", (string jobId, BookTranslationJobService jobs) =>
{
  if (string.IsNullOrWhiteSpace(jobId))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.JobNotFound), statusCode: 404);
  if (!jobs.TryGetStatus(jobId, out var dto))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.JobNotFound), statusCode: 404);
  return Results.Json(new { status = dto.Status, progress = dto.Progress, message = dto.Message });
});

app.MapGet("/api/translation/book/result", (string jobId, BookTranslationJobService jobs) =>
{
  if (string.IsNullOrWhiteSpace(jobId))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.JobNotFound), statusCode: 404);
  if (!jobs.TryGetResultPath(jobId, out var path, out var expired))
  {
    if (expired)
      return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.ResultExpired), statusCode: 410);
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.JobNotFound), statusCode: 404);
  }

  var name = Path.GetFileName(path);
  return Results.File(File.OpenRead(path), contentType: "application/octet-stream", fileDownloadName: name);
});

app.MapPost("/api/translation/book/cancel", async (HttpRequest request, BookTranslationJobService jobs) =>
{
  CancelBody? body = null;
  try
  {
    body = await request.ReadFromJsonAsync<CancelBody>().ConfigureAwait(false);
  }
  catch { }
  var jobId = body?.JobId ?? "";
  if (string.IsNullOrWhiteSpace(jobId))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.JobNotFound), statusCode: 404);
  if (!jobs.TryCancel(jobId))
    return Results.Json(new TranslationApiErrorDto(TranslationApiErrorCode.JobNotFound), statusCode: 404);
  return Results.Ok();
});

app.Run();

/// <summary>Тело запроса <c>POST /api/translation/book/cancel</c> с идентификатором задачи.</summary>
internal sealed record CancelBody([property: JsonPropertyName("jobId")] string? JobId);

/// <summary>Входные данные <c>POST /api/translation/sentence</c>: текст и коды языков.</summary>
internal sealed record SentenceRequest(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("sourceLanguage")] string? SourceLanguage,
    [property: JsonPropertyName("targetLanguage")] string? TargetLanguage);

/// <summary>Успешный ответ перевода одного предложения (<c>translatedText</c>).</summary>
internal sealed record OkDto(string TranslatedText);

/// <summary>Модель JSON для прямого вызова LibreTranslate из конечной точки предложения (без общего клиента).</summary>
internal sealed class LibreTranslateRequest
{
  public LibreTranslateRequest(string q, string source, string target)
  {
    Q = q;
    Source = source;
    Target = target;
    Format = "text";
  }

  [JsonPropertyName("q")]
  public string Q { get; }

  [JsonPropertyName("source")]
  public string Source { get; }

  [JsonPropertyName("target")]
  public string Target { get; }

  [JsonPropertyName("format")]
  public string Format { get; }
}

/// <summary>Разбор ответа LibreTranslate при переводе предложения.</summary>
internal sealed class LibreTranslateOk
{
  [JsonPropertyName("translatedText")]
  public string? TranslatedText { get; set; }
}
