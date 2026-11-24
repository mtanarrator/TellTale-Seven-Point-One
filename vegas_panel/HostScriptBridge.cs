using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ScriptPortal.Vegas;

namespace VegasSurround71
{
    /// <summary>Summary of a validation report produced in the VEGAS script host.</summary>
    public sealed class ValidationOutcome
    {
        public string ReportText { get; set; }
        public string ReportPath { get; set; }
        public int PassedCount { get; set; }
        public int FailedCount { get; set; }
        public bool Passed { get; set; }
    }

    /// <summary>Deferred keyframe sample queued while VEGAS is playing.</summary>
    public struct PanPoint
    {
        public PanPoint(long nanos, double x, double y, double lfe, double spread)
        {
            Nanos = nanos;
            X = x;
            Y = y;
            Lfe = lfe;
            Spread = spread;
        }

        public long Nanos { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Lfe { get; private set; }
        public double Spread { get; private set; }
    }

    /// <summary>
    /// Executes project-mutating VegasSurround71 operations in VEGAS' own script
    /// host through <c>Vegas.RunScriptText</c>.
    ///
    /// <para>
    /// Observed in VEGAS Pro 2027 build 51: direct project mutation from an
    /// application-extension callback fails with E_UNEXPECTED
    /// ("Catastrophic failure"), while the identical API calls succeed in the
    /// script engine. This bridge is therefore the supported execution path for
    /// provisioning and validation; reads and UI status queries still run
    /// directly in the extension.
    /// </para>
    /// </summary>
    public static class HostScriptBridge
    {
        /// <summary>
        /// Runs <see cref="HostValidation.Run"/> in the VEGAS script host and
        /// returns the resulting report/summary.
        /// </summary>
        public const string TrackRoutingGuidance =
            "To start, click \"Setup 7.1 Busses\". Previous or subsequent added tracks will automatically be assigned to each bus.";

        /// <summary>
        /// Unwraps reflection/script-host wrappers so logs show the real
        /// root-cause message instead of "Exception has been thrown by the
        /// target of an invocation."
        /// </summary>
        public static string DescribeException(Exception ex)
        {
            if (ex == null)
            {
                return "Unknown error.";
            }

            Exception current = ex;
            while (current is TargetInvocationException &&
                   current.InnerException != null)
            {
                current = current.InnerException;
            }

            return string.IsNullOrWhiteSpace(current.Message)
                ? current.GetType().Name
                : current.Message;
        }

        public static ValidationOutcome RunValidation(
            Vegas vegas, string reportPath, bool createScratchProject)
        {
            var outcome = new ValidationOutcome { ReportPath = reportPath };

            if (vegas == null)
            {
                outcome.ReportText = "Host script bridge failed: Vegas instance is null.";
                outcome.FailedCount = 1;
                return outcome;
            }

            try
            {
                string assemblyPath = typeof(HostScriptBridge).Assembly.Location;
                string reportLiteral = VerbatimLiteral(reportPath);
                string assemblyLiteral = VerbatimLiteral(assemblyPath);
                string scratchLiteral = createScratchProject ? "true" : "false";

                string script = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        Assembly asm = Assembly.LoadFrom(" + assemblyLiteral + @");
        Type type = asm.GetType(""VegasSurround71.HostValidation"", true);
        MethodInfo method = type.GetMethod(""Run"", new Type[] { typeof(Vegas), typeof(string), typeof(bool) });
        if (method == null) throw new MissingMethodException(""VegasSurround71.HostValidation.Run"");
        method.Invoke(null, new object[] { vegas, " + reportLiteral + @", " + scratchLiteral + @" });
    }
}";

                vegas.RunScriptText(ScriptEngineType.CSharp, script);

                string undoReportPath = reportPath + ".undo.txt";
                HostValidation.RunUndoProbesDirect(vegas, undoReportPath);

