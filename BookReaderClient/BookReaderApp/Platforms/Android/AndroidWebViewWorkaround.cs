#if ANDROID
using Microsoft.Maui.Handlers;

namespace BookReaderApp;

/// <summary>
/// Включает JS/DOM и доступ к файлам — без этого HtmlWebViewSource на Android часто пустой.
/// </summary>
public static class AndroidWebViewWorkaround
{
  public static void Register()
  {
    WebViewHandler.Mapper.AppendToMapping("BookReaderWebView", (handler, view) =>
    {
      if (handler.PlatformView is not Android.Webkit.WebView wv)
        return;
      wv.Settings.JavaScriptEnabled = true;
      wv.Settings.DomStorageEnabled = true;
      wv.Settings.AllowFileAccess = true;
      wv.Settings.AllowFileAccessFromFileURLs = true;
      wv.Settings.AllowUniversalAccessFromFileURLs = true;
      wv.Settings.SetSupportZoom(false);
    });
  }
}
#endif
