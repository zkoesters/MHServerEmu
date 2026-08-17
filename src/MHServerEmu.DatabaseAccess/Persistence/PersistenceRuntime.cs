namespace MHServerEmu.DatabaseAccess.Persistence
{
    public sealed class PersistenceRuntime : IAsyncDisposable
    {
        private readonly object _disposeLock = new();
        private Func<ValueTask> _disposeAsync;
        private Task _disposeTask;

        public PersistenceRuntime(PersistenceServices services, Func<ValueTask> disposeAsync)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
            _disposeAsync = disposeAsync ?? throw new ArgumentNullException(nameof(disposeAsync));
        }

        public PersistenceServices Services { get; }

        public ValueTask DisposeAsync()
        {
            lock (_disposeLock)
            {
                Task disposeTask = _disposeTask;
                if (disposeTask == null)
                {
                    TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    disposeTask = completion.Task;
                    _disposeTask = disposeTask;
                    _ = DisposeCoreAsync(completion);
                }

                return new ValueTask(disposeTask);
            }
        }

        private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                await _disposeAsync();
                lock (_disposeLock)
                    _disposeAsync = null;
                completion.SetResult(true);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
                lock (_disposeLock)
                    _disposeTask = null;
            }
        }
    }
}
