using System.Text.Json.Serialization;

namespace TranslationProxy;

/// <summary>Тело JSON при ошибке API: код для локализации на клиенте и опциональная техническая деталь.</summary>
public sealed record TranslationApiErrorDto(
    [property: JsonPropertyName("error")] TranslationApiErrorCode Error,
    [property: JsonPropertyName("detail")] string? Detail = null);
