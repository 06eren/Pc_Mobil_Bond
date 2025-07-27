namespace TelefonSunucu
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // MainPage = new AppShell(); satırını silin ve bunu ekleyin:
            MainPage = new NavigationPage(new MainPage());
        }
    }
}
