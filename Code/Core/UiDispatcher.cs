using System.Collections.Concurrent;
using System.Diagnostics;

namespace GameMaker.Core;

// UI 스레드로 일 넘기기 (1.6.0 — WPF Dispatcher 대신). Photino 는 Invoke(동기)만 있고 BeginInvoke 가 없어서
// 줄(큐)에 쌓아 두고 스레드 풀 한 곳에서 window.Invoke(Drain) 로 한꺼번에 비운다. 창이 뜨기 전에 넣은 일은 창이 뜬 뒤 돈다.
// 같은 스레드의 await 뒤 이어서 할 일도 여기로 오게 SynchronizationContext 를 건다 (WPF 와 같은 느낌)
public sealed class Dispatcher
{
    private const int MaxItemsPerDrain = 200;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _uiThreadId;
    private Action<Action>? _invoker;
    private int _isScheduled;
    private Action<Exception>? _errorSink;

    internal Dispatcher()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    // 앱 하나에 하나 (Program.Main 이 만듦)
    public static Dispatcher Current { get; private set; } = null!;

    internal static Dispatcher CreateForCurrentThread()
    {
        var dispatcher = new Dispatcher();
        Current = dispatcher;
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(dispatcher));
        return dispatcher;
    }

    // 창이 뜬 뒤 (Photino window.Invoke) — 그 전에 쌓인 일을 이때부터 비움
    internal void Attach(Action<Action> invoker, Action<Exception> errorSink)
    {
        _invoker = invoker;
        _errorSink = errorSink;
        Schedule();
    }

    // 창이 닫힘 — 더는 넘기지 않음 (남은 일은 버림)
    internal void Detach()
    {
        _invoker = null;
    }

    public bool CheckAccess()
    {
        bool isUiThread = Environment.CurrentManagedThreadId == _uiThreadId;
        return isUiThread;
    }

    public void VerifyAccess()
    {
        Debug.Assert(true == CheckAccess(), "UI 스레드가 아님");
    }

    public DispatcherOperation BeginInvoke(Delegate method, params object?[] args)
    {
        return Enqueue(method, args);
    }

    public DispatcherOperation BeginInvoke(Delegate method, DispatcherPriority priority, params object?[] args)
    {
        return Enqueue(method, args);
    }

    public DispatcherOperation BeginInvoke(DispatcherPriority priority, Delegate method)
    {
        return Enqueue(method, Array.Empty<object?>());
    }

    // 끝날 때까지 기다리는 판 (UI 스레드에서 부르면 바로 실행)
    public void Invoke(Action action)
    {
        if (true == CheckAccess())
        {
            action();
            return;
        }
        Action<Action>? invoker = _invoker;
        if (null == invoker)
        {
            return;
        }
        invoker(action);
    }

    public Task InvokeAsync(Action action)
    {
        DispatcherOperation operation = Enqueue(action, Array.Empty<object?>());
        return operation.Task;
    }

    // 값을 돌려받는 판 (ControlServer 요청 → UI 스레드에서 처리한 결과)
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new ValueCall<T>(func, done);
        Enqueue(call.Run, Array.Empty<object?>());
        return done.Task;
    }

    private sealed class ValueCall<T>
    {
        private readonly Func<T> _func;
        private readonly TaskCompletionSource<T> _done;

        public ValueCall(Func<T> func, TaskCompletionSource<T> done)
        {
            _func = func;
            _done = done;
        }

        public void Run()
        {
            try
            {
                _done.TrySetResult(_func());
            }
            catch (Exception ex)
            {
                _done.TrySetException(ex);
            }
        }
    }

    private DispatcherOperation Enqueue(Delegate method, object?[] args)
    {
        var operation = new DispatcherOperation(method, args);
        _queue.Enqueue(operation.Run);
        Schedule();
        return operation;
    }

    private void Schedule()
    {
        if (null == _invoker)
        {
            return;
        }
        if (_queue.IsEmpty)
        {
            return;
        }
        int wasScheduled = Interlocked.Exchange(ref _isScheduled, 1);
        if (1 == wasScheduled)
        {
            return;
        }
        // UI 스레드에서 불려도 바로 돌리지 않는다 — 지금 하던 일(WebView 이벤트 처리 등)이 끝난 뒤에 돌게
        ThreadPool.UnsafeQueueUserWorkItem(PumpFromPool, null);
    }

    private void PumpFromPool(object? state)
    {
        Action<Action>? invoker = _invoker;
        if (null == invoker)
        {
            Volatile.Write(ref _isScheduled, 0);
            return;
        }
        try
        {
            invoker(Drain);
        }
        catch (Exception ex)
        {
            // 창이 닫히는 중 등 — 넘기지 못함
            Volatile.Write(ref _isScheduled, 0);
            Debug.WriteLine($"Dispatcher 넘기기 실패: {ex.Message}");
        }
    }

    private void Drain()
    {
        Volatile.Write(ref _isScheduled, 0);
        int count = 0;
        while (count < MaxItemsPerDrain)
        {
            if (false == _queue.TryDequeue(out Action? item))
            {
                break;
            }
            count++;
            try
            {
                item();
            }
            catch (Exception ex)
            {
                // WPF 의 DispatcherUnhandledException 자리 — 기록하고 계속 (앱이 죽으면 세션도 같이 죽으므로)
                _errorSink?.Invoke(ex);
            }
        }
        Schedule();
    }
}

