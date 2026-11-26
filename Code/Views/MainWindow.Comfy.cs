using GameMaker.Services;

namespace GameMaker.Views;

// 그림 도구(ComfyUI) 자동 설치 — 화면 [설치 방법] 창(index.html #comfy-guide, wwwroot/comfy.js)의 [설치하기]·[취소].
// 설치 절차 = Services/ComfyInstaller.cs, 끝나면 SetComfyRootAsync(MainWindow.cs) 로 설정 저장 + 서버 시작
public partial class MainWindow
{
    private readonly ComfyInstaller _comfyInstaller = new();
    private CancellationTokenSource? _comfyInstallCancel; // null = 설치 중 아님 (UI 스레드에서만 접근)

    // 창을 열 때: 그래픽카드·여유 공간·받을 판을 미리 보여 줌
    private async Task CheckComfyInstallAsync()
    {
        if (null != _comfyInstallCancel)
        {
            return;
        }

        ComfyInstaller.Plan plan = await Task.Run(() => _comfyInstaller.CheckAsync(CancellationToken.None));
        PostComfyPlan(plan);
    }

    private void PostComfyPlan(ComfyInstaller.Plan plan)
    {
        Post(new
        {
            t = "comfy-plan",
            plan.Gpu,
            plan.Flavor,
            plan.IsNvidia,
            asset = plan.AssetName,
            plan.Tag,
            sizeMb = (long)Math.Round(plan.Size / (1024.0 * 1024.0)),
            freeGb = plan.FreeBytes >= 0 ? ComfyInstaller.ToGb(plan.FreeBytes) : "",
            installed = plan.AlreadyInstalled,
            folder = ComfyInstaller.InstallRoot,
            plan.Warning,
            plan.Error,
        });
    }

    // [설치하기]: 다시 확인 → 받기·풀기·옮기기 → 저장·서버 시작. 진행은 comfy-install-progress, 실패·취소는 comfy-install-done ok=false
    private async Task InstallComfyAsync()
    {
        if (null != _comfyInstallCancel || _comfyInstaller.IsBusy)
        {
            return;
        }

        CancellationTokenSource cancel = new();
        _comfyInstallCancel = cancel;
        Post(new { t = "comfy-install-progress", stage = "check", text = "확인 중…", percent = -1 });
        try
        {
            ComfyInstaller.Plan plan = await Task.Run(() => _comfyInstaller.CheckAsync(cancel.Token));
            PostComfyPlan(plan);
            if (null != plan.Error)
            {
                Post(new { t = "comfy-install-done", ok = false, text = plan.Error });
                return;
            }

            // 진행 알림은 일하는 스레드에서 옴 → UI 줄로
            void Report(ComfyInstaller.Progress progress)
            {
                Dispatcher.BeginInvoke(() => Post(new { t = "comfy-install-progress", stage = progress.Stage, text = progress.Text, percent = progress.Percent }));
            }

            string? error = await Task.Run(() => _comfyInstaller.InstallAsync(plan, Report, cancel.Token));
            if (null != error)
            {
                Post(new { t = "comfy-install-done", ok = false, text = error });
                return;
            }

            Post(new { t = "comfy-install-done", ok = true, text = "" });
            await SetComfyRootAsync(ComfyInstaller.InstallRoot,
                "설치 끝! 그림 도구를 켜는 중이에요 (처음엔 몇 분 걸릴 수 있어요). 그림 모델은 오른쪽 AI 에게 \"그림 모델 준비해 줘\" 라고 말해 주세요 — 용량·라이선스를 먼저 알려 드리고 받아 둬요.");
        }
        catch (OperationCanceledException)
        {
            Post(new { t = "comfy-install-done", ok = false, text = "취소했어요. 받다 만 파일은 지웠어요." });
        }
        catch (Exception ex)
        {
            // 예상 못한 실패도 화면이 "설치 중" 에 멈추지 않게 (반쪽 정리는 ComfyInstaller 의 finally · 다음 시도 때)
            _host.Log("그림 도구 설치 실패: " + ex);
            Post(new { t = "comfy-install-done", ok = false, text = $"설치하지 못했어요: {ex.Message}" });
        }
        finally
        {
            _comfyInstallCancel = null;
            cancel.Dispose();
        }
    }

    private void DisposeComfyInstall()
    {
        // 설치 중 앱을 끄면 7-Zip 을 멈추고 받기를 끊음. 반쪽 폴더는 다음 [설치하기] 때 먼저 지움
        _comfyInstallCancel?.Cancel();
        _comfyInstaller.Dispose();
    }
}
