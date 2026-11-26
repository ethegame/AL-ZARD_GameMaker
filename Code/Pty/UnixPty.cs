using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameMaker.Pty;

// 맥 터미널 프로세스 (1.6.0): libSystem 의 forkpty 로 의사 터미널 + 자식 하나, 자식은 바로 execve.
// - 출력: 전용 스레드가 master fd 를 read → UTF-8 증분 디코드 → Output (Windows PseudoConsoleProcess 와 같은 모양)
// - 입력: 전용 writer 스레드가 큐를 비우며 write
// - 크기: ioctl(TIOCSWINSZ). ioctl 은 가변 인자 함수라 Apple arm64 에선 세 번째 인자를 스택으로 넘겨야 함 → 빈 인자 6개로 레지스터를 채워 9번째(스택)에 둠
// - 종료: waitpid → reader 가 남은 출력을 다 읽고 master fd 를 닫음(fd 는 reader 만 닫는다) → Exited.
//   끄기 = 자식 프로세스 그룹에 SIGHUP → 잠깐(최대 0.5초) 기다림 → 그 자리에서 SIGKILL (forkpty 가 자식을 새 세션·그룹 대표로 만듦)
// fork 뒤 자식에선 관리 코드가 거의 돌면 안 됨 (런타임 잠금이 복제됨) → 자식이 쓸 글자·배열은 fork 전에 전부 네이티브 메모리로 만들고,
// 자식이 부를 함수(chdir·signal·sigprocmask·execve·_exit)는 fork 전에 한 번씩 해 없이 불러 호출 준비(스텁)를 끝내 두며,
// forkpty 와 자식 쪽 함수는 [SuppressGCTransition] 으로 GC 전환(=런타임 잠금 대기) 없이 부른다.
// 앱이 nohup·& 로 켜지면 SIGHUP·SIGINT 가 "무시" 로 물려 내려오므로, 자식에서 끊김·중단 신호를 기본값으로 되돌리고 신호 막음도 비운 뒤 exec 한다
public sealed class UnixPty : IPty
{
    private const string Lib = "libSystem.dylib";
    private const ulong TIOCSWINSZ = 0x80087467;   // _IOW('t', 103, struct winsize)
    private const int SIGHUP = 1;
    private const int SIGINT = 2;
    private const int SIGQUIT = 3;
    private const int SIGKILL = 9;
    private const int SIGPIPE = 13;
    private const int SIGTERM = 15;
    private const int SIG_BLOCK = 1;
    private const int SIG_SETMASK = 3;
    private const int SigSetBytes = 16;            // 맥 sigset_t = 4바이트 — 넉넉히
    private const int EINTR = 4;
    private static readonly IntPtr SigDefault = IntPtr.Zero;
    private static readonly IntPtr SigIgnore = new(1);
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReaderDrainWait = TimeSpan.FromSeconds(3);

    public event Action<string>? Output;
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    private int _master = -1;
    private readonly BlockingCollection<byte[]> _writeQueue = new();
    // fd 를 닫는 것(reader)과 쓰기·크기 바꾸기가 겹치지 않게 (닫힌 번호가 다른 파일에 다시 쓰이는 사고 방지)
    private readonly object _fdLock = new();
    private Thread? _readerThread;
    private int _closed;
    private int _disposed;
    private int _exited;

