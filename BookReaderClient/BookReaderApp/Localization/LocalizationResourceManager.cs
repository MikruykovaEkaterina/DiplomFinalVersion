using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Threading;
using BookReaderApp.Resources;

namespace BookReaderApp.Localization;

/// <summary>
/// Синглтон текущего языка UI: синхронизирует поток, <see cref="Strings"/> и уведомления для перевода разметки.
/// </summary>
public class LocalizationResourceManager : INotifyPropertyChanged
{
  /// <summary>Общий экземпляр для приложения.</summary>
  public static LocalizationResourceManager Instance { get; } = new();

  ResourceManager _resourceManager = Strings.ResourceManager;
  CultureInfo _currentCulture = CultureInfo.GetCultureInfo("ru");

  /// <summary>Уведомление об изменении <see cref="CurrentCulture"/> (и широкий <c>null</c> для переразбора связанных биндов).</summary>
  public event PropertyChangedEventHandler? PropertyChanged;

  /// <summary>
  /// Строка из <see cref="Strings"/> по ключу для текущей <see cref="CurrentCulture"/>.
  /// Для привязок в MAUI надёжнее схема <see cref="TranslateExtension"/> (<see cref="LocalizeConverter"/> на <see cref="CurrentCulture"/>).
  /// </summary>
  /// <param name="text">Ключ ресурса.</param>
  public string this[string text] => _resourceManager.GetString(text, _currentCulture);

  /// <summary>
  /// Активная культура интерфейса; при установке обновляет поток, ресурсы и вызывает <see cref="PropertyChanged"/>.
  /// </summary>
  public CultureInfo CurrentCulture
  {
    get => _currentCulture;
    set
    {
      ArgumentNullException.ThrowIfNull(value);
      if (_currentCulture?.Name == value.Name)
        return;
      _currentCulture = value;
      Thread.CurrentThread.CurrentUICulture = value;
      Thread.CurrentThread.CurrentCulture = value;
      Strings.Culture = value;
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
  }

  /// <summary>Задаёт начальную синхронизацию <see cref="Strings.Culture"/>.</summary>
  private LocalizationResourceManager()
  {
    Strings.Culture = _currentCulture;
  }
}
