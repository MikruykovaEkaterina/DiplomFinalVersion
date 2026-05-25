#if ANDROID

namespace BookReaderApp.Platforms.Android;

/// <summary>Нижний отступ под системную панель / жестовую зону (dp).</summary>
public static class SystemInsetHelper
{
  public static double NavigationBarInsetDp()
  {
    try
    {
      var c = global::Android.App.Application.Context;
      var r = c?.Resources;
      if (r == null)
        return 48;
      int id = r.GetIdentifier("navigation_bar_height", "dimen", "android");
      if (id <= 0)
        return 48;
      int px = r.GetDimensionPixelSize(id);
      float d = r.DisplayMetrics?.Density ?? 1f;
      if (d <= 0)
        d = 1f;
      double dp = px / d;
      return dp < 1 ? 48 : dp;
    }
    catch
    {
      return 48;
    }
  }
}
#endif
