using System.Globalization;
using BookReaderApp.Helpers;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Converters;

/// <summary>
/// Преобразует сырое значение языка из БД (ISO или устаревшая строка) в локализованную подпись <see cref="BookLanguage"/> для UI.
/// </summary>
/// <remarks>
/// Предполагается <see cref="MultiBinding"/>: первый источник — строка из хранилища; второй (например текущая культура интерфейса)
/// служит триггером пересчёта при смене языка приложения и может не использоваться в теле метода напрямую.
/// </remarks>
public sealed class BookLanguageIsoDisplayMultiConverter : IMultiValueConverter
{
  /// <summary>Возвращает локализованное имя языка по первой привязке (строка из БД).</summary>
  public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
  {
    var raw = values is { Length: > 0 } ? values[0] as string : null;
    if (values is { Length: > 1 })
      _ = values[1];

    var lang = BookLanguageStorage.FromStored(raw);
    return LocalizedEnumHelper.GetBookLanguageString(lang);
  }

  /// <summary>Не используется; обратное преобразование не поддерживается.</summary>
  /// <exception cref="NotSupportedException">Всегда.</exception>
  public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
      throw new NotSupportedException();
}
