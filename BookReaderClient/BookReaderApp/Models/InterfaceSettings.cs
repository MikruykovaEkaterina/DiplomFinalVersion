using SQLite;

namespace BookReaderApp.Models;

/// <summary>
/// Настройки оболочки: тема палитры, масштаб шрифта интерфейса, язык UI.
/// Одна строка в БД (<c>InterfaceSettings</c>, ключ <see cref="Id"/> = 1).
/// </summary>
[Table("InterfaceSettings")]
public class InterfaceSettings
{
  [PrimaryKey]
  public int Id { get; set; }

  /// <summary>Режим темы приложения (<see cref="InterfaceTheme"/>), в SQLite — число.</summary>
  public InterfaceTheme Theme { get; set; } = InterfaceTheme.Light;

  /// <summary>Кегль основного текста UI (pt). Допустимо 14–19; по умолчанию 16 — «средний» (оригинальные размеры иконок и пропорций).</summary>
  public int InterfaceFontSize { get; set; } = 16;

  /// <summary>Язык интерфейса: <c>ru</c> или <c>en</c>.</summary>
  public string InterfaceLanguage { get; set; } = "ru";
}
