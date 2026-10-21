using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Multiplayer.Common;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Collects the game logs and loaded mods to be included with desync file.
    /// </summary>
    public static class LogGenerator
    {
        private const int MaxLogLineCount = 10000;
        // The start has the setup (mods, patching errors), the end the desync itself
        private const int HeadLogLineCount = 2000;

        /// <summary>
        /// Pre-compiled patterns for log redaction to avoid rebuilding AST/NFA state machines on megabyte payloads.
        /// </summary>
        private static readonly Regex NonPlatformAssemblyRegex = new(@"Non platform assembly:.+\n", RegexOptions.Compiled);
        private static readonly Regex PlatformAssemblyRegex = new(@"Platform assembly: .+\n", RegexOptions.Compiled);
        private static readonly Regex FallbackHandlerRegex = new(@"Fallback handler could not load library.+\n", RegexOptions.Compiled);
        private static readonly Regex CompletedReloadRegex = new(@"- Completed reload, in [\d\. ]+ seconds\n", RegexOptions.Compiled);
        private static readonly Regex UnloadTimeRegex = new(@"UnloadTime: [\d\. ]+ ms\n", RegexOptions.Compiled);
        private static readonly Regex TouchSupportRegex = new(@"<RI> Initialized touch support\.\r?\n", RegexOptions.Compiled);
        private static readonly Regex BuildslaveRegex = new(@"\(Filename: [A-Z]:[/\\]buildslave.+\n", RegexOptions.Compiled);

        internal static string PrepareLogData()
        {
            try
            {
                var logSection = GetLogFileContents();
                if (logSection != null)
                {
                    logSection = NormalizeLineEndings(logSection);
                    // redact logs for privacy
                    logSection = RedactRimworldPaths(logSection);
                    logSection = RedactPlayerConnectInformation(logSection);
                    logSection = RedactRendererInformation(logSection);
                    logSection = RedactHomeDirectoryPaths(logSection);
                    logSection = RedactSteamId(logSection);
                    logSection = RedactUselessLines(logSection);
                    logSection = TrimExcessLines(logSection);
                }
                else logSection = "Could not find the log";

                return logSection;
            }
            catch (Exception e)
            {
                return $"Failed to generate log data: \n{e}";
            }
        }

        private static string NormalizeLineEndings(string log)
        {
            return log.Replace("\r\n", "\n");
        }

        private static string TrimExcessLines(string log) =>
            log.KeepHeadAndTailLines(HeadLogLineCount, MaxLogLineCount - HeadLogLineCount);

        /// <summary>
        /// Cleans uninformative Unity runtime lines. Replaces constant substrings via string.Replace to bypass regex engine.
        /// </summary>
        private static string RedactUselessLines(string log)
        {
            log = NonPlatformAssemblyRegex.Replace(log, "");
            log = PlatformAssemblyRegex.Replace(log, "");
            log = FallbackHandlerRegex.Replace(log, "");
            log = CompletedReloadRegex.Replace(log, "");
            log = UnloadTimeRegex.Replace(log, "");
            log = TouchSupportRegex.Replace(log, "");
            log = BuildslaveRegex.Replace(log, "");

            // Use direct string replace for literal sequences instead of invoking regex engines
            log = log.Replace("<RI> Initializing input.\r\n", "");
            log = log.Replace("<RI> Initializing input.\n", "");
            log = log.Replace("<RI> Input initialized.\r\n", "");
            log = log.Replace("<RI> Input initialized.\n", "");
            log = log.Replace("\n \n", "\n");

            return log;
        }

        // only relevant on linux
        private static string RedactSteamId(string log)
        {
            const string idReplacement = "[Steam Id redacted]";
            return Regex.Replace(log, "Steam_SetMinidumpSteamID.+", idReplacement);
        }

        private static string RedactHomeDirectoryPaths(string log)
        {
            // not necessary for windows logs
            if (UnityData.platform is RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor)
                return log;

            const string pathReplacement = "[Home_dir]";
            var homePath = Environment.GetEnvironmentVariable("HOME");
            if (homePath == null) return log;
            return log.Replace(homePath, pathReplacement);
        }

        private static string RedactRimworldPaths(string log)
        {
            const string pathReplacement = "[Rimworld_dir]";
            // easiest way to get the game folder is one level up from dataPath
            var appPath = Path.GetFullPath(Application.dataPath);
            var pathParts = appPath.Split(Path.DirectorySeparatorChar).ToList();
            pathParts.RemoveAt(pathParts.Count - 1);
            appPath = pathParts.Join(Path.DirectorySeparatorChar.ToString());
            log = log.Replace(appPath, pathReplacement);
            if (Path.DirectorySeparatorChar != '/')
            {
                // log will contain mixed windows and unix style paths
                appPath = appPath.Replace(Path.DirectorySeparatorChar, '/');
                log = log.Replace(appPath, pathReplacement);
            }
            return log;
        }

        private static string RedactRendererInformation(string log)
        {
            // apparently renderer information can appear multiple times in the log
            for (var i = 0; i < 5; i++)
            {
                var redacted = RedactString(log, "GfxDevice: ", "\nBegin MonoManager", "[Renderer information redacted]");
                if (log.Length == redacted.Length) break;
                log = redacted;
            }
            return log;
        }

        private static string RedactPlayerConnectInformation(string log)
        {
            return RedactString(log, "PlayerConnection ", "Initialize engine", "[PlayerConnect information redacted]\n");
        }

        private static string GetLogFileContents()
        {
            var filePath = TryGetLogFilePath();
            if (filePath.NullOrEmpty() || !File.Exists(filePath))
            {
                throw new FileNotFoundException("Log file not found:" + filePath);
            }

            var tempPath = Path.GetTempFileName();
            File.Delete(tempPath);
            // we need to copy the log file since the original is already opened for writing by Unity
            File.Copy(filePath, tempPath);
            var fileContents = File.ReadAllText(tempPath);
            File.Delete(tempPath);

            return fileContents;
        }

        private static string RedactString(string original, string redactStart, string redactEnd, string replacement)
        {
            var startIndex = original.IndexOf(redactStart, StringComparison.Ordinal);
            var endIndex = original.IndexOf(redactEnd, StringComparison.Ordinal);
            var result = original;

            if (startIndex < 0 || endIndex < 0) return result;

            var logTail = original[endIndex..];
            result = original[..(startIndex + redactStart.Length)];
            result += replacement;
            result += logTail;

            return result;
        }

        /// <summary>
        /// Attempts to return the path of the log file Unity is writing to.
        /// </summary>
        private static string TryGetLogFilePath()
        {
            if (TryGetCommandLineOptionValue("logfile") is { } cmdLog)
            {
                return cmdLog;
            }

            return UnityData.platform switch
            {
                RuntimePlatform.LinuxPlayer
                    => @"/tmp/rimworld_log",
                RuntimePlatform.OSXPlayer or RuntimePlatform.OSXEditor
                    => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), $"Library/Logs/{Application.companyName}/{Application.productName}/Player.log"),
                RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor
                    => Path.Combine(Application.persistentDataPath, "Player.log"),
                _ => null
            };
        }

        private static string TryGetCommandLineOptionValue(string key)
        {
            const string keyPrefix = "-";
            var prefixedKey = keyPrefix + key;
            var valueExpectedNext = false;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.EqualsIgnoreCase(prefixedKey))
                {
                    valueExpectedNext = true;
                }
                else if (valueExpectedNext && !arg.StartsWith(keyPrefix) && !arg.NullOrEmpty())
                {
                    return arg;
                }
            }
            return null;
        }
    }
}
