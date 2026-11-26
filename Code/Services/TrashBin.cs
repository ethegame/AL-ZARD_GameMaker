using System.IO;

namespace GameMaker.Services;

// 폴더를 휴지통으로 (되살릴 수 있게). Windows = 셸 휴지통, 맥 = ~/.Trash 로 이동.
// AllDialogs = 휴지통에 못 넣는 경우 윈도우가 "영구 삭제할까요" 를 먼저 묻는다 (OnlyErrorDialogs 는 묻지 않고 영구 삭제할 수 있음). 취소 = OperationCanceledException
public static class TrashBin
{
    public static void Delete(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                directory,
                Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            string trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
            Directory.CreateDirectory(trash);
            string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
            string target = Path.Combine(trash, name);
            for (int number = 2; Directory.Exists(target); number++)
            {
                target = Path.Combine(trash, $"{name} {number}");
            }

            Directory.Move(directory, target);
            return;
        }

        throw new PlatformNotSupportedException("이 OS 는 휴지통 이동을 지원하지 않아 폴더를 지우지 않았습니다.");
    }
}
