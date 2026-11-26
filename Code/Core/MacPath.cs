using System.Diagnostics;
using System.IO;

namespace GameMaker.Core;

// 맥: Finder·Dock·nohup 으로 켠 앱은 PATH 가 /usr/bin:/bin 정도뿐이라 claude·python3·Godot 를 못 찾는다.
// 시작 때 로그인 셸(zsh -l)의 PATH 와 잘 알려진 설치 폴더를 합친다.
// Windows 는 예전처럼 아무것도 안 함
public static class MacPath
{
    private static readonly TimeSpan LoginShellTimeout = TimeSpan.FromSeconds(5);

    public static void RefreshProcessPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddUnique(parts, seen, LoginShellPath());
        AddUnique(parts, seen, Environment.GetEnvironmentVariable("PATH") ?? "");
        AddUnique(parts, seen, string.Join(Path.PathSeparator, KnownDirs()));
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, parts));
    }

    // claude · node · python3 가 흔히 깔리는 곳 (사용자 폴더 먼저)
    private static IEnumerable<string> KnownDirs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".dotnet"),
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin",
        };
    }

    // 로그인 셸이 보는 PATH (못 읽으면 "")
    private static string LoginShellPath()
    {
        string shell = Environment.GetEnvironmentVariable("SHELL") ?? "";
        if (0 == shell.Length)
        {
            shell = "/bin/zsh";
        }

        if (false == File.Exists(shell))
        {
            shell = "/bin/sh";
        }

        try
        {
            var info = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-l");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("printf '%s' \"$PATH\"");
            using Process? process = Process.Start(info);
            if (null == process)
            {
                return "";
            }

            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (false == process.WaitForExit(LoginShellTimeout))
            {
                process.Kill(true);
                return "";
            }

            // 셸 시작 파일이 찍은 글이 섞일 수 있어 마지막 줄만
            string text = output.Result.Trim();
            int lastBreak = text.LastIndexOf('\n');
            return 0 <= lastBreak ? text[(lastBreak + 1)..] : text;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"로그인 셸 PATH 읽기 실패: {ex.Message}");
            return "";
        }
    }

    private static void AddUnique(List<string> parts, HashSet<string> seen, string pathValue)
    {
        foreach (string part in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                parts.Add(trimmed);
            }
        }
    }
}
