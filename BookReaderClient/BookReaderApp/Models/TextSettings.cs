using BookReaderApp.Helpers;
using SQLite;

namespace BookReaderApp.Models;

/// <summary>
/// Одна строка настроек оформления текста книги (<c>TextSettings</c>, ключ <see cref="Id"/> обычно <c>1</c>).
/// Отображение в UI см. ключи из <see cref="TextSettingsStoredFormats"/>.
/// </summary>
[Table("TextSettings")]
public class TextSettings
{
  [PrimaryKey]
  public int Id { get; set; }

  /// <summary>Кегль основного текста книги.</summary>
  public int FontSize { get; set; } = 16;

  /// <summary>Цвет шрифта (шестнадцатеричный или совместимый с MAUI строковый вид).</summary>
  public string TextColor { get; set; } = "#000000";

  /// <summary>Цвет подложки текста книги.</summary>
  public string BackgroundColor { get; set; } = "#FFFFFF";

  /// <summary>Поле содержимого в логических пикселях.</summary>
  public int Margins { get; set; } = 16;

  /// <summary>
  /// Ключ выравнивания абзацев: <see cref="TextSettingsStoredFormats.AlignmentJustify"/> и др.
  /// </summary>
  public string TextAlignment { get; set; } = TextSettingsStoredFormats.AlignmentJustify;

  /// <summary>
  /// Ключ режима навигации: вертикальный скролл или горизонтальное листание.
  /// </summary>
  public string ScrollingMode { get; set; } = TextSettingsStoredFormats.ScrollingVertical;

  /// <summary>Языковой код перевода онлайн-сервисом для этого профиля (например <c>ru</c>).</summary>
  public string TranslationLanguage { get; set; } = "ru";
}
