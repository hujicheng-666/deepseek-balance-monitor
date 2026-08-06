using System;
using System.IO;
using System.Windows;

namespace DeepSeekMonitor;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.Handled = true; // 记录后继续运行，避免静默闪退
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogCrash(ex);
        };
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
