using BookReaderApp.Services;

namespace BookReaderApp
{
    /// <summary>Корневое приложение MAUI: тема/интерфейс из БД, окно со Shell, сохранение чтения при уходе в фон.</summary>
    public partial class App : Application
    {
        /// <summary>Инициализирует компоненты из XAML и запускает координатор темы/языка/шрифта из БД.</summary>
        public App()
        {
            InitializeComponent();
            InterfacePreferenceCoordinator.Start(this);
        }

        /// <summary>При возврате в приложение обновляет авто-тему и применяет настройки из БД.</summary>
        protected override void OnResume()
        {
            base.OnResume();
            InterfaceThemeManager.RefreshAutoIfNeeded(DateTime.Now);
            _ = InterfacePreferenceCoordinator.ApplyFromDatabaseAsync();
        }

        /// <summary>Создаёт главное окно с <see cref="AppShell"/>; при активации планирует возобновление фонового опроса перевода.</summary>
        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell());
            window.Activated += (_, _) =>
                BookTranslationBackgroundCoordinator.ScheduleResumePersistedJob(300);
            return window;
        }

        /// <summary>Перед уходом в фон пытается сохранить состояние активного чтения.</summary>
        protected override async void OnSleep()
        {
            try
            {
                await ReadingPage.TrySaveActiveReadingStateAsync();
            }
            catch
            {
                // ignore
            }
            base.OnSleep();
        }
    }
}
