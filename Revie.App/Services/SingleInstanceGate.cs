namespace Revie.App.Services;

public sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly bool _ownsMutex;

    public SingleInstanceGate(string mutexName)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            throw new ArgumentException("Mutex name is required.", nameof(mutexName));
        }

        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        _ownsMutex = createdNew;
        IsPrimaryInstance = createdNew;
    }

    public bool IsPrimaryInstance { get; }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore release failures during shutdown.
            }
        }

        _mutex.Dispose();
    }
}
