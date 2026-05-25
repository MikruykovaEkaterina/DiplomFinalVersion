namespace BookReaderApp.Services;

/// <summary>Сообщения для отладки перевода: Debug Output в IDE, на Android — ещё и logcat (тег BookReaderTranslate).</summary>
public static class TranslationDiagnostics
{
  /// <summary>Префикс строк лога в IDE и на Android (<c>BookReaderTranslate</c> в logcat).</summary>
  public const string Tag = "[BookReader:Translate]";

  /// <summary>Записывает сообщение в отладочный вывод и на Android в logcat с тегом <c>BookReaderTranslate</c>.</summary>
  public static void Log(string message)
  {
    var line = $"{Tag} {message}";
    System.Diagnostics.Debug.WriteLine(line);
#if ANDROID
    global::Android.Util.Log.Info("BookReaderTranslate", line);
#endif
  }
}
