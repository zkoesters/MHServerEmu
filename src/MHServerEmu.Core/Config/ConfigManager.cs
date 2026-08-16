using MHServerEmu.Core.Helpers;

namespace MHServerEmu.Core.Config
{
    /// <summary>
    /// A singleton that provides access to config value containers.
    /// </summary>
    public class ConfigManager
    {
        private readonly Dictionary<Type, ConfigContainer> _configContainerDict = new();
        private readonly IniFile _iniFile;
        private readonly IniFile _overrideFile;

        /// <summary>
        /// Provides access to the <see cref="ConfigManager"/> instance.
        /// </summary>
        public static ConfigManager Instance { get; } = new();

        /// <summary>
        /// Constructs the <see cref="ConfigManager"/> instance.
        /// </summary>
        private ConfigManager()
            : this(Path.Combine(FileHelper.ServerRoot, "Config.ini"), Path.Combine(FileHelper.ServerRoot, "ConfigOverride.ini"))
        {
        }

        internal ConfigManager(string configPath, string overridePath)
        {
            _iniFile = new(configPath);

            OverrideFilePath = overridePath;
            if (File.Exists(OverrideFilePath) == false)
            {
                try
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        using (FileStream stream = new FileStream(OverrideFilePath, new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.Read,
                            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        }))
                        {
                        }
                    }
                    else
                    {
                        using (FileStream stream = new FileStream(OverrideFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        {
                        }
                    }
                }
                catch (IOException) when (File.Exists(OverrideFilePath))
                {
                }
            }

            _overrideFile = new(OverrideFilePath);
        }

        /// <summary>
        /// Gets the path to the override configuration file.
        /// </summary>
        public string OverrideFilePath { get; }

        /// <summary>
        /// Gets a string from the override configuration file without falling back to the base configuration file.
        /// </summary>
        public string GetOverrideString(string section, string key) => _overrideFile.GetString(section, key);

        /// <summary>
        /// Checks Unix mode bits to determine whether the override configuration file can be read or written by group or other users.
        /// Always returns false on Windows because ACL inspection is deliberately unsupported in Phase 1.
        /// </summary>
        public bool HasUnsafeUnixOverrideFilePermissions()
        {
            if (OperatingSystem.IsWindows())
                return false;

            try
            {
                UnixFileMode permissions = File.GetUnixFileMode(OverrideFilePath);
                UnixFileMode unsafePermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
                return (permissions & unsafePermissions) != 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Initializes if needed and returns <typeparamref name="T"/>.
        /// </summary>
        public T GetConfig<T>() where T: ConfigContainer, new()
        {
            lock (_configContainerDict)
            {
                if (_configContainerDict.TryGetValue(typeof(T), out ConfigContainer container) == false)
                {
                    container = new T();
                    container.Initialize(_iniFile, _overrideFile);
                    _configContainerDict.Add(typeof(T), container);
                }

                return (T)container;
            }
        }
    }
}