                outcome.ReportText = CombineReports(reportPath, undoReportPath);
                File.WriteAllText(reportPath, outcome.ReportText, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                outcome.ReportText = "Host script bridge failed:\r\n" + DescribeException(ex);
                TryWriteFailureReport(reportPath, ex);
            }

            ParseSummary(outcome);
            return outcome;
        }

        /// <summary>
        /// Applies a pan in a one-operation host script transaction. Used by the
        /// WPF puck on mouse release (dragging itself only computes gains live).
        /// </summary>
        public static bool ApplyTrackPanning(
            Vegas vegas,
            AudioTrack track,
            Timecode cursor,
            double x,
            double y,
            double lfeSend,
            double spread,
            bool writeKeyframes,
            out string error)
        {
            string ignored;
            return ApplyTrackPanning(
                vegas, track, cursor, x, y, lfeSend, spread, writeKeyframes,
                out error, out ignored);
        }

        /// <summary>
        /// Applies a pan in a one-operation host script transaction and returns
        /// the post-write envelope values read back from VEGAS.
        /// </summary>
        public static bool ApplyTrackPanning(
            Vegas vegas,
            AudioTrack track,
            Timecode cursor,
            double x,
            double y,
            double lfeSend,
            double spread,
            bool writeKeyframes,
            out string error,
            out string readback)
        {
            error = null;
            readback = null;

            if (vegas == null)
            {
                error = "Vegas instance is null.";
                return false;
            }

            if (track == null)
            {
                error = TrackRoutingGuidance;
                return false;
            }

            if (cursor == null)
            {
                error = "Cursor position is null.";
                return false;
            }

            if (vegas.Project == null)
            {
                error = "No active VEGAS project.";
                return false;
            }

            if (BusMatrixManager.FindCanonicalBusses(vegas.Project) == null)
            {
                error = "Click 'Setup 7.1 Busses' to route tracks first.";
                return false;
            }

            string readbackPath = Path.Combine(
                Path.GetTempPath(),
                "vs71_send_readback_" + Guid.NewGuid().ToString("N") + ".txt");

            try
            {
                string assemblyLiteral = VerbatimLiteral(
                    typeof(HostScriptBridge).Assembly.Location);
                string args = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}, {1}, {2}, {3}, {4}, {5}L, {6}, {7}",
                    track.Index,
                    x.ToString("R", CultureInfo.InvariantCulture),
                    y.ToString("R", CultureInfo.InvariantCulture),
                    lfeSend.ToString("R", CultureInfo.InvariantCulture),
                    spread.ToString("R", CultureInfo.InvariantCulture),
                    cursor.Nanos,
                    writeKeyframes ? "true" : "false",
                    VerbatimLiteral(readbackPath));

