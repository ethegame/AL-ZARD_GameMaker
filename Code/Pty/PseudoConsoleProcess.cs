using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GameMaker.Pty;

// ConPTY 위에서 자식 프로세스 1개를 돌리는 래퍼.
// - 출력: 전용 스레드가 파이프를 읽고 UTF-8 Decoder 로 증분 디코드(멀티바이트가 read 경계에서 잘려도 안전) → Output 이벤트
// - 입력: 전용 writer 스레드가 큐를 비우며 파이프에 씀 (UI 스레드가 파이프 버퍼 가득 참으로 막히지 않게)
// - 종료: 프로세스 handle 대기 → ClosePseudoConsole → reader 드레인 → Exited 이벤트
// - 자식 트리는 Job Object(KILL_ON_JOB_CLOSE)에 묶여 Dispose/앱 크래시 시 함께 정리됨
public sealed class PseudoConsoleProcess : IPty
{
    // 백그라운드 스레드에서 호출됨
    public event Action<string>? Output;
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    private IntPtr _hPC;
    private IntPtr _hProcess;
    private IntPtr _hJob;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private readonly BlockingCollection<byte[]> _writeQueue = new();
    private Thread? _readerThread;
    private int _pcClosed;
    private int _disposed;

    private PseudoConsoleProcess()
    {
    }

    public static PseudoConsoleProcess Start(string exePath, string arguments, string workDir, short cols, short rows,
        IReadOnlyCollection<string> removeEnvVars)
    {
        var p = new PseudoConsoleProcess();
        try
        {
            p.StartInternal(exePath, arguments, workDir, cols, rows, removeEnvVars);
            return p;
        }
        catch
        {
            p.Dispose();
            throw;
        }
    }

