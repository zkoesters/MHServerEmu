namespace MHServerEmu.DatabaseAccess.Persistence
{
    public sealed class PersistenceRuntime : IAsyncDisposable
    {
        private Func<ValueTask> _disposeAsync;

        public PersistenceRuntime(PersistenceServices services, Func<ValueTask> disposeAsync)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
            _disposeAsync = disposeAsync ?? throw new ArgumentNullException(nameof(disposeAsync));
        }

        public PersistenceServices Services { get; }

        public async ValueTask DisposeAsync()
        {
            Func<ValueTask> disposeAsync = Interlocked.Exchange(ref _disposeAsync, null);
            if (disposeAsync != null)
                await disposeAsync();
        }
    }
}