                string script = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        Assembly asm = Assembly.LoadFrom(" + assemblyLiteral + @");
        Type type = asm.GetType(""VegasSurround71.HostScriptBridge"", true);
        MethodInfo method = type.GetMethod(""ApplyPanningInScript"", new Type[] {
            typeof(Vegas), typeof(int), typeof(double), typeof(double),
            typeof(double), typeof(double), typeof(long), typeof(bool),
            typeof(string) });
        if (method == null) throw new MissingMethodException(""HostScriptBridge.ApplyPanningInScript"");
        method.Invoke(null, new object[] { vegas, " + args + @" });
    }
}";

                vegas.RunScriptText(ScriptEngineType.CSharp, script);

                try
                {
                    readback = File.Exists(readbackPath)
                        ? File.ReadAllText(readbackPath).Trim()
                        : "<read-back missing>";
                }
                finally
                {
                    TryDelete(readbackPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
                TryDelete(readbackPath);
                return false;
            }
        }

        /// <summary>
        /// Script-host entry point used by <see cref="ApplyTrackPanning"/>.
        /// Resolves the track by collection index (not Selected/Name), maps sends
        /// by the real bus letter, writes the linear 0..2 send envelope, and
        /// writes post-write values to <paramref name="readbackPath"/>.
        /// </summary>
        public static void ApplyPanningInScript(
            Vegas vegas,
            int trackIndex,
            double x,
            double y,
            double lfeSend,
            double spread,
            long cursorNanos,
            bool writeKeyframes,
            string readbackPath)
        {
            Project project = vegas.Project;
            if (project == null)
            {
                throw new InvalidOperationException("No active VEGAS project.");
            }

            if (trackIndex < 0 || trackIndex >= project.Tracks.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(trackIndex));
            }

            var track = project.Tracks[trackIndex] as AudioTrack;
            if (track == null)
            {
                throw new InvalidOperationException(
                    "Track index " + trackIndex + " is not an audio track.");
            }

            Timecode cursor = Timecode.FromNanos(cursorNanos);
            BusMatrixManager.ApplyTrackPanning(
                vegas, track, cursor,
                (float)x, (float)y, (float)lfeSend, (float)spread,
                writeKeyframes);

            if (!string.IsNullOrEmpty(readbackPath))
            {
                File.WriteAllText(
                    readbackPath, BuildReadback(track, project, cursor),
                    new UTF8Encoding(false));
            }
        }

        /// <summary>
        /// Applies a deferred keyframe batch in one host script transaction and
        /// returns post-write envelope values.
        /// </summary>
        public static bool ApplyTrackPanningBatch(
            Vegas vegas,
            AudioTrack track,
            IList<PanPoint> points,
            out string error,
            out string readback)
        {
            error = null;
            readback = null;

            if (vegas == null)
            {
                error = "Vegas instance is null.";
                return false;
            }

            if (track == null)
            {
                error = TrackRoutingGuidance;
                return false;
            }

            if (points == null || points.Count == 0)
            {
                error = "No pending keyframe points.";
                return false;
            }

            if (vegas.Project == null)
            {
                error = "No active VEGAS project.";
                return false;
            }

            if (BusMatrixManager.FindCanonicalBusses(vegas.Project) == null)
            {
                error = "Click 'Setup 7.1 Busses' to route tracks first.";
                return false;
            }

            string readbackPath = Path.Combine(
                Path.GetTempPath(),
                "vs71_send_batch_" + Guid.NewGuid().ToString("N") + ".txt");

            try
            {
                var nanos = new StringBuilder();
                var xs = new StringBuilder();
                var ys = new StringBuilder();
                var lfes = new StringBuilder();
                var spreads = new StringBuilder();

                for (int i = 0; i < points.Count; i++)
                {
                    if (i > 0)
                    {
                        nanos.Append(", ");
                        xs.Append(", ");
                        ys.Append(", ");
                        lfes.Append(", ");
                        spreads.Append(", ");
                    }

                    PanPoint point = points[i];
                    nanos.Append(point.Nanos.ToString(CultureInfo.InvariantCulture)).Append('L');
                    xs.Append(point.X.ToString("R", CultureInfo.InvariantCulture));
                    ys.Append(point.Y.ToString("R", CultureInfo.InvariantCulture));
                    lfes.Append(point.Lfe.ToString("R", CultureInfo.InvariantCulture));
                    spreads.Append(point.Spread.ToString("R", CultureInfo.InvariantCulture));
                }

                string assemblyLiteral = VerbatimLiteral(
                    typeof(HostScriptBridge).Assembly.Location);
                string readbackLiteral = VerbatimLiteral(readbackPath);

                string script = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        Assembly asm = Assembly.LoadFrom(" + assemblyLiteral + @");
        Type type = asm.GetType(""VegasSurround71.HostScriptBridge"", true);
        MethodInfo method = type.GetMethod(""ApplyPanningBatchInScript"", new Type[] {
            typeof(Vegas), typeof(int), typeof(long[]), typeof(double[]),
            typeof(double[]), typeof(double[]), typeof(double[]), typeof(string) });
        if (method == null) throw new MissingMethodException(""HostScriptBridge.ApplyPanningBatchInScript"");
        long[] nanos = new long[] { " + nanos + @" };
        double[] xs = new double[] { " + xs + @" };
        double[] ys = new double[] { " + ys + @" };
        double[] lfes = new double[] { " + lfes + @" };
        double[] spreads = new double[] { " + spreads + @" };
        method.Invoke(null, new object[] {
            vegas, " + track.Index + @", nanos, xs, ys, lfes, spreads, " + readbackLiteral + @" });
    }
}";

                vegas.RunScriptText(ScriptEngineType.CSharp, script);

                try
                {
                    readback = File.Exists(readbackPath)
                        ? File.ReadAllText(readbackPath).Trim()
                        : "<read-back missing>";
                }
                finally
                {
                    TryDelete(readbackPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
                TryDelete(readbackPath);
                return false;
            }
        }

        /// <summary>
        /// Script-host entry point used by <see cref="ApplyTrackPanningBatch"/>.
        /// </summary>
        public static void ApplyPanningBatchInScript(
            Vegas vegas,
            int trackIndex,
            long[] nanos,
            double[] xs,
            double[] ys,
            double[] lfes,
            double[] spreads,
            string readbackPath)
        {
            Project project = vegas.Project;
            if (project == null)
            {
                throw new InvalidOperationException("No active VEGAS project.");
            }

            if (trackIndex < 0 || trackIndex >= project.Tracks.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(trackIndex));
            }

            var track = project.Tracks[trackIndex] as AudioTrack;
            if (track == null)
            {
                throw new InvalidOperationException(
                    "Track index " + trackIndex + " is not an audio track.");
            }

            if (nanos == null || xs == null || ys == null ||
                lfes == null || spreads == null ||
                nanos.Length != xs.Length || nanos.Length != ys.Length ||
                nanos.Length != lfes.Length || nanos.Length != spreads.Length)
            {
                throw new ArgumentException("Pan batch arrays are inconsistent.");
            }

            var points = new PanPoint[nanos.Length];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new PanPoint(nanos[i], xs[i], ys[i], lfes[i], spreads[i]);
            }

            BusMatrixManager.ApplyTrackPanningBatch(vegas, track, points);

            if (!string.IsNullOrEmpty(readbackPath))
            {
                long lastNanos = nanos.Length == 0 ? 0 : nanos[nanos.Length - 1];
                File.WriteAllText(
                    readbackPath,
                    BuildReadback(track, project, Timecode.FromNanos(lastNanos)),
                    new UTF8Encoding(false));
            }
        }

        private static string BuildReadback(
            AudioTrack track, Project project, Timecode cursor)
        {
            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "track={0} '{1}' ", track.Index, track.Name);

            if (busses == null)
            {
                sb.Append("<canonical busses missing>");
                return sb.ToString();
            }

            for (int i = 0; i < busses.Length; i++)
            {
                EnvelopeType type;
                try
                {
                    type = Bus71Layout.GetSendEnvelopeType(busses[i]);
                }
                catch (InvalidOperationException)
                {
                    sb.Append(Bus71Layout.ChannelLabels[i]).Append("=<no bus letter> ");
                    continue;
                }

                Envelope env = track.Envelopes.HasEnvelope(type)
                    ? track.Envelopes.FindByType(type)
                    : null;

                if (env == null)
                {
                    sb.Append(Bus71Layout.ChannelLabels[i]).Append("=<missing> ");
                    continue;
                }

                double value = env.ValueAt(cursor);
                double db = value <= 0.0
                    ? -60.0
                    : 20.0 * Math.Log10(Math.Min(2.0, value));
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}({1})={2:0.####}/{3:0.0}dB ",
                    Bus71Layout.ChannelLabels[i], busses[i].Name, value, db);
            }

            return sb.ToString().Trim();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        /// <summary>
        /// Runs <see cref="BusMatrixManager.Provision71BusTopology"/> in the VEGAS
        /// script host.
        /// </summary>
        public static bool RunProvision(Vegas vegas, out string error)
        {
            error = null;
            if (vegas == null)
            {
                error = "Vegas instance is null.";
                return false;
            }

            try
            {
                string assemblyPath = typeof(HostScriptBridge).Assembly.Location;
                string assemblyLiteral = VerbatimLiteral(assemblyPath);

                string script = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        Assembly asm = Assembly.LoadFrom(" + assemblyLiteral + @");
        Type type = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        MethodInfo method = type.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) });
        if (method == null) throw new MissingMethodException(""VegasSurround71.BusMatrixManager.Provision71BusTopology"");
        method.Invoke(null, new object[] { vegas });
    }
}";

                vegas.RunScriptText(ScriptEngineType.CSharp, script);
                return true;
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
                return false;
            }
        }

        private static string CombineReports(string mainPath, string undoPath)
        {
            string main = File.Exists(mainPath)
                ? File.ReadAllText(mainPath)
                : "<setup/automation report missing>";
            string undo = File.Exists(undoPath)
                ? File.ReadAllText(undoPath)
                : "<undo report missing>";

            var sb = new StringBuilder();
            sb.AppendLine("VegasSurround71 Phase 4A In-Host Validation");
            sb.AppendLine("=============================================");
            sb.AppendLine("--- SETUP / AUTOMATION PHASE ---");
            AppendBody(sb, main);
            sb.AppendLine();
            sb.AppendLine("--- UNDO / REDO PHASE (separate VEGAS transaction) ---");
            AppendBody(sb, undo);

            int passed = 0;
            int failed = 0;
            string all = main + "\n" + undo;
            foreach (string line in all.Split(new[] { "\r\n", "\n" },
                StringSplitOptions.None))
            {
                if (line.StartsWith("PASS ", StringComparison.Ordinal))
                {
                    passed++;
                }
                else if (line.StartsWith("FAIL ", StringComparison.Ordinal))
                {
                    failed++;
                }
            }

            sb.AppendLine();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "SUMMARY: {0} passed, {1} failed, {2} total{3}",
                passed, failed, passed + failed, Environment.NewLine);
            sb.AppendLine(failed == 0 && passed > 0 ? "RESULT: PASS" : "RESULT: FAIL");
            return sb.ToString();
        }

        private static void AppendBody(StringBuilder sb, string text)
        {
            foreach (string line in text.Split(new[] { "\r\n", "\n" },
                StringSplitOptions.None))
            {
                if (line.StartsWith("VegasSurround71 Phase 4A In-Host Validation",
                        StringComparison.Ordinal) ||
                    line.StartsWith("=============================================",
                        StringComparison.Ordinal) ||
                    line.StartsWith("SUMMARY:", StringComparison.Ordinal) ||
                    line.StartsWith("RESULT:", StringComparison.Ordinal))
                {
                    continue;
                }

                sb.AppendLine(line);
            }
        }

        private static void ParseSummary(ValidationOutcome outcome)
        {
            string text = outcome.ReportText ?? string.Empty;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("PASS ", StringComparison.Ordinal))
                    {
                        outcome.PassedCount++;
                    }
                    else if (line.StartsWith("FAIL ", StringComparison.Ordinal))
                    {
                        outcome.FailedCount++;
                    }
                }
            }

            outcome.Passed = text.IndexOf("RESULT: PASS", StringComparison.Ordinal) >= 0 &&
                             outcome.FailedCount == 0;
        }

        private static void TryWriteFailureReport(string reportPath, Exception ex)
        {
            try
            {
                string dir = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(reportPath,
                    "TellTale-Seven-Point-One Diagnostic" + Environment.NewLine +
                    "Host script bridge failed:" + Environment.NewLine +
                    DescribeException(ex) + Environment.NewLine +
                    ex + Environment.NewLine +
                    "RESULT: FAIL" + Environment.NewLine);
            }
            catch (Exception)
            {
                // Best effort only.
            }
        }

        private static string VerbatimLiteral(string value)
        {
            return "@\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }
    }
}
