namespace BookReaderApp.Services;

/// <summary>
/// Базовый URL TranslationProxy (LAN). Порт — как у сервера в <c>appsettings.json</c> («Urls», обычно 8080).
/// При смене IP или порта измените константу <see cref="DefaultApiBaseUrl"/>.
/// </summary>
public static class TranslationServerConfig
{
  /// <summary>Фиксированный LAN-адрес машины с TranslationProxy.</summary>
  public const string DefaultApiBaseUrl = "http://192.168.0.185:8080";

  /// <summary>Базовый URL без завершающего слэша не требуется — клиенты обрезают сами.</summary>
  public static string ApiBaseUrl => DefaultApiBaseUrl;
}
