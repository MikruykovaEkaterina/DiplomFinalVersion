using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TranslationProxy;

/// <summary>Клиент к экземпляру LibreTranslate: POST <c>translate</c> через именованный <see cref="HttpClient"/> из фабрики.</summary>
public sealed class LibreTranslateClient
{
  readonly IHttpClientFactory _httpFactory;

  public LibreTranslateClient(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

  public async Task<string?> TranslateAsync(string text, string sourceIso, string targetIso, CancellationToken ct)
  {
    var t = text.Trim();
    if (t.Length == 0)
      return "";

    var client = _httpFactory.CreateClient("LibreTranslate");
    var body = new LibreTranslateRequest(t, sourceIso, targetIso);
    using var response = await client.PostAsJsonAsync("translate", body, ct).ConfigureAwait(false);
    var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
      throw new InvalidOperationException($"LibreTranslate HTTP {(int)response.StatusCode}: {(raw.Length > 200 ? raw[..200] : raw)}");

    var parsed = System.Text.Json.JsonSerializer.Deserialize<LibreTranslateOk>(raw);
    return parsed?.TranslatedText;
  }

  /// <summary>JSON-тело запроса LibreTranslate (<c>q</c>, <c>source</c>, <c>target</c>, <c>format</c>).</summary>
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

  /// <summary>Успешный ответ LibreTranslate с полем <c>translatedText</c>.</summary>
  sealed class LibreTranslateOk
  {
    [JsonPropertyName("translatedText")]
    public string? TranslatedText { get; set; }
  }
}
