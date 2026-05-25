namespace BookReaderApp.Models;

/// <summary>
/// Код ошибки JSON API TranslationProxy (поле <c>error</c>). Должен совпадать с <c>TranslationProxy.TranslationApiErrorCode</c>.
/// </summary>
public enum TranslationApiErrorCode
{
  InvalidRequestEmptyText,
  InvalidRequestMultipartExpected,
  InvalidRequestNoFile,
  InvalidLanguageMissing,
  UpstreamError,
  InternalError,
  InternalErrorEmptyTranslation,
  JobNotFound,
  ResultExpired
}
