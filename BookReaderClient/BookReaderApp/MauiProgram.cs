using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using BookReaderApp;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using SQLitePCL;

namespace BookReaderApp
{
  public static class MauiProgram
  {
    public static MauiApp CreateMauiApp()
    {
      // Инициализация SQLitePCLRaw (bundle_green)
      Batteries_V2.Init();

      var builder = MauiApp.CreateBuilder();
      builder
        .UseMauiApp<App>()
        .UseMauiCommunityToolkit(options => options.SetShouldEnableSnackbarOnWindows(true))
        .ConfigureFonts(_ => { });

      // Регистрация сервисов
      builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
      builder.Services.AddSingleton<IFilePickerService, FilePickerService>();
      builder.Services.AddSingleton<IBookParserService, BookParserService>();
      builder.Services.AddSingleton<IBookUploadService, BookUploadService>();
      builder.Services.AddTransient<UploadViewModel>();
      builder.Services.AddSingleton<IAppNotificationService, AppNotificationService>();
      builder.Services.AddSingleton<BookTranslationApiClient>();
      builder.Services.AddSingleton<ISentenceTranslationService, SentenceTranslationService>();

#if DEBUG
      builder.Logging.AddDebug();
#endif

#if ANDROID
      AndroidWebViewWorkaround.Register();
#endif

      var app = builder.Build();
      ServiceLocator.Init(app.Services);
      BookTranslationBackgroundCoordinator.EnsureTranslationPollConnectivityHook();
      return app;
    }
  }
}
