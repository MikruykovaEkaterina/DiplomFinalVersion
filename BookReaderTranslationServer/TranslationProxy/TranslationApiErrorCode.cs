namespace TranslationProxy;

/// <summary>
/// Код ошибки API прокси (<c>error</c> в JSON). Текст для пользователя формирует клиент по культуре; опционально <see cref="TranslationApiErrorDto.Detail"/> для диагностики.
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
