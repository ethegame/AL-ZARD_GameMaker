using System.Text;

namespace GameMaker.Pty;

// Windows 명령줄 규칙 (CommandLineToArgvW: 공백·따옴표·끝 백슬래시).
// Quote = 인자 하나를 명령줄 글자로, Split = 그 반대.
// 터미널 인자는 Windows 규칙으로 한 줄에 만들어지므로 맥 forkpty(execve 인자 배열)에서 Split 으로 다시 나눈다
public static class CommandLine
{
    public static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if ('\\' == c)
            {
                backslashes++;
                continue;
            }
            if ('"' == c)
            {
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }
            sb.Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    public static List<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;
        int i = 0;
        while (i < commandLine.Length)
        {
            char c = commandLine[i];
            if ('\\' == c)
            {
                int start = i;
                while (i < commandLine.Length && '\\' == commandLine[i])
                {
                    i++;
                }
                int count = i - start;
                bool beforeQuote = i < commandLine.Length && '"' == commandLine[i];
                if (true == beforeQuote)
                {
                    // 2n 개 + " = n 개 + 따옴표 경계 / 2n+1 개 + " = n 개 + 글자 "
                    current.Append('\\', count / 2);
                    hasToken = true;
                    if (1 == count % 2)
                    {
                        current.Append('"');
                        i++;
                    }
                    continue;
                }
                current.Append('\\', count);
                hasToken = true;
                continue;
            }
            if ('"' == c)
            {
                // 따옴표 안에서 "" = 글자 " 하나 + 따옴표 끝 (shell32 CommandLineToArgvW 규칙 — 하네스로 대조함)
                bool isDoubled = true == inQuotes && i + 1 < commandLine.Length && '"' == commandLine[i + 1];
                if (true == isDoubled)
                {
                    current.Append('"');
                    inQuotes = false;
                    i += 2;
                    continue;
                }
                inQuotes = false == inQuotes;
                hasToken = true;
                i++;
                continue;
            }
            bool isSpace = ' ' == c || '\t' == c;
            if (true == isSpace && false == inQuotes)
            {
                if (true == hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                i++;
                continue;
            }
            current.Append(c);
            hasToken = true;
            i++;
        }
        if (true == hasToken)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}