    // struct winsize — 줄이 먼저 (ws_row, ws_col, ws_xpixel, ws_ypixel)
    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row;
        public ushort Col;
        public ushort XPixel;
        public ushort YPixel;
    }

    // ---- fork 와 자식 쪽: GC 전환 없이 (포인터·정수만 — 마샬링 없음, SetLastError 없음 → 오류 번호는 __error) ----
    [DllImport(Lib), SuppressGCTransition]
    private static extern int forkpty(IntPtr master, IntPtr name, IntPtr termp, IntPtr winp);

    [DllImport(Lib), SuppressGCTransition]
    private static extern int execve(IntPtr path, IntPtr argv, IntPtr envp);

    [DllImport(Lib), SuppressGCTransition]
    private static extern int chdir(IntPtr path);

    [DllImport(Lib), SuppressGCTransition]
    private static extern IntPtr signal(int sig, IntPtr handler);

    [DllImport(Lib), SuppressGCTransition]
    private static extern int sigprocmask(int how, IntPtr set, IntPtr oldSet);

    [DllImport(Lib), SuppressGCTransition]
    private static extern void _exit(int code);

    // errno 자리 (SetLastError 대신)
    [DllImport(Lib, EntryPoint = "__error")]
    private static extern IntPtr ErrnoLocation();

    [DllImport(Lib, SetLastError = true)]
    private static extern nint read(int fd, [Out] byte[] buffer, nint count);

    [DllImport(Lib, SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nint count);

    [DllImport(Lib, SetLastError = true)]
    private static extern int close(int fd);

    [DllImport(Lib, SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport(Lib, SetLastError = true)]
    private static extern int kill(int pid, int sig);

    // x64: 가변 인자도 레지스터 — 보통 선언 그대로
    [DllImport(Lib, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlX64(int fd, nuint request, ref WinSize size);

    // arm64(Apple): 가변 인자는 스택 — 정해진 인자 레지스터 x0~x7 을 빈 값으로 채워 size 포인터가 스택 첫 칸에 가게
    [DllImport(Lib, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlArm64(int fd, nuint request, nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7, ref WinSize size);

    private static readonly object WarmLock = new();
    private static bool _isWarm;

    private UnixPty()
    {
    }

    public static UnixPty Start(string exePath, string arguments, string workDir, short cols, short rows,
        IReadOnlyCollection<string> removeEnvVars)
    {
        string path = ResolveExe(exePath);
        var argv = new List<string> { path };
        argv.AddRange(CommandLine.Split(arguments));
        List<string> env = BuildEnvironment(removeEnvVars);
        var pty = new UnixPty();
        pty.StartInternal(path, argv, env, workDir, cols, rows);
        return pty;
    }

    private void StartInternal(string path, List<string> argv, List<string> env, string workDir, short cols, short rows)
    {
        var allocations = new List<IntPtr>();
        try
        {
            IntPtr pathPtr = Utf8(path, allocations);
            IntPtr cwdPtr = Utf8(workDir, allocations);
            IntPtr argvPtr = StringArray(argv, allocations);
            IntPtr envPtr = StringArray(env, allocations);
            IntPtr emptyPtr = Utf8("", allocations);
            // 빈 신호 묶음 (0 으로 채운 네이티브 메모리)
            IntPtr emptySet = Marshal.AllocHGlobal(SigSetBytes);
            allocations.Add(emptySet);
            for (int i = 0; i < SigSetBytes; i++)
            {
                Marshal.WriteByte(emptySet, i, 0);
            }
            Warm(emptyPtr, argvPtr, envPtr, emptySet, allocations);

            var size = new WinSize { Row = (ushort)Math.Max((short)2, rows), Col = (ushort)Math.Max((short)2, cols) };
            int master = -1;
            int pid = ForkPty(ref master, ref size);
            if (0 == pid)
            {
                // ---- 자식: 네이티브 호출만 ----
                chdir(cwdPtr);
                // 물려받은 "무시"(nohup·& · .NET 의 SIGPIPE)를 기본으로 — exec 뒤에도 남으므로. 잡아 둔 처리기는 exec 가 알아서 기본으로 돌림
                signal(SIGHUP, SigDefault);
                signal(SIGINT, SigDefault);
                signal(SIGQUIT, SigDefault);
                signal(SIGTERM, SigDefault);
                signal(SIGPIPE, SigDefault);
                // 막아 둔 신호 없음으로 (fork 한 스레드의 막음이 물려 내려옴)
                sigprocmask(SIG_SETMASK, emptySet, IntPtr.Zero);
                execve(pathPtr, argvPtr, envPtr);
                _exit(127);
            }
            if (0 > pid)
            {
                throw new Win32Exception(LastErrno(), "forkpty 실패");
            }
            _master = master;
            ProcessId = pid;
        }
        finally
        {
            foreach (IntPtr allocation in allocations)
            {
                Marshal.FreeHGlobal(allocation);
            }
        }

        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{ProcessId}" };
        _readerThread.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = $"pty-write-{ProcessId}" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = $"pty-wait-{ProcessId}" }.Start();
    }

    // 받는 칸(master)·크기를 네이티브 메모리로 넘김 (마샬링 없이 — GC 전환을 빼려면 포인터만)
    private static int ForkPty(ref int master, ref WinSize size)
    {
        IntPtr masterPtr = Marshal.AllocHGlobal(sizeof(int));
        IntPtr sizePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinSize>());
        Marshal.WriteInt32(masterPtr, -1);
        Marshal.StructureToPtr(size, sizePtr, false);
        int pid = forkpty(masterPtr, IntPtr.Zero, IntPtr.Zero, sizePtr);
        if (0 == pid)
        {
            // 자식: 아무것도 풀지 않고 바로 돌아가서 exec (exec 가 메모리를 통째로 바꿈). try/finally 를 안 쓰는 까닭 = 자식에서 해제 호출이 돌지 않게
            return 0;
        }
        master = Marshal.ReadInt32(masterPtr);
        Marshal.FreeHGlobal(masterPtr);
        Marshal.FreeHGlobal(sizePtr);
        return pid;
    }

    private static int LastErrno()
    {
        return Marshal.ReadInt32(ErrnoLocation());
    }

    // 자식이 부를 함수의 호출 준비를 fork 전에 (해 없는 값으로 한 번씩)
    private static void Warm(IntPtr emptyPtr, IntPtr argvPtr, IntPtr envPtr, IntPtr emptySet, List<IntPtr> allocations)
    {
        lock (WarmLock)
        {
            if (true == _isWarm)
            {
                return;
            }
            // 빈 경로 = ENOENT 로 바로 실패 (아무것도 실행 안 됨)
            execve(emptyPtr, argvPtr, envPtr);
            // 지금 폴더로 chdir = 그대로
            chdir(Utf8(Environment.CurrentDirectory, allocations));
            // SIGPIPE 는 .NET 이 무시로 둔 값 — 읽고 그대로 되돌림 (다른 신호는 .NET 처리기를 건드리지 않게 부르지 않음 — 같은 함수라 준비는 이걸로 끝)
            IntPtr previous = signal(SIGPIPE, SigIgnore);
            signal(SIGPIPE, previous);
            // 빈 묶음을 "더 막기" = 바뀌는 것 없음
            sigprocmask(SIG_BLOCK, emptySet, IntPtr.Zero);
            _isWarm = true;
        }
    }

    private static IntPtr Utf8(string text, List<IntPtr> allocations)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        allocations.Add(ptr);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    // char*[] + 끝 NULL
    private static IntPtr StringArray(List<string> items, List<IntPtr> allocations)
    {
        IntPtr array = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
        allocations.Add(array);
        for (int i = 0; i < items.Count; i++)
        {
            Marshal.WriteIntPtr(array, i * IntPtr.Size, Utf8(items[i], allocations));
        }
        Marshal.WriteIntPtr(array, items.Count * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    // 경로가 아니면 PATH 에서 찾음 (execve 는 PATH 를 안 봄)
    private static string ResolveExe(string exePath)
    {
        if (true == exePath.Contains('/'))
        {
            if (false == File.Exists(exePath))
            {
                throw new FileNotFoundException($"실행 파일 없음: {exePath}");
            }
            return exePath;
        }
        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathValue.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, exePath);
            if (true == File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"PATH 에서 못 찾음: {exePath}");
    }

    // 부모 환경을 물려주되 지정 변수 제거 + 터미널 종류 (xterm.js 와 맞춤)
    private static List<string> BuildEnvironment(IReadOnlyCollection<string> removeEnvVars)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            if (removeEnvVars.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }
            vars[key] = (string?)e.Value ?? string.Empty;
        }
        vars["TERM"] = "xterm-256color";
        vars["COLORTERM"] = "truecolor";
        // Finder 로 켠 앱은 LANG 이 없어 한글이 깨질 수 있음
        if (false == vars.ContainsKey("LANG"))
        {
            vars["LANG"] = "en_US.UTF-8";
        }
        var list = new List<string>();
        foreach (KeyValuePair<string, string> pair in vars)
        {
            list.Add($"{pair.Key}={pair.Value}");
        }
        return list;
    }

    // master fd 를 닫는 건 여기 한 곳 — 읽을 게 끝나면(EIO = 터미널을 쥔 프로세스가 다 끝남) 닫음
    private void ReadLoop()
    {
        Decoder decoder = new UTF8Encoding(false).GetDecoder();
        byte[] buf = new byte[16 * 1024];
        char[] chars = new char[buf.Length + 4];
        try
        {
            while (true)
            {
                nint n = read(_master, buf, buf.Length);
                if (0 > n)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (EINTR == error)
                    {
                        continue;
                    }
                    // 자식이 끝나면 EIO — 정상 종료
                    break;
                }
                if (0 == n)
                {
                    break;
                }
                int c = decoder.GetChars(buf, 0, (int)n, chars, 0, flush: false);
                if (c > 0)
                {
                    Output?.Invoke(new string(chars, 0, c));
                }
            }
        }
        finally
        {
            CloseMaster();
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (byte[] data in _writeQueue.GetConsumingEnumerable())
            {
                WriteAll(data);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void WriteAll(byte[] data)
    {
        byte[] rest = data;
        while (0 < rest.Length)
        {
            nint n;
            lock (_fdLock)
            {
                if (0 != _closed)
                {
                    return;
                }
                n = write(_master, rest, rest.Length);
            }
            if (0 > n)
            {
                int error = Marshal.GetLastWin32Error();
                if (EINTR == error)
                {
                    continue;
                }
                return;
            }
            if (n >= rest.Length)
            {
                return;
            }
            rest = rest.AsSpan((int)n).ToArray();
        }
    }

    private void WaitLoop()
    {
        int code = -1;
        while (true)
        {
            int result = waitpid(ProcessId, out int status, 0);
            if (0 > result)
            {
                int error = Marshal.GetLastWin32Error();
                if (EINTR == error)
                {
                    continue;
                }
                break;
            }
            code = DecodeStatus(status);
            break;
        }
        Interlocked.Exchange(ref _exited, 1);
        // 남은 출력을 reader 가 다 흘려보내고 fd 를 닫을 때까지 기다림.
        // 손자 프로세스가 터미널을 쥐고 안 끝나면 그 그룹을 끝내서 reader 를 풀어 줌 (그래도 안 풀리면 fd 는 reader 가 풀릴 때 닫음)
        Thread? reader = _readerThread;
        if (null != reader)
        {
            if (false == reader.Join(ReaderDrainWait))
            {
                kill(-ProcessId, SIGKILL);
                reader.Join(ReaderDrainWait);
            }
        }
        Exited?.Invoke(code);
    }

    // WIFEXITED → 종료 코드, 신호로 죽음 → 128 + 신호 (셸과 같은 규칙)
    private static int DecodeStatus(int status)
    {
        int signalNumber = status & 0x7F;
        if (0 == signalNumber)
        {
            return (status >> 8) & 0xFF;
        }
        return 128 + signalNumber;
    }

    // reader 스레드만 부름
    private void CloseMaster()
    {
        lock (_fdLock)
        {
            if (0 != _closed)
            {
                return;
            }
            _closed = 1;
            if (0 <= _master)
            {
                close(_master);
            }
        }
        try
        {
            _writeQueue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Write(string text)
    {
        Write(Encoding.UTF8.GetBytes(text));
    }

    public void Write(byte[] data)
    {
        if (0 != _disposed)
        {
            return;
        }
        if (true == _writeQueue.IsAddingCompleted)
        {
            return;
        }
        try
        {
            _writeQueue.Add(data);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Resize(short cols, short rows)
    {
        const short MinSize = 2;
        if (0 != _closed)
        {
            return;
        }
        if (cols < MinSize)
        {
            return;
        }
        if (rows < MinSize)
        {
            return;
        }
        var size = new WinSize { Row = (ushort)rows, Col = (ushort)cols };
        lock (_fdLock)
        {
            if (0 != _closed)
            {
                return;
            }
            if (Architecture.Arm64 == RuntimeInformation.ProcessArchitecture)
            {
                IoctlArm64(_master, (nuint)TIOCSWINSZ, 0, 0, 0, 0, 0, 0, ref size);
                return;
            }
            IoctlX64(_master, (nuint)TIOCSWINSZ, ref size);
        }
    }

    // 자식 그룹 전체에 끊김 신호 → 잠깐 기다려 안 끝나면 그 자리에서 강제 종료 (앱이 곧 꺼져도 남지 않게 — 뒤 스레드에 맡기지 않음)
    public void Kill()
    {
        if (0 >= ProcessId)
        {
            return;
        }
        if (0 != _exited)
        {
            return;
        }
        kill(-ProcessId, SIGHUP);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < KillGrace)
        {
            if (0 != Volatile.Read(ref _exited))
            {
                return;
            }
            Thread.Sleep(20);
        }
        if (0 != Volatile.Read(ref _exited))
        {
            return;
        }
        kill(-ProcessId, SIGKILL);
    }

    public void Dispose()
    {
        if (0 != Interlocked.Exchange(ref _disposed, 1))
        {
            return;
        }
        Kill();
        try
        {
            _writeQueue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
        // master fd 는 reader 스레드가 읽기를 끝낸 뒤 닫는다 (읽는 중·쓰는 중에 닫지 않게)
    }
}
