using System.Runtime.InteropServices;

namespace KeyPassXCCloudSync;

internal static class WindowsStartupReporter
{
    [DllImport("user32.dll",EntryPoint="MessageBoxW",CharSet=CharSet.Unicode)]
    private static extern int MessageBox(nint hwnd,string message,string caption,uint type);
    internal static void ConfigureLanguage(string configPath)
    {
        try
        {
            if(!File.Exists(configPath))return;
            var inUi=false;
            foreach(var raw in File.ReadLines(configPath))
            {
                var line=raw.Trim();
                if(line.StartsWith('['))
                    inUi=line.Equals("[UI]",StringComparison.OrdinalIgnoreCase);
                else if(inUi && line.StartsWith("Language",StringComparison.OrdinalIgnoreCase))
                {
                    var separator=line.IndexOf('=');
                    if(separator<0 || !line[..separator].Trim().Equals("Language",StringComparison.OrdinalIgnoreCase))continue;
                    UiLang.Use(line[(separator+1)..].Trim());
                    break;
                }
            }
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    internal static void ExplainExistingInstance()
    {
        // Typical when the old build remains in the notification area after an
        // update. The new process would otherwise appear to launch and vanish.
        var message=UiLang.T("KeePassXC Cloud Sync уже запущен с этой конфигурацией.\n\n"+
            "Новая копия не запускается, чтобы два процесса не изменяли одну базу одновременно.\n\n"+
            "Завершите предыдущую копию через значок в трее (Выход), " +
            "после этого снова запустите новый EXE.\n\n"+
            "Синхронизация и данные не удаляются.");
        try{MessageBox(0,message,UiLang.T("KeePassXC Cloud Sync — экземпляр уже работает"),0x30);}
        catch(Exception){}
    }
    internal static void RecoverableTaskbarError(Exception ex)
    {
        try
        {
            var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KeyPassXCCloudSync","startup-errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+
                " Taskbar icon (non-fatal): "+ex+Environment.NewLine);
        }
        catch(Exception){}
    }
    internal static void Report(Exception ex)
    {
        string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyPassXCCloudSync","startup-errors.log");
        try
        {
            if(PlatformServices.Current.Paths.StartupLogPath is { } configured)path=configured;
        }
        catch(InvalidOperationException){}
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+ex+Environment.NewLine+Environment.NewLine);
        }
        catch(Exception){} // startup report must never conceal the original failure
        try{MessageBox(0,UiLang.T("KeyPassXC Cloud Sync не удалось запустить.")+"\n"+
            UiErrorDisplay.Present(ex)+"\n\n"+UiLang.T("Подробности: ")+path,
            UiLang.T("KeyPassXC Cloud Sync — ошибка запуска"),0x10);}
        catch(Exception){}
    }
}
