namespace MHServerEmu.DatabaseAccess.Persistence
{
    public sealed record PersistenceFatalFailure
    {
        public string Code { get; }
        public string Operation { get; }

        public PersistenceFatalFailure(string code, string operation)
        {
            Code = Validate(code, nameof(code));
            Operation = Validate(operation, nameof(operation));
        }

        private static string Validate(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Value must not be blank.", parameterName);

            return value;
        }
    }
}
