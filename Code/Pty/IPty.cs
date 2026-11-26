namespace GameMaker.Pty;

// 터미널 프로세스 하나 (1.6.0 — Windows = ConPTY PseudoConsoleProcess · 맥 = forkpty UnixPty).
// 출력·종료 이벤트는 백그라운드 스레드에서 불린다 (부른 쪽이 Dispatcher 로 넘김)
public interface IPty : IDisposable
{
    event Action<string>? Output;
    event Action<int>? Exited;

    int ProcessId { get; }

    void Write(string text);
    void Write(byte[] data);
    void Resize(short cols, short rows);
    void Kill();
}

// OS 에 맞는 터미널 프로세스를 띄움. arguments = Windows 명령줄 규칙의 글자 하나 (DetachedLauncher.Quote 로 만든 것 — 맥은 CommandLine.Split 으로 다시 나눔)
public static class PtyLauncher
{
    public static IPty Start(string exePath, string arguments, string workDir, short cols, short rows,
        IReadOnlyCollection<string> removeEnvVars)
    {
        if (true == OperatingSystem.IsWindows())
        {
            return PseudoConsoleProcess.Start(exePath, arguments, workDir, cols, rows, removeEnvVars);
        }
        return UnixPty.Start(exePath, arguments, workDir, cols, rows, removeEnvVars);
    }
}
