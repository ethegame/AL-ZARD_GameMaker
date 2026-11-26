using System.Diagnostics;

namespace GameMaker.Services;

// 폴더·파일·주소를 OS 기본 프로그램으로 (Windows = 셸 실행·탐색기, 맥 = open).
// 예전 MainWindow 의 Process.Start(UseShellExecute) · explorer.exe /select 자리
public static class DesktopShell
{
    private const string MacOpen = "/usr/bin/open";

    // 폴더 = 탐색기·Finder, 주소 = 기본 브라우저
    public static void Open(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return;
        }

        RunOpen(target);
    }

    // 파일을 골라 둔 채 그 폴더 열기
    public static void Reveal(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start("explorer.exe", $"/select,\"{file}\"");
            return;
        }

        RunOpen("-R", file);
    }

    private static void RunOpen(params string[] args)
    {
        var info = new ProcessStartInfo(MacOpen) { UseShellExecute = false };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(info);
    }
}
