using Microsoft.Maui.Controls;

namespace BookReaderApp.Localization;

/// <summary>
/// Разметочное расширение XAML для строк интерфейса: <c>{loc:Translate ResourceKey}</c> задаёт одностороннюю привязку к
/// <see cref="LocalizationResourceManager.CurrentCulture"/> с <see cref="LocalizeConverter"/> и ключом <see cref="Key"/>.
/// </summary>
[ContentProperty(nameof(Key))]
public class TranslateExtension : IMarkupExtension<BindingBase>
{
  /// <summary>Ключ строки в <see cref="BookReaderApp.Resources.Strings"/>.</summary>
  public string Key { get; set; } = "";

  /// <summary>
  /// Строит <see cref="Binding"/> так, чтобы при смене языка свойство-слушатель обновило текст через <see cref="LocalizeConverter"/>.
  /// </summary>
  /// <returns>При пустом <see cref="Key"/> возвращает <see langword="null"/> для совместимости с генератором XAML.</returns>
  public BindingBase ProvideValue(IServiceProvider serviceProvider)
  {
    if (string.IsNullOrEmpty(Key))
      return null!;

    return new Binding
    {
      Mode = BindingMode.OneWay,
      Path = nameof(LocalizationResourceManager.CurrentCulture),
      Source = LocalizationResourceManager.Instance,
      Converter = LocalizeConverter.Instance,
      ConverterParameter = Key
    };
  }

  /// <inheritdoc cref="ProvideValue(IServiceProvider)"/>
  object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) =>
      ProvideValue(serviceProvider);
}
