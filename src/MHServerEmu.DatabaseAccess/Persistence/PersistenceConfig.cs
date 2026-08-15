using MHServerEmu.Core.Config;

namespace MHServerEmu.DatabaseAccess.Persistence
{
    public class PersistenceConfig : ConfigContainer
    {
        public string Provider { get; private set; } = string.Empty;
    }
}
