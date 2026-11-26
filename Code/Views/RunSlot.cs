namespace GameMaker.Views;

// 그림 실행 칸 하나 — ComfyUI 실행(MainWindow.cs StartRun)과 GPT 그림(MainWindow.Draw.cs StartDraw)이 같이 쓴다.
// 한 번에 하나만 (리소스 칸 진행 줄이 하나라서) + 실행 기록 (제어 창구 GET /run/<id> 가 둘 다 같은 모양으로 읽음).
// 11/26 MainWindow 에 흩어져 있던 _runCancel·_drawCancel·_runStates 와 "둘 중 하나라도 돌면 바쁨" 검사 네 군데를 여기로. UI 스레드에서만 접근
public sealed class RunSlot
{
    private readonly Dictionary<string, RunState> _states = new();
    private CancellationTokenSource? _cancel;
    private bool _isDraw;

    public bool IsBusy => null != _cancel;

    // 리소스 칸 [중단]: GPT 그림이면 이 칸의 취소로, ComfyUI 면 서버에 중단 요청 (MainWindow "cancel")
    public bool IsDrawing => null != _cancel && _isDraw;

    // 비어 있을 때만 부름 (바쁘면 예외 — 화면 [생성] 은 이 글을 그대로 보여 줌). 끝나면 받은 토큰으로 Release
    public CancellationTokenSource Begin(string runId, bool isDraw)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("이미 실행 중입니다.");
        }

        CancellationTokenSource cancel = new();
        _cancel = cancel;
        _isDraw = isDraw;
        _states[runId] = new RunState("running", null, new List<string>());
        return cancel;
    }

    public void Done(string runId, List<string> files)
    {
        _states[runId] = new RunState("done", null, files);
    }

    public void Fail(string runId, string error)
    {
        _states[runId] = new RunState("failed", error, new List<string>());
    }

    public void Release(CancellationTokenSource cancel)
    {
        cancel.Dispose();
        if (ReferenceEquals(_cancel, cancel))
        {
            _cancel = null;
            _isDraw = false;
        }
    }

    public void Cancel()
    {
        _cancel?.Cancel();
    }

    public bool TryGet(string runId, out RunState? state)
    {
        return _states.TryGetValue(runId, out state);
    }
}

public sealed record RunState(string State, string? Error, List<string> Files);
