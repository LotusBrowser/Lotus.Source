using System.Windows;

namespace TerminalOS
{
    public partial class App : Application
    {
        private SingleInstanceManager _instanceManager;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Windows passes the clicked link as a plain command-line argument
            // when it launches Lotus because it's the registered default
            // browser, e.g.:  Lotus.exe "https://example.com/article"
            string startupUrl = e.Args.Length > 0 ? e.Args[0] : null;

            _instanceManager = new SingleInstanceManager();

            if (!_instanceManager.IsFirstInstance)
            {
                // Another Lotus window is already open - hand the URL off to it
                // (if there was one) and close this duplicate process instead of
                // opening a second window.
                if (!string.IsNullOrWhiteSpace(startupUrl))
                {
                    SingleInstanceManager.ForwardUrlToRunningInstance(startupUrl);
                }

                Shutdown();
                return;
            }

            var mainWindow = new MainWindow(startupUrl);

            // Any later launch (another link click while Lotus is already
            // running) forwards its URL here over the named pipe.
            _instanceManager.StartListening(url =>
            {
                Dispatcher.Invoke(() => mainWindow.OpenUrlFromExternal(url));
            });

            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _instanceManager?.Dispose();
            base.OnExit(e);
        }
    }
}