    private void StartInternal(string exePath, string arguments, string workDir, short cols, short rows,
        IReadOnlyCollection<string> removeEnvVars)
    {
        if (!Native.CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) 실패");
        }
        if (!Native.CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) 실패");
        }

        var size = new Native.COORD { X = Math.Max((short)2, cols), Y = Math.Max((short)2, rows) };
        int hr = Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out _hPC);
        // ConPTY 가 자기 쪽 끝을 복제해 가지므로 우리 쪽 사본은 닫는다 (안 닫으면 EOF 가 오지 않음)
        inputRead.Dispose();
        outputWrite.Dispose();
        if (0 != hr)
        {
            throw new Win32Exception(hr, $"CreatePseudoConsole 실패 (HRESULT 0x{hr:X8})");
        }

        _inputStream = new FileStream(inputWrite, FileAccess.Write, 1);
        _outputStream = new FileStream(outputRead, FileAccess.Read, 1);

        _hJob = Native.CreateJobObject(IntPtr.Zero, null);
        if (IntPtr.Zero != _hJob)
        {
            var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            Native.SetInformationJobObject(_hJob, Native.JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        }

        IntPtr attrList = IntPtr.Zero;
        IntPtr cmdLine = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            IntPtr attrSize = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal(attrSize);
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
            {
                Marshal.FreeHGlobal(attrList);
                attrList = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList 실패");
            }
            // lpValue 에는 HPCON 값 자체를 넘긴다 (포인터가 아님)
            if (!Native.UpdateProcThreadAttribute(attrList, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute 실패");
            }

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            si.lpAttributeList = attrList;
            // 부모의 표준 핸들(리다이렉트된 경우)이 자식에 새어 들어가지 않도록 null 로 명시 → 의사 콘솔 핸들이 쓰임
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;

            string commandLine = $"\"{exePath}\"";
            if (false == string.IsNullOrWhiteSpace(arguments))
            {
                commandLine = $"\"{exePath}\" {arguments}";
            }
            cmdLine = Marshal.StringToHGlobalUni(commandLine); // CreateProcessW 는 쓰기 가능한 버퍼를 요구
            envBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(removeEnvVars));

            uint flags = Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_SUSPENDED;
            if (!Native.CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, workDir,
                    ref si, out Native.PROCESS_INFORMATION pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess 실패: {exePath}");
            }

            _hProcess = pi.hProcess;
            ProcessId = pi.dwProcessId;
            if (IntPtr.Zero != _hJob)
            {
                // 일시정지 상태에서 Job 에 넣어야 손자 프로세스까지 빠짐없이 묶인다
                Native.AssignProcessToJobObject(_hJob, _hProcess);
            }
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
        }
        finally
        {
            if (IntPtr.Zero != attrList)
            {
                Native.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (IntPtr.Zero != cmdLine)
            {
                Marshal.FreeHGlobal(cmdLine);
            }
            if (IntPtr.Zero != envBlock)
            {
                Marshal.FreeHGlobal(envBlock);
            }
        }

        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{ProcessId}" };
        _readerThread.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = $"pty-write-{ProcessId}" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = $"pty-wait-{ProcessId}" }.Start();
    }

    // 부모 환경을 상속하되 지정 변수 제거 (예: 중첩 Claude 세션 감지 변수)
    private static string BuildEnvironmentBlock(IReadOnlyCollection<string> removeEnvVars)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            if (removeEnvVars.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            vars[key] = (string?)e.Value ?? string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var kv in vars)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0'); // StringToHGlobalUni 가 하나 더 붙여 이중 null 종료
        return sb.ToString();
    }

    private void ReadLoop()
    {
        Decoder decoder = new UTF8Encoding(false).GetDecoder();
        byte[] buf = new byte[16 * 1024];
        char[] chars = new char[buf.Length + 4];
        try
        {
            while (true)
            {
                int n = _outputStream!.Read(buf, 0, buf.Length);
                if (n <= 0)
                {
                    break;
                }
                int c = decoder.GetChars(buf, 0, n, chars, 0, flush: false);
                if (c > 0)
                {
                    Output?.Invoke(new string(chars, 0, c));
                }
            }
        }
        catch (IOException)
        {
            // 파이프 끊김 = 정상 종료
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (byte[] data in _writeQueue.GetConsumingEnumerable())
            {
                _inputStream!.Write(data, 0, data.Length);
                _inputStream.Flush();
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WaitLoop()
    {
        Native.WaitForSingleObject(_hProcess, Native.INFINITE);
        Native.GetExitCodeProcess(_hProcess, out uint code);
        // ConPTY 를 닫아야 출력 파이프에 EOF 가 오고 reader 가 남은 출력을 다 흘려보낸 뒤 끝난다
        ClosePseudoConsoleOnce();
        _readerThread?.Join(3000);
        _outputStream?.Dispose();
        Native.CloseHandle(_hProcess);
        Exited?.Invoke(unchecked((int)code));
    }

    private void ClosePseudoConsoleOnce()
    {
        const int Closed = 1;
        int previous = Interlocked.Exchange(ref _pcClosed, Closed);
        if (0 != previous)
        {
            return;
        }
        if (IntPtr.Zero != _hPC)
        {
            Native.ClosePseudoConsole(_hPC);
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
        // 닫힌 콘솔·너무 작은 크기는 무시 (ConPTY 가 오류를 냄)
        const short MinSize = 2;
        if (0 != _pcClosed)
        {
            return;
        }
        if (IntPtr.Zero == _hPC)
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
        Native.ResizePseudoConsole(_hPC, new Native.COORD { X = cols, Y = rows });
    }

    public void Kill()
    {
        if (IntPtr.Zero != _hJob)
        {
            Native.TerminateJobObject(_hJob, 1);
        }
    }

    public void Dispose()
    {
        if (0 != Interlocked.Exchange(ref _disposed, 1))
        {
            return;
        }
        Kill();
        _writeQueue.CompleteAdding();
        ClosePseudoConsoleOnce();
        _inputStream?.Dispose();
        // 출력 파이프/프로세스 handle 은 wait 스레드가 프로세스 종료 후 닫는다 (Kill 로 곧 종료됨)
        if (IntPtr.Zero != _hJob)
        {
            Native.CloseHandle(_hJob);
            _hJob = IntPtr.Zero;
        }
    }
}
