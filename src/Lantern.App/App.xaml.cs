using System.Windows;
namespace Lantern.App;
public partial class App : Application
{
    Mutex? instance;
    bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        // Smoke checks use isolated data and must not contend with the user's app.
        if (!e.Args.Contains("--smoke"))
        {
            var user = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
            instance = new Mutex(true, @"Local\CS2Lantern-" + user, out ownsInstance);
            if (!ownsInstance)
            {
                MessageBox.Show("启动器已经打开，请使用任务栏中的现有窗口。", "CS2 Lantern");
                Shutdown(); return;
            }
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose(); base.OnExit(e);
    }
}
