using System.Globalization;
using BookReaderApp.Resources;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Localization;

/// <summary>
/// Достаёт строку из <see cref="Strings.ResourceManager"/> по ключу из <c>ConverterParameter</c>; при изменении входа (культура через биндинг)
/// текст в UI пересчитывается — так обновляется локализация после смены <see cref="LocalizationResourceManager.CurrentCulture"/>.
/// </summary>
public sealed class LocalizeConverter : IValueConverter
{
  /// <summary>Совместное использование с <see cref="TranslateExtension"/>.</summary>
  public static LocalizeConverter Instance { get; } = new();

  /// <summary>Возвращает локализованную строку или сам ключ при отсутствии перевода.</summary>
  /// <param name="value">Обычно значение свойства источника (культура); не используется, берётся <see cref="LocalizationResourceManager.Instance"/>.</param>
  /// <param name="parameter">Ключ ресурса <see cref="Strings"/>.</param>
  public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
  {
    if (parameter is not string key || string.IsNullOrEmpty(key))
      return "";

    var c = LocalizationResourceManager.Instance.CurrentCulture;
    return Strings.ResourceManager.GetString(key, c) ?? key;
  }

  /// <summary>Обратное преобразование не поддерживается.</summary>
  /// <exception cref="NotSupportedException">Всегда.</exception>
  public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
      throw new NotSupportedException();
}
