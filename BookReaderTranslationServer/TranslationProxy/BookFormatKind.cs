namespace TranslationProxy;

/// <summary>Вариант входного файла для пайплайна перевода книги.</summary>
public enum BookFormatKind
{
  /// <summary>Формат не распознан или не поддерживается.</summary>
  Unknown,

  /// <summary>Плоский файл FictionBook (.fb2).</summary>
  Fb2,

  /// <summary>ZIP-архив с одним .fb2 внутри (.fb2.zip).</summary>
  Fb2Zip,

  /// <summary>Плоский EPUB (контейнер ZIP по спецификации).</summary>
  Epub,

  /// <summary>ZIP, содержащий один EPUB (.epub.zip).</summary>
  EpubZip
}
