namespace Noted.Tests.Services;

internal sealed class CallbackTimeProvider(Action callback) : TimeProvider
{
    private Action? _callback = callback;

    public override DateTimeOffset GetUtcNow()
    {
        var callback = _callback;
        _callback = null;
        callback?.Invoke();
        return DateTimeOffset.UtcNow;
    }
}
