﻿using MHServerEmu.Core.Logging;
using System.Reflection;
using System.Text.Json;

namespace MHServerEmu.Core.Helpers
{
    /// <summary>
    /// Makes it easier to load and save files.
    /// </summary>
    public static class FileHelper
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public const string FileNameDateFormat = "yyyy-MM-dd_HH.mm.ss";

        public static readonly string ServerRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public static readonly string DataDirectory = Path.Combine(ServerRoot, "Data");

        /// <summary>
        /// Returns a path relative to server root directory.
        /// </summary>
        public static string GetRelativePath(string filePath)
        {
            return Path.GetRelativePath(ServerRoot, filePath);
        }

        /// <summary>
        /// Deserializes a <typeparamref name="T"/> from a JSON file located at the specified path.
        /// </summary>
        public static T DeserializeJson<T>(string path, JsonSerializerOptions options = null)
        {
            try
            {
                string json = File.ReadAllText(path);
                T data = JsonSerializer.Deserialize<T>(json, options);
                return data;
            }
            catch
            {
                return Logger.WarnReturn<T>(default, $"DeserializeJson(): Failed to deserialize {path} as {typeof(T).Name}");
            }
        }

        /// <summary>
        /// Serializes a <typeparamref name="T"/> to JSON and saves it to the specified path.
        /// </summary>
        public static void SerializeJson<T>(string path, T @object, JsonSerializerOptions options = null)
        {
            string dirName = Path.GetDirectoryName(path);
            if (Directory.Exists(dirName) == false)
                Directory.CreateDirectory(dirName);

            string json = JsonSerializer.Serialize(@object, options);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Saves the provided <see cref="string"/> to a text file in the server root directory.
        /// </summary>
        public static void SaveTextFileToRoot(string fileName, string text)
        {
            File.WriteAllText(Path.Combine(ServerRoot, fileName), text);
        }

        public static bool CreateFileBackup(string filePath, int maxBackups)
        {
            if (maxBackups == 0)
                return false;

            if (File.Exists(filePath) == false)
                return Logger.WarnReturn(false, $"CreateFileBackup(): File not found at {filePath}");

            // Cache backup file names for reuse.
            // NOTE: We can also reuse the same string array for multiple calls of this function,
            // but it's probably not going to be called often enough to be worth it.
            string[] backupPaths = new string[maxBackups];

            // Look for a free backup index
            int freeIndex = -1;
            for (int i = 0; i < maxBackups; i++)
            {
                // Backup path strings are created on demand so that we don't end up creating
                // a lot of unneeded strings when we don't have a lot of backup files.
                backupPaths[i] = $"{filePath}.bak{i}";
                
                if (File.Exists(backupPaths[i]) == false)
                {
                    freeIndex = i;
                    break;
                }
            }
            
            // Delete the oldest backup if there are no free spots
            if (freeIndex == -1)
            {
                freeIndex = maxBackups - 1;
                File.Delete(backupPaths[freeIndex]);
            }

            // Move files to the right until we free up index 0
            for (int i = freeIndex - 1; i >= 0; i--)
            {
                File.Move(backupPaths[i], backupPaths[i + 1]);
            }

            // Create our backup at index 0
            if (File.Exists(backupPaths[0]))
                return Logger.WarnReturn(false, $"CreateFileBackup(): Backup file path is not free {backupPaths[0]}");

            File.Copy(filePath, backupPaths[0]);

            return true;
        }
    }
}
