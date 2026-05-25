using System.Text.Json;
using BookReaderApp.Models;
using BookReaderApp.Resources;

namespace BookReaderApp.Helpers;

/// <summary>
/// Локализованный текст для ответа прокси с полями <c>error</c> и опционально <c>detail</c>.
/// Старые ответы с полем <c>message</c> учитываются как необязательный detail при разборе JSON.
/// </summary>
public static class TranslationApiErrorLocalizer
{
  /// <summary>Разбор JSON тела ошибки (новый формат <c>error</c>/<c>detail</c> или старый с <c>message</c>).</summary>
  public static bool TryParseErrorBody(string? json, out TranslationApiErrorCode code, out string? detail)
  {
    code = default;
    detail = null;
    if (string.IsNullOrWhiteSpace(json))
      return false;
    try
    {
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      if (!root.TryGetProperty("error", out var errEl))
        return false;
      var errStr = errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : null;
      if (string.IsNullOrEmpty(errStr) || !Enum.TryParse(errStr, ignoreCase: true, out code))
        return false;
      if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
        detail = d.GetString();
      else if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        detail = m.GetString();
      return true;
    }
    catch
    {
      return false;
    }
  }

  /// <summary>Возвращает строку для UI по коду ошибки API.</summary>
  public static string GetUserMessage(TranslationApiErrorCode code) =>
      code switch
      {
        TranslationApiErrorCode.InvalidRequestEmptyText => Strings.TranslationApi_Error_InvalidRequestEmptyText,
        TranslationApiErrorCode.InvalidRequestMultipartExpected => Strings.TranslationApi_Error_InvalidRequestMultipartExpected,
        TranslationApiErrorCode.InvalidRequestNoFile => Strings.TranslationApi_Error_InvalidRequestNoFile,
        TranslationApiErrorCode.InvalidLanguageMissing => Strings.TranslationApi_Error_InvalidLanguageMissing,
        TranslationApiErrorCode.UpstreamError => Strings.TranslationApi_Error_UpstreamError,
        TranslationApiErrorCode.InternalError => Strings.TranslationApi_Error_InternalError,
        TranslationApiErrorCode.InternalErrorEmptyTranslation => Strings.TranslationApi_Error_InternalErrorEmptyTranslation,
        TranslationApiErrorCode.JobNotFound => Strings.TranslationApi_Error_JobNotFound,
        TranslationApiErrorCode.ResultExpired => Strings.TranslationApi_Error_ResultExpired,
        _ => Strings.TranslationApi_Error_Generic
      };
}
