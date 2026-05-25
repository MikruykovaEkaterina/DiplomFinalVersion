namespace BookReaderApp
{
    /// <summary>Оболочка навигации Shell: главная страница из XAML и зарегистрированные маршруты Upload, Settings, Translation.</summary>
    public partial class AppShell : Shell
    {
        /// <summary>Регистрирует именованные маршруты для переходов через <see cref="Shell"/>.</summary>
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute(nameof(UploadPage), typeof(UploadPage));
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
            Routing.RegisterRoute(nameof(TranslationPage), typeof(TranslationPage));
        }
    }
}
