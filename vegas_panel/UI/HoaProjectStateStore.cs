using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ScriptPortal.Vegas;

namespace VegasSurround71.UI
{
    /// <summary>
    /// Persists the HOA workspace state per VEGAS project in
    /// <c>%LOCALAPPDATA%\TellTale7PointOne\hoa-state\&lt;project-hash&gt;.state</c>.
    ///
    /// The store is intentionally sidecar-based: it never mutates the project,
    /// bus envelopes, or automation. The project key is derived from the saved
    /// project path; unsaved projects share an "untitled" slot.
    /// </summary>
    public static class HoaProjectStateStore
    {
        public const int CurrentVersion = 1;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        /// <summary>Serializes the HOA state to the stable key=value format.</summary>
        public static string Serialize(
            HoaSpatialState state, string outputPath, int snapDegrees)
        {
            if (state == null)
            {
                state = new HoaSpatialState();
            }

            var builder = new StringBuilder();
            builder.AppendLine("version=" + CurrentVersion.ToString(
                CultureInfo.InvariantCulture));
            builder.AppendLine("order=" + ((int)state.Order).ToString(
                CultureInfo.InvariantCulture));
            builder.AppendLine("azimuth=" + state.AzimuthDegrees.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("elevation=" + state.ElevationDegrees.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("distance=" + state.Distance.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("spread=" + state.Spread.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("preview=" + ((int)state.PreviewMode).ToString(
                CultureInfo.InvariantCulture));
            builder.AppendLine("exportTarget=" +
                ((int)state.ExportTarget).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("sourceMode=" +
                ((int)state.SourceMode).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("distanceModel=" +
                ((int)state.DistanceModel).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("air=" + state.AirAbsorption.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("headModel=" +
                ((int)state.HeadModel).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("headWidth=" + state.HeadWidth.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("roomAmount=" + state.RoomAmount.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("roomSize=" + state.RoomSize.ToString(
                "R", CultureInfo.InvariantCulture));
            builder.AppendLine("snap=" + snapDegrees.ToString(
                CultureInfo.InvariantCulture));
            builder.AppendLine("outputPath=" + SanitizeLine(outputPath));
            return builder.ToString();
        }

        /// <summary>Parses a serialized HOA state; returns false for bad data.</summary>
        public static bool TryDeserialize(
            string text,
            HoaSpatialState state,
            out string outputPath,
            out int snapDegrees)
        {
            outputPath = null;
            snapDegrees = 0;

            if (state == null || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                values[key] = value;
            }

            string version;
            if (!values.TryGetValue("version", out version) ||
                version != CurrentVersion.ToString(CultureInfo.InvariantCulture))
            {
                return false;
            }

            var parsed = new HoaSpatialState();

            int order;
            if (TryGetInt(values, "order", out order) && order >= 1 && order <= 3)
            {
                parsed.Order = (HoaOrder)order;
            }

            double azimuth;
            if (TryGetDouble(values, "azimuth", out azimuth))
            {
                parsed.AzimuthDegrees = HoaSpatialMath.NormalizeAzimuth(azimuth);
            }

            double elevation;
            if (TryGetDouble(values, "elevation", out elevation))
            {
                parsed.ElevationDegrees = HoaSpatialMath.Clamp(
                    elevation,
                    HoaSpatialMath.MinElevation,
                    HoaSpatialMath.MaxElevation);
            }

            double distance;
            if (TryGetDouble(values, "distance", out distance))
            {
                parsed.Distance = HoaSpatialMath.Clamp(
                    distance,
                    HoaSpatialMath.MinDistance,
                    HoaSpatialMath.MaxDistance);
            }

            double spread;
            if (TryGetDouble(values, "spread", out spread))
            {
                parsed.Spread = HoaSpatialMath.Clamp(
                    spread,
                    HoaSpatialMath.MinSpread,
                    HoaSpatialMath.MaxSpread);
            }

            int preview;
            if (TryGetInt(values, "preview", out preview) &&
                preview >= 0 && preview <= 2)
            {
                parsed.PreviewMode = (HoaPreviewMode)preview;
            }

            int exportTarget;
            if (TryGetInt(values, "exportTarget", out exportTarget) &&
                exportTarget >= 0 && exportTarget <= 1)
            {
                parsed.ExportTarget = (HoaExportTarget)exportTarget;
            }

            int sourceMode;
            if (TryGetInt(values, "sourceMode", out sourceMode) &&
                sourceMode >= 0 && sourceMode <= 1)
            {
                parsed.SourceMode = (HoaSourceMode)sourceMode;
            }

            int distanceModel;
            if (TryGetInt(values, "distanceModel", out distanceModel) &&
                distanceModel >= 0 && distanceModel <= 3)
            {
                parsed.DistanceModel = (HoaDistanceModel)distanceModel;
            }

            double air;
            if (TryGetDouble(values, "air", out air))
            {
                parsed.AirAbsorption = HoaSpatialMath.Clamp(air, 0.0, 1.0);
            }

            int headModel;
            if (TryGetInt(values, "headModel", out headModel) &&
                headModel >= 0 && headModel <= 3)
            {
                parsed.HeadModel = (HoaHeadModel)headModel;
            }

            double headWidth;
            if (TryGetDouble(values, "headWidth", out headWidth))
            {
                parsed.HeadWidth = HoaSpatialMath.Clamp(headWidth, 0.5, 1.5);
            }

            double roomAmount;
            if (TryGetDouble(values, "roomAmount", out roomAmount))
            {
                parsed.RoomAmount = HoaSpatialMath.Clamp(roomAmount, 0.0, 1.0);
            }

            double roomSize;
            if (TryGetDouble(values, "roomSize", out roomSize))
            {
                parsed.RoomSize = HoaSpatialMath.Clamp(roomSize, 0.0, 1.0);
            }

            int snap;
            if (TryGetInt(values, "snap", out snap) &&
                (snap == 0 || snap == 1 || snap == 5 || snap == 15 || snap == 45))
            {
                snapDegrees = snap;
            }

            string path;
            if (values.TryGetValue("outputPath", out path))
            {
                outputPath = path;
            }

            state.CopyFrom(parsed);
            return true;
        }

        /// <summary>Loads the current project's HOA state, if present.</summary>
        public static bool TryLoad(
            WorkspaceContext context,
            HoaSpatialState state,
            out string outputPath,
            out int snapDegrees)
        {
            outputPath = null;
            snapDegrees = 0;

            string path = StatePath(context, false);
            if (path == null || !File.Exists(path))
            {
                return false;
            }

            try
            {
                string text = File.ReadAllText(path, StrictUtf8);
                return TryDeserialize(text, state, out outputPath, out snapDegrees);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Saves the current project's HOA state atomically.</summary>
        public static bool Save(
            WorkspaceContext context,
            HoaSpatialState state,
            string outputPath,
            int snapDegrees)
        {
            string path = StatePath(context, true);
            if (path == null)
            {
                return false;
            }

            string temp = path + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(
                    temp, Serialize(state, outputPath, snapDegrees), StrictUtf8);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (Exception)
                    {
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }

                return true;
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                    // Best-effort cleanup.
                }

                return false;
            }
        }

        private static string StatePath(WorkspaceContext context, bool create)
        {
            Project project = context == null ? null : context.GetProject();
            if (project == null)
            {
                return null;
            }

            string key = ProjectKey(project);
            string directory = Path.Combine(
                ExtensionSettings.SettingsDirectory, "hoa-state");
            if (create)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return Path.Combine(directory, key + ".state");
        }

        private static string ProjectKey(Project project)
        {
            string path = null;
            try
            {
                path = project.FilePath;
            }
            catch (Exception)
            {
                path = null;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return "untitled";
            }

            string normalized = path;
            try
            {
                normalized = Path.GetFullPath(path).ToLowerInvariant();
            }
            catch (Exception)
            {
                normalized = path.ToLowerInvariant();
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(32);
                for (int index = 0; index < 16; index++)
                {
                    builder.Append(hash[index].ToString("x2",
                        CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static string SanitizeLine(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static bool TryGetInt(
            Dictionary<string, string> values, string key, out int value)
        {
            value = 0;
            string raw;
            return values.TryGetValue(key, out raw) &&
                int.TryParse(raw, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetDouble(
            Dictionary<string, string> values, string key, out double value)
        {
            value = 0.0;
            string raw;
            return values.TryGetValue(key, out raw) &&
                double.TryParse(raw, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value);
        }
    }
}
