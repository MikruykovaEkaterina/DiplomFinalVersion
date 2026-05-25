using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BookReaderApp.Resources;
using BookReaderApp.Services;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Helpers;

/// <summary>
/// Показ тематического списка вариантов (action sheet): выбор enum или ключа настроек без отдельного модального окна.
/// </summary>
public static class ThemedEnumPickSheet
{
  /// <summary>
  /// Находит <see cref="ContentPage"/> снизу вверх по визуальному дереву либо берёт текущую страницу Shell — хост для оверлея.
  /// </summary>
  public static ContentPage? FindHostContentPage(Element? start)
  {
    for (Element? p = start; p != null; p = p.Parent)
    {
      if (p is ContentPage cp)
        return cp;
    }
    return Shell.Current?.CurrentPage as ContentPage;
  }

  /// <summary>Показывает лист подписей; при выборе вызывает <paramref name="applySelection"/> для соответствующего элемента <paramref name="items"/>.</summary>
  /// <typeparam name="T">Тип элемента списка (enum, строка-ключ и т.д.).</typeparam>
  /// <param name="visualSender">Точка в дереве UI для поиска хост-страницы.</param>
  /// <param name="toLabel">Текст строки списка для пользователя.</param>
  /// <param name="applySelection">Вызывается на главном потоке после подтверждения пункта.</param>
  public static async Task PickAsync<T>(
      Element? visualSender,
      IReadOnlyList<T> items,
      Func<T, string> toLabel,
      Action<T> applySelection,
      string sheetTitle)
  {
    var host = FindHostContentPage(visualSender);
    if (host == null || items.Count == 0)
      return;

    var labels = items.Select(toLabel).ToList();
    var pick = await ThemedOverlayPresenter.ShowActionSheetAsync(
        host, sheetTitle, Strings.Common_Cancel, labels).ConfigureAwait(true);
    if (pick == null)
      return;
    int idx = labels.FindIndex(s => s == pick);
    if (idx >= 0 && idx < items.Count)
      applySelection(items[idx]);
  }
}
