namespace BookReaderApp.Models;

/// <summary>
/// Итог загрузки или импорта книги через <see cref="Services.IBookUploadService"/> —
/// успех, идентификатор сохранённой карточки или текст ошибки.
/// </summary>
public class UploadResult
{
  /// <summary>Удалось ли сохранить или импортировать книгу.</summary>
  public bool Success { get; set; }

  /// <summary>Идентификатор записи в таблице <c>Cards</c> при успехе.</summary>
  public int CardId { get; set; }

  /// <summary>Сообщение об ошибке для пользователя (при неуспешном результате).</summary>
  public string ErrorMessage { get; set; } = "";
}
