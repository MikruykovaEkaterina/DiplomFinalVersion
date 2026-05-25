using System.Globalization;
using BookReaderApp.Helpers;
using BookReaderApp.Models;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Converters;

/// <summary>
/// Локализованная подпись выбранного элемента <see cref="Picker"/> для enum-полей (язык, статус, реакция, сортировка);
/// второй параметр привязки обычно культура/смена языка интерфейса, чтобы текст обновлялся без смены выбора.
/// </summary>
/// <remarks>
/// В <see cref="Convert"/> параметр <c>parameter</c> (строка): <c>Language</c>, <c>Status</c>, <c>Reaction</c>, <c>Sort</c> —
/// маршрут к соответствующему методу в <see cref="LocalizedEnumHelper"/>.
/// </remarks>
public sealed class PickerEnumDisplayMultiConverter : IMultiValueConverter
{
  /// <summary>Возвращает локализованную строку для выбранного enum в зависимости от строкового <paramref name="parameter"/>.</summary>
  public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
  {
    var item = values is { Length: > 0 } ? values[0] : null;
    if (values is { Length: > 1 })
      _ = values[1];

    return parameter as string switch
    {
      "Language" => item is BookLanguage lang ? LocalizedEnumHelper.GetBookLanguageString(lang) : "",
      "Status" => item is BookStatus st ? LocalizedEnumHelper.GetBookStatusString(st) : "",
      "Reaction" => item is BookReaction r ? LocalizedEnumHelper.GetBookReactionString(r) : "",
      "Sort" => item is BookSort so ? LocalizedEnumHelper.GetBookSortString(so) : "",
      _ => item?.ToString() ?? ""
    };
  }

  /// <summary>Не используется; обратное преобразование не поддерживается.</summary>
  /// <exception cref="NotSupportedException">Всегда.</exception>
  public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
      throw new NotSupportedException();
}
