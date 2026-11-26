using System.Diagnostics;
using System.IO;

namespace GameMaker.Services;

// Claude 세션을 auto 모드(--permission-mode auto)로 띄울지 (1.1.5 — 만든 사람 "클로드 자동으로 그냥 auto mode on").
// auto = 위험해 보이는 것만 Claude 가 스스로 막고 나머지는 묻지 않고 진행 — 단계마다 뜨는 승인창을 줄임.
// 옛 Claude Code 는 auto 를 몰라서 붙이면 켜지자마자 꺼진다 → 그 claude 의 --help 에 "auto" 가 있을 때만 붙임.
// 실행 파일(경로·고친 시각)마다 한 번만 물어 기억 (Claude 가 업데이트되면 다시 봄)
public static class ClaudeAutoMode
{
    private const string ModeArgs = "--permission-mode auto";
    private static readonly TimeSpan HelpTimeout = TimeSpan.FromSeconds(8);
    private static readonly object Gate = new();
    private static string _checkedKey = "";
    private static bool _supported;

    // 붙일 인자 ("" = 안 붙임)
    public static string ArgsFor(string exe)
    {
        bool supported = Supports(exe);
        if (false == supported)
        {
            return "";
        }
        return ModeArgs;
    }

    private static bool Supports(string exe)
    {
        if (0 == exe.Length)
        {
            return false;
        }
        if (false == File.Exists(exe))
        {
            return false;
        }
        string key = $"{exe}|{File.GetLastWriteTimeUtc(exe).Ticks}";
        lock (Gate)
        {
            if (key == _checkedKey)
            {
                return _supported;
            }
            _supported = HelpListsAuto(exe);
            _checkedKey = key;
            return _supported;
        }
    }

    // claude --help 의 --permission-mode 고르기에 "auto" 가 있나
    private static bool HelpListsAuto(string exe)
    {
        try
        {
            var info = new ProcessStartInfo(exe, "--help")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.Environment.Remove("CLAUDECODE");
            info.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
            using Process? process = Process.Start(info);
            if (null == process)
            {
                return false;
            }
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit((int)HelpTimeout.TotalMilliseconds);
            if (false == exited)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            string help = output.Result;
            int at = help.IndexOf("--permission-mode", StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }
            // 고르기 목록은 옵션 이름 뒤 몇 줄 안 (choices: "acceptEdits", "auto", …)
            int length = Math.Min(400, help.Length - at);
            string near = help.Substring(at, length);
            return near.Contains("\"auto\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine($"claude --help 실패: {ex.Message}");
            return false;
        }
    }
}
