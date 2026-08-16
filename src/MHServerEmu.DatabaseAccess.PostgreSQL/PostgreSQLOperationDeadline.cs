using System.Diagnostics;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLOperationDeadline
    {
        private readonly long _deadlineTimestamp;

        public PostgreSQLOperationDeadline(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            _deadlineTimestamp = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        }

        public TimeSpan Remaining
        {
            get
            {
                long remainingTimestamp = _deadlineTimestamp - Stopwatch.GetTimestamp();
                return remainingTimestamp <= 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds((double)remainingTimestamp / Stopwatch.Frequency);
            }
        }

        public int RemainingCommandTimeoutSeconds => Math.Max(1, (int)Math.Ceiling(Remaining.TotalSeconds));

        public CancellationTokenSource CreateCancellationSource(CancellationToken cancellationToken = default)
        {
            TimeSpan remaining = Remaining;
            if (remaining == TimeSpan.Zero)
                throw new TimeoutException();

            CancellationTokenSource cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(remaining);
            return cancellationSource;
        }
    }
}
