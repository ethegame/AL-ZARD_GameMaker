using GameMaker.Services;

namespace GameMaker.Views;

// 앱 안 [⬆ 업데이트] — 새 판 확인·받기·설치 = Services/AppUpdater.cs, 화면 = wwwroot/update.js (확인 창도 거기서)
public partial class MainWindow
{
    private AppUpdater? _updater;

    private void InitializeUpdate()
    {
        _updater = new AppUpdater(_dataDir, Post, _host.Log, _host.Close);
        _updater.Start();
    }

    private void DisposeUpdate()
    {
        _updater?.Dispose();
    }
}
