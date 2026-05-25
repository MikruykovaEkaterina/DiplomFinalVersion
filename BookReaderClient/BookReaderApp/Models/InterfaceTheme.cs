namespace BookReaderApp.Models;

/// <summary>Сохранённые в БД ключи палитры оболочки (без строк UI — подписи в <see cref="Resources.Strings"/>).</summary>
public enum InterfaceTheme : byte
{
  Light = 0,
  Dark = 1,
  Auto = 2
}

/// <summary>Совместимость с прошлым TEXT в SQLite и русскими подписями.</summary>
public static class InterfaceThemeStored
{
  public static InterfaceTheme Clamp(InterfaceTheme raw) =>
      raw switch
      {
        InterfaceTheme.Dark => InterfaceTheme.Dark,
        InterfaceTheme.Auto => InterfaceTheme.Auto,
        _ => InterfaceTheme.Light
      };

  /// <summary>Нормализация числа из SQLite или приведённых значений перечисления.</summary>
  public static InterfaceTheme Clamp(int raw) =>
      raw switch
      {
        (int)InterfaceTheme.Dark => InterfaceTheme.Dark,
        (int)InterfaceTheme.Auto => InterfaceTheme.Auto,
        _ => InterfaceTheme.Light
      };

  /// <summary>Разбор сохранённого значения интерфейса (англ/RU) при миграции.</summary>
  public static InterfaceTheme ParseLegacy(object? stored)
  {
    if (stored is int i)
      return Clamp(i);
    if (stored is long l)
      return Clamp((int)l);
    var t = stored?.ToString()?.Trim();
    if (string.IsNullOrEmpty(t))
      return InterfaceTheme.Light;
    return t.ToUpperInvariant() switch
    {
      "DARK" => InterfaceTheme.Dark,
      "AUTO" => InterfaceTheme.Auto,
      "ТЁМАЯ" or "ТЁМНАЯ" or "ТЕМНАЯ" => InterfaceTheme.Dark,
      "АВТО" => InterfaceTheme.Auto,
      "СВЕТЛАЯ" => InterfaceTheme.Light,
      _ => InterfaceTheme.Light
    };
  }
}
