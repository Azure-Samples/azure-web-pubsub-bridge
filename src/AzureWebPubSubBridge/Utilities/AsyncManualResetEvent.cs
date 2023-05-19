namespace AzureWebPubSubBridge.Utilities;

public class AsyncManualResetEvent<TValue>
{
    private volatile bool _isCanceled;

    private volatile TaskCompletionSource<(TValue Value, bool WasReset)> _signal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Cancel(Exception? exception = null)
    {
        _isCanceled = true;
        if (exception == null)
            _signal.TrySetCanceled();
        else
            _signal.TrySetException(exception);
    }

    public void Reset()
    {
        TaskCompletionSource<(TValue, bool)> signal;
        TaskCompletionSource<(TValue, bool)> previous;
        do
        {
            signal = _signal;
            previous = Interlocked.CompareExchange(ref _signal,
                new TaskCompletionSource<(TValue, bool)>(TaskCreationOptions.RunContinuationsAsynchronously), signal);
        } while (previous != signal);

        // If canceled during reset then try and cancel both signals.
        if (_isCanceled)
        {
            previous.TrySetCanceled();
            _signal.TrySetCanceled();
            return;
        }

        // Complete the previous signal.
        previous.TrySetResult((default, true)!);
    }

    public void Set(TValue value)
    {
        for (var signal = _signal; !signal.TrySetResult((value, false)); signal = _signal)
        {
            if (signal.Task.Result.WasReset)
                continue;

            if (value == null && signal.Task.Result.Value == null)
                return;

            if (value!.Equals(signal.Task.Result.Value))
                return;

            Reset();
        }
    }

    public async Task<TValue> WaitAsync(CancellationToken cancellationToken = new())
    {
        await using var cancelRegistration = cancellationToken.Register(() => Cancel());
        TValue value;
        bool wasReset;
        do
        {
            (value, wasReset) = await _signal.Task;
        } while (wasReset);

        return value;
    }
}