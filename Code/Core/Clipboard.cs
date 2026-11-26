using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameMaker.Core;

// 클립보드 글 넣기·읽기 (1.6.0 — WPF Clipboard 대신, 같은 이름). Windows = Win32 클립보드 API, 맥 = pbcopy · pbpaste · osascript.
// 실패하면 예외 대신 조용히 빈 값 (복사 하나 때문에 앱이 멈추지 않게) — SetText 만 실패를 예외로 알림 (부른 쪽이 "복사 실패" 를 보여 줌)
public static class Clipboard
{
    private const uint CfUnicodeText = 13;
    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;
    private const int OpenRetries = 10;
    private const int MacTimeoutMs = 3000;

    public static void SetText(string text)
    {
        if (true == OperatingSystem.IsWindows())
        {
            SetTextWindows(text);
            return;
        }
        if (true == OperatingSystem.IsMacOS())
        {
            RunMac("/usr/bin/pbcopy", Array.Empty<string>(), text);
            return;
        }
        throw new PlatformNotSupportedException("클립보드 — Windows·맥만");
    }

    public static string GetText()
    {
        try
        {
            if (true == OperatingSystem.IsWindows())
            {
                return GetTextWindows();
            }
            if (true == OperatingSystem.IsMacOS())
            {
                return RunMac("/usr/bin/pbpaste", Array.Empty<string>(), null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"클립보드 읽기 실패: {ex.Message}");
        }
        return "";
    }

    public static bool ContainsText()
    {
        if (true == OperatingSystem.IsWindows())
        {
            return IsClipboardFormatAvailable(CfUnicodeText);
        }
        string text = GetText();
        return 0 < text.Length;
    }

    public static bool ContainsImage()
    {
        try
        {
            if (true == OperatingSystem.IsWindows())
            {
                if (true == IsClipboardFormatAvailable(CfDib))
                {
                    return true;
                }
                if (true == IsClipboardFormatAvailable(CfDibV5))
                {
                    return true;
                }
                return IsClipboardFormatAvailable(CfBitmap);
            }
            if (true == OperatingSystem.IsMacOS())
            {
                // clipboard info = 들어 있는 형식 목록 («class PNGf», TIFF picture …)
                string info = RunMac("/usr/bin/osascript", new[] { "-e", "clipboard info" }, null);
                bool hasPng = info.Contains("PNGf", StringComparison.Ordinal);
                bool hasTiff = info.Contains("TIFF", StringComparison.Ordinal);
                if (true == hasPng)
                {
                    return true;
                }
                return hasTiff;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"클립보드 그림 확인 실패: {ex.Message}");
        }
        return false;
    }

    // ---------------- Windows ----------------

    private static void SetTextWindows(string text)
    {
        OpenWindows();
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            IntPtr handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (IntPtr.Zero == handle)
            {
                throw new InvalidOperationException("클립보드 메모리를 못 잡음");
            }
            IntPtr target = GlobalLock(handle);
            if (IntPtr.Zero == target)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("클립보드 메모리를 못 잠금");
            }
            char[] chars = (text + "\0").ToCharArray();
            Marshal.Copy(chars, 0, target, chars.Length);
            GlobalUnlock(handle);
            IntPtr result = SetClipboardData(CfUnicodeText, handle);
            if (IntPtr.Zero == result)
            {
                // 넘기지 못했으면 메모리는 아직 내 것
                GlobalFree(handle);
                throw new InvalidOperationException("클립보드에 넣지 못함");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string GetTextWindows()
    {
        if (false == IsClipboardFormatAvailable(CfUnicodeText))
        {
            return "";
        }
        OpenWindows();
        try
        {
            IntPtr handle = GetClipboardData(CfUnicodeText);
            if (IntPtr.Zero == handle)
            {
                return "";
            }
            IntPtr source = GlobalLock(handle);
            if (IntPtr.Zero == source)
            {
                return "";
            }
            try
            {
                string text = Marshal.PtrToStringUni(source) ?? "";
                return text;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    // 다른 프로그램이 잠깐 잡고 있으면 조금 기다렸다 다시
    private static void OpenWindows()
    {
        for (int i = 0; i < OpenRetries; i++)
        {
            bool isOpen = OpenClipboard(IntPtr.Zero);
            if (true == isOpen)
            {
                return;
            }
            Thread.Sleep(20);
        }
        throw new InvalidOperationException("클립보드를 다른 프로그램이 쓰는 중");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    // ---------------- 맥 ----------------

    // input 이 있으면 표준 입력으로 보내고, 출력을 돌려줌. LANG 을 UTF-8 로 (안 주면 pbcopy 가 한글을 깨뜨림)
    private static string RunMac(string exe, string[] args, string? input)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = null != input,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (null != input)
        {
            info.StandardInputEncoding = new UTF8Encoding(false);
        }
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment["LANG"] = "en_US.UTF-8";
        info.Environment["LC_CTYPE"] = "UTF-8";
        using Process? process = Process.Start(info);
        if (null == process)
        {
            throw new InvalidOperationException($"{exe} 실행 실패");
        }
        if (null != input)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        bool isExited = process.WaitForExit(MacTimeoutMs);
        if (false == isExited)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            throw new TimeoutException($"{exe} 응답 없음");
        }
        return output.Result;
    }
}
