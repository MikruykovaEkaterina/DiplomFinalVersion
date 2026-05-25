using System;
using System.Collections.Generic;
using System.Linq;
using BookReaderApp.Helpers;
using BookReaderApp.Models;
using BookReaderApp.ViewModels;

namespace BookReaderApp.Helpers
{
  /// <summary>
  /// Фильтрация и сортировка групп книг каталога (группа = одна работа <c>Work</c>, внутри несколько языковых <see cref="BookInfoViewModel"/>).
  /// </summary>
  public static class BookGroupListQuery
  {
    /// <summary>
    /// Возвращает <b>новые</b> группы с урезанным <see cref="BookGroupViewModel.LanguageVersions"/>:
    /// учитываются только версии, подходящие под выбранные язык, название, автора и диапазон страниц.
    /// Экземпляры <see cref="BookInfoViewModel"/> те же, что в мастер-списке (обновления статуса/реакции сохраняются).
    /// </summary>
    public static IEnumerable<BookGroupViewModel> Filter(
      IEnumerable<BookGroupViewModel> groups,
      SearchFilterViewModel f)
    {
      foreach (var g in groups)
      {
        if (!MatchesStatus(g, f)) continue;
        if (!MatchesReaction(g, f)) continue;

        var narrowed = NarrowLanguageVersions(g, f).ToList();
        if (narrowed.Count == 0) continue;

        var view = new BookGroupViewModel { WorkId = g.WorkId };
        foreach (var v in narrowed)
          view.LanguageVersions.Add(v);
        yield return view;
      }
    }

    /// <summary>Сортирует группы по выбранному <see cref="BookSort"/> (заголовок, дата добавления, число страниц).</summary>
    public static IEnumerable<BookGroupViewModel> Sort(IEnumerable<BookGroupViewModel> groups, BookSort sort)
    {
      var list = groups.ToList();
      BookInfoViewModel? Rep(BookGroupViewModel g) =>
        g.LanguageVersions.Count > 0 ? g.LanguageVersions[0] : null;

      DateTime MinAdded(BookGroupViewModel g) =>
        g.LanguageVersions.Count == 0
          ? DateTime.MinValue
          : g.LanguageVersions.Min(v => v.DateAdded);

      IEnumerable<BookGroupViewModel> ordered = sort switch
      {
        BookSort.TitleAsc => list.OrderBy(g => Rep(g)?.Title ?? "", StringComparer.CurrentCultureIgnoreCase),
        BookSort.TitleDesc => list.OrderByDescending(g => Rep(g)?.Title ?? "", StringComparer.CurrentCultureIgnoreCase),
        BookSort.DateNew => list.OrderByDescending(MinAdded),
        BookSort.DateOld => list.OrderBy(MinAdded),
        BookSort.SizeAsc => list.OrderBy(g => Rep(g)?.Pages ?? 0),
        BookSort.SizeDesc => list.OrderByDescending(g => Rep(g)?.Pages ?? 0),
        _ => list.OrderByDescending(MinAdded)
      };
      return ordered;
    }

    /// <summary>Оставляет только карточки, согласованные со всеми заданными полями фильтра.</summary>
    static IEnumerable<BookInfoViewModel> NarrowLanguageVersions(BookGroupViewModel g, SearchFilterViewModel f)
    {
      IEnumerable<BookInfoViewModel> q = g.LanguageVersions;

      if (f.AppliedLanguage != BookLanguage.None)
      {
        q = q.Where(v => BookLanguageStorage.FromStored(v.Language) == f.AppliedLanguage);
      }

      var titleQ = (f.AppliedBookTitle ?? "").Trim();
      if (titleQ.Length > 0)
      {
        q = q.Where(v =>
          (v.Title ?? "").IndexOf(titleQ, StringComparison.OrdinalIgnoreCase) >= 0);
      }

      var authorQ = (f.AppliedBookAuthor ?? "").Trim();
      if (authorQ.Length > 0)
      {
        q = q.Where(v =>
          (v.Author ?? "").IndexOf(authorQ, StringComparison.OrdinalIgnoreCase) >= 0);
      }

      var fromT = (f.AppliedBookPageFrom ?? "").Trim();
      var toT = (f.AppliedBookPageTo ?? "").Trim();
      if (fromT.Length > 0 || toT.Length > 0)
      {
        int minP = 0;
        if (fromT.Length > 0)
        {
          if (!int.TryParse(fromT, out var fp) || fp < 1)
          {
            // как раньше в MatchesPages: некорректное «от» не отсекает по минимуму
          }
          else
            minP = fp;
        }

        int? maxP = null;
        if (toT.Length > 0 && int.TryParse(toT, out var tp))
          maxP = tp;

        q = q.Where(v =>
        {
          int p = v.Pages;
          if (p < minP) return false;
          if (maxP.HasValue && p > maxP.Value) return false;
          return true;
        });
      }

      return q;
    }

    /// <summary>Сопоставляет фильтр статуса чтения с представителем группы (первая карточка в списке языков).</summary>
    static bool MatchesStatus(BookGroupViewModel g, SearchFilterViewModel f)
    {
      if (f.AppliedStatus == BookStatus.None) return true;
      if (g.LanguageVersions.Count == 0) return false;
      var st = g.LanguageVersions[0].ReadingStatus;
      return f.AppliedStatus switch
      {
        BookStatus.New => st == BookStatus.New,
        BookStatus.InProgress => st == BookStatus.InProgress,
        BookStatus.Read => st == BookStatus.Read,
        _ => true
      };
    }

    /// <summary>Сопоставляет фильтр реакции с представителем группы (первая карточка).</summary>
    static bool MatchesReaction(BookGroupViewModel g, SearchFilterViewModel f)
    {
      if (f.AppliedReaction == BookReaction.None) return true;
      if (g.LanguageVersions.Count == 0) return false;
      var reaction = g.LanguageVersions[0].Reaction;
      return f.AppliedReaction switch
      {
        BookReaction.Favorite => reaction == BookReaction.Favorite,
        BookReaction.Unrated => reaction == BookReaction.Unrated,
        _ => true
      };
    }
  }
}
