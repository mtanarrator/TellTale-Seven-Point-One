using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace VegasSurround71
{
    /// <summary>
    /// Centralized extension display-name settings.
    ///
    /// Resolution order:
    ///   1. TELLTALE_71_DISPLAY_NAME environment variable
    ///   2. %LOCALAPPDATA%\TellTale7PointOne\settings.json
    ///   3. MSBuild ExtensionDisplayName (AssemblyMetadataAttribute)
    ///   4. "TellTale 7-Point-One"
    /// </summary>
    public sealed class ExtensionSettings
    {
        public const string DefaultDisplayName = "TellTale 7-Point-One";
        public const string LegacyDefaultDisplayName = "TellTale 7.1";
        public const string DisplayNameEnvironmentVariable = "TELLTALE_71_DISPLAY_NAME";

        private const string DisplayNameKey = "displayName";
        private const string SettingsFileName = "settings.json";

        public string DisplayName { get; set; }

        public ExtensionSettings()
        {
            DisplayName = DefaultDisplayName;
        }

        public static string SettingsDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TellTale7PointOne");
            }
        }

        public static string SettingsFilePath
        {
            get { return Path.Combine(SettingsDirectory, SettingsFileName); }
        }

        public static string LegacySettingsDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VegasSurround71");
            }
        }

        public static string LegacySettingsFilePath
        {
            get { return Path.Combine(LegacySettingsDirectory, SettingsFileName); }
        }

        /// <summary>MSBuild-provided default from AssemblyMetadata.</summary>
        public static string BuildDisplayName
        {
            get
            {
                try
                {
                    foreach (object attribute in typeof(ExtensionSettings).Assembly
                        .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                    {
                        var metadata = attribute as AssemblyMetadataAttribute;
                        if (metadata != null &&
                            string.Equals(metadata.Key, "ExtensionDisplayName",
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(metadata.Value))
                        {
                            return metadata.Value.Trim();
                        }
                    }
                }
                catch (Exception)
                {
                    // Fall back to the built-in default.
                }

                return DefaultDisplayName;
            }
        }

        /// <summary>The effective display name for the current process.</summary>
        public static string CurrentDisplayName
        {
            get
            {
                EnsureMigrated();
                return ResolveDisplayName(
                    SettingsFilePath,
                    Environment.GetEnvironmentVariable(DisplayNameEnvironmentVariable),
                    BuildDisplayName);
            }
        }

        /// <summary>Loads settings, creating defaults and migrating the old folder.</summary>
        public static ExtensionSettings Load()
        {
            EnsureMigrated();
            ExtensionSettings settings = LoadFrom(SettingsFilePath);
            if (settings == null)
            {
                settings = new ExtensionSettings();
            }

            if (string.IsNullOrWhiteSpace(settings.DisplayName))
            {
                settings.DisplayName = DefaultDisplayName;
            }

            return settings;
        }

        /// <summary>Copies the legacy settings.json into the new settings folder.</summary>
        public static void EnsureMigrated()
        {
            try
            {
                if (File.Exists(SettingsFilePath) ||
                    !File.Exists(LegacySettingsFilePath))
                {
                    return;
                }

                Directory.CreateDirectory(SettingsDirectory);
                File.Copy(LegacySettingsFilePath, SettingsFilePath, false);
            }
            catch (Exception)
            {
                // Migration is best effort; defaults still apply.
            }
        }

        /// <summary>Pure precedence resolver used by tests.</summary>
        public static string ResolveDisplayName(
            string settingsPath, string environmentValue, string buildValue)
        {
            string environmentName = NormalizeDisplayName(environmentValue);
            if (!string.IsNullOrEmpty(environmentName))
            {
                return environmentName;
            }

            ExtensionSettings settings = LoadFrom(settingsPath);
            if (settings != null)
            {
                string settingsName = NormalizeDisplayName(settings.DisplayName);
                if (!string.IsNullOrEmpty(settingsName))
                {
                    return settingsName;
                }
            }

            string buildName = NormalizeDisplayName(buildValue);
            if (!string.IsNullOrEmpty(buildName))
            {
                return buildName;
            }

            return DefaultDisplayName;
        }

        /// <summary>Migrates the previous shipped name to the current one.</summary>
        private static string NormalizeDisplayName(string value)
        {
            string trimmed = value == null ? string.Empty : value.Trim();
            if (string.Equals(trimmed, LegacyDefaultDisplayName,
                StringComparison.OrdinalIgnoreCase))
            {
                return DefaultDisplayName;
            }

            return trimmed;
        }

        /// <summary>Loads settings.json, returning null when absent or invalid.</summary>
        public static ExtensionSettings LoadFrom(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }

                string displayName = ParseStringField(
                    File.ReadAllText(path), DisplayNameKey);
                return new ExtensionSettings
                {
                    DisplayName = displayName == null ? null : displayName.Trim()
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Saves the current display name to the standard settings.json.</summary>
        public static bool SaveDisplayName(string displayName)
        {
            return SaveTo(SettingsFilePath, displayName);
        }

        /// <summary>Saves a display name to an explicit path.</summary>
        public static bool SaveTo(string path, string displayName)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                string value = string.IsNullOrWhiteSpace(displayName)
                    ? DefaultDisplayName
                    : displayName.Trim();

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = new StringBuilder();
                json.Append("{\n");
                json.Append("  \"").Append(DisplayNameKey).Append("\": \"")
                    .Append(EscapeJsonString(value)).Append("\"\n");
                json.Append("}\n");

                File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Minimal JSON string reader for a single field.</summary>
        internal static string ParseStringField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName))
            {
                return null;
            }

            int key = json.IndexOf("\"" + fieldName + "\"",
                StringComparison.OrdinalIgnoreCase);
            if (key < 0)
            {
                return null;
            }

            int colon = json.IndexOf(':', key + fieldName.Length + 2);
            if (colon < 0)
            {
                return null;
            }

            int quote = json.IndexOf('"', colon + 1);
            if (quote < 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            for (int i = quote + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (++i >= json.Length)
                {
                    break;
                }

                char escape = json[i];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < json.Length)
                        {
                            int code;
                            if (int.TryParse(json.Substring(i + 1, 4),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default:
                        sb.Append(escape);
                        break;
                }
            }

            return null;
        }

        /// <summary>Compatibility helper for the displayName field.</summary>
        internal static string ParseDisplayName(string json)
        {
            return ParseStringField(json, DisplayNameKey);
        }

        private static string EscapeJsonString(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4",
                                CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