public sealed class DispatcherOperation
{
    private readonly Delegate _method;
    private readonly object?[] _args;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DispatcherOperation(Delegate method, object?[] args)
    {
        _method = method;
        _args = args;
    }

    public Task Task => _done.Task;

    internal void Run()
    {
        try
        {
            if (_method is Action action)
            {
                action();
            }
            else
            {
                _method.DynamicInvoke(_args);
            }
            _done.TrySetResult();
        }
        catch (Exception ex)
        {
            _done.TrySetException(ex);
            throw;
        }
    }
}

// 순서 값은 받기만 함 (줄 하나로 순서대로 돎)
public enum DispatcherPriority
{
    SystemIdle,
    ApplicationIdle,
    ContextIdle,
    Background,
    Input,
    Loaded,
    Render,
    DataBind,
    Normal,
    Send,
}

// 주기 일 — 시간은 스레드 풀 타이머가 재고, Tick 은 UI 스레드에서 돈다. 앞 Tick 이 아직 줄에 있으면 겹쳐 쌓지 않음
public sealed class DispatcherTimer
{
    private readonly Dispatcher _dispatcher;
    private readonly object _lock = new();
    private Timer? _timer;
    private TimeSpan _interval;
    private int _generation;
    private bool _isTickQueued;

    public DispatcherTimer() : this(DispatcherPriority.Background)
    {
    }

    public DispatcherTimer(DispatcherPriority priority)
    {
        _dispatcher = Dispatcher.Current;
    }

    public DispatcherTimer(DispatcherPriority priority, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    // WPF 와 같게: 이 생성자는 바로 시작한다
    public DispatcherTimer(TimeSpan interval, DispatcherPriority priority, EventHandler callback, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _interval = interval;
        Tick += callback;
        Start();
    }

    public event EventHandler? Tick;

    public bool IsEnabled { get; private set; }

    public object? Tag { get; set; }

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value;
            if (true == IsEnabled)
            {
                Start();
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _generation++;
            IsEnabled = true;
            TimeSpan period = _interval;
            if (period <= TimeSpan.Zero)
            {
                period = TimeSpan.FromMilliseconds(1);
            }
            int generation = _generation;
            _timer = new Timer(OnTimer, generation, period, period);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _generation++;
            IsEnabled = false;
        }
    }

    private void OnTimer(object? state)
    {
        lock (_lock)
        {
            if (true == _isTickQueued)
            {
                return;
            }
            _isTickQueued = true;
        }
        int generation = (int)state!;
        var tick = new TickRun(this, generation);
        _dispatcher.BeginInvoke(tick.Run);
    }

    private void RaiseTick(int generation)
    {
        lock (_lock)
        {
            _isTickQueued = false;
            if (generation != _generation)
            {
                return;
            }
        }
        Tick?.Invoke(this, EventArgs.Empty);
    }

    // 어느 시작 차례의 Tick 인지 (멈춘 뒤 늦게 도착한 Tick 은 버림)
    private sealed class TickRun
    {
        private readonly DispatcherTimer _owner;
        private readonly int _generation;

        public TickRun(DispatcherTimer owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public void Run()
        {
            _owner.RaiseTick(_generation);
        }
    }
}

// await 뒤 이어서 할 일을 UI 스레드로
internal sealed class UiSynchronizationContext : SynchronizationContext
{
    private readonly Dispatcher _dispatcher;

    public UiSynchronizationContext(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        var item = new PostItem(d, state);
        _dispatcher.BeginInvoke(item.Run);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        var item = new PostItem(d, state);
        _dispatcher.Invoke(item.Run);
    }

    public override SynchronizationContext CreateCopy()
    {
        return this;
    }

    private sealed class PostItem
    {
        private readonly SendOrPostCallback _callback;
        private readonly object? _state;

        public PostItem(SendOrPostCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        public void Run()
        {
            _callback(_state);
        }
    }
}
