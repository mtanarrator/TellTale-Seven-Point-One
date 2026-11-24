using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ScriptPortal.Vegas;
using VegasSurround71.UI;

namespace VegasSurround71
{
    /// <summary>Result of one HOA master/preview render.</summary>
    public sealed class HoaRenderResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string OutputPath { get; set; }
        public int NativeStatus { get; set; }
        public bool IsPreview { get; set; }
        public HoaPreviewAnalysis Analysis { get; set; }
    }

    /// <summary>Stereo analysis of the last rendered preview.</summary>
    public sealed class HoaPreviewAnalysis
    {
        public double LeftRmsDb { get; set; }
        public double RightRmsDb { get; set; }
        public double PeakDb { get; set; }
        public double Correlation { get; set; }
        public string BalanceText { get; set; }
    }

    /// <summary>
    /// HOA audio orchestrator, deliberately separate from the 7.1
    /// <see cref="RenderOrchestrator"/> and <see cref="BusMatrixManager"/>.
    ///
    /// Workflow:
    ///   1. isolate the selected audio track with temporary solo/mute state,
    ///   2. render a temporary mono/stereo stem for the requested range,
    ///   3. call the native HOA encoder/preview decoder,
    ///   4. restore all track state and clean up the temporary stem.
    ///
    /// It never writes `[7.1] FL..SR` send envelopes or routes HOA audio through
    /// the 7.1 busses.
    /// </summary>
    public static class HoaRenderOrchestrator
    {
        private static readonly long NanosPerSecond = 10_000_000L;
        private static readonly long PreviewWindowNanos = 8L * NanosPerSecond;

        /// <summary>Renders the chosen AmbiX target to <paramref name="outputPath"/>.</summary>
        public static HoaRenderResult RenderMaster(
            WorkspaceContext context,
            HoaSpatialState state,
            string outputPath,
            Action<string> progress)
        {
            return Run(context, state, outputPath, false, progress);
        }

        /// <summary>
        /// Renders a short 2-channel preview WAV and returns its path. The caller
        /// is responsible for playing it (for example with SoundPlayer).
        /// </summary>
        public static HoaRenderResult RenderPreview(
            WorkspaceContext context,
            HoaSpatialState state,
            Action<string> progress)
        {
            string previewPath = Path.Combine(
                Path.GetTempPath(),
                "TellTale7PointOne_HOA_preview_" +
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                ".wav");

            return Run(context, state, previewPath, true, progress);
        }

        private static HoaRenderResult Run(
            WorkspaceContext context,
            HoaSpatialState state,
            string outputPath,
            bool preview,
            Action<string> progress)
        {
            Action<string> report = progress ?? delegate { };
            var result = new HoaRenderResult
            {
                OutputPath = outputPath,
                IsPreview = preview
            };

            if (context == null)
            {
                result.Message = "HOA render context is null.";
                return result;
            }

            Vegas vegas = context.Vegas;
            Project project = context.GetProject();
            if (vegas == null || project == null)
            {
                result.Message = "No active VEGAS project.";
                return result;
            }

            if (state == null)
            {
                result.Message = "HOA state is null.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                result.Message = "Choose an HOA output path.";
                return result;
            }

            bool isolate = state.SourceMode == HoaSourceMode.SelectedTrack;
            AudioTrack track = isolate
                ? ResolveAudioTrack(project, context.SelectedTrackIndex)
                : null;
            if (isolate && track == null)
            {
                result.Message = "Select an audio track before rendering HOA audio.";
                return result;
            }

            int order = (int)state.Order;
            int channels = HoaSpatialMath.ChannelCount(order);
            if (channels <= 0)
            {
                result.Message = "Unsupported HOA order.";
                return result;
            }

            string extension = Path.GetExtension(outputPath) ?? string.Empty;
            string normalizedExtension = extension.ToLowerInvariant();
            if (!preview)
            {
                if (normalizedExtension != ".wav" &&
                    normalizedExtension != ".w64" &&
                    normalizedExtension != ".flac")
                {
                    result.Message = "HOA master output must be .wav, .w64, or .flac.";
                    return result;
                }

                if (normalizedExtension == ".flac" && channels > 8)
                {
                    result.Message =
                        "FLAC supports at most 8 channels. Choose WAV or W64 for " +
                        "2nd/3rd order AmbiX masters.";
                    return result;
                }
            }
            else if (normalizedExtension != ".wav")
            {
                result.Message = "HOA preview output must be a .wav file.";
                return result;
            }

            long startNanos;
            long lengthNanos;
            string rangeError = ResolveRange(context, project, preview,
                out startNanos, out lengthNanos);
            if (rangeError != null)
            {
                result.Message = rangeError;
                return result;
            }

            RenderTemplate template;
            string stemExtension;
            string templateError = FindStemTemplate(vegas, project,
                out template, out stemExtension);
            if (templateError != null)
            {
                result.Message = templateError;
                return result;
            }

            string stemPath = Path.Combine(
                Path.GetTempPath(),
                "TellTale7PointOne_hoa_stem_" + Guid.NewGuid().ToString("N") +
                stemExtension);

            List<TrackState> trackStates = isolate
                ? TrackState.Capture(project)
                : new List<TrackState>();
            List<BusState> busStates = isolate
                ? BusState.Capture(project)
                : new List<BusState>();
            UndoBlock undoBlock = null;
            bool rendered = false;

            try
            {
                report("HOA source: " + (isolate
                    ? DescribeTrack(track)
                    : "Full Mix (all tracks)"));
                report("HOA range: " + Timecode.FromNanos(startNanos) + " -> " +
                    Timecode.FromNanos(startNanos + lengthNanos));
                report("Project audio: " +
                    project.Audio.SampleRate.ToString(CultureInfo.InvariantCulture) +
                    " Hz / " +
                    project.Audio.BitDepth.ToString(CultureInfo.InvariantCulture) +
                    "-bit; template: " + template.Name);

                if (isolate)
                {
                    undoBlock = new UndoBlock(project,
                        preview ? "HOA Preview Source" : "HOA Export Source");
                    ApplyIsolation(project, track);
                    ApplyBusIsolation(project, track);
                    vegas.WaitForIdle();
                }

                report("Rendering HOA source stem...");
                RenderStatus status = vegas.Render(
                    stemPath,
                    template,
                    Timecode.FromNanos(startNanos),
                    Timecode.FromNanos(lengthNanos));

                if (status != RenderStatus.Complete || !File.Exists(stemPath))
                {
                    result.Message = "HOA source stem render failed: " + status +
                        " (template: " + template.Name + ")";
                    return result;
                }

                rendered = true;
            }
            catch (Exception ex)
            {
                result.Message = "HOA source stem render failed: " + ex.Message;
                return result;
            }
            finally
            {
                RestoreIsolation(trackStates);
                RestoreBuses(busStates);
                if (undoBlock != null)
                {
                    try
                    {
                        // The solo/mute change was temporary; do not leave an
                        // undo entry behind.
                        undoBlock.Cancel = true;
                        undoBlock.Dispose();
                    }
                    catch (Exception)
                    {
                        // Restoring the audio state already succeeded above.
                    }
                }

                try
                {
                    vegas.WaitForIdle();
                }
                catch (Exception)
                {
                    // Best effort after the render.
                }
            }

            if (!rendered)
            {
                result.Message = "HOA source stem was not produced.";
                return result;
            }

            try
            {
                report(preview
                    ? "Encoding " + PreviewLabel(state.PreviewMode) + " preview..."
                    : "Encoding AmbiX master (" + channels + " ch)...");

                var renderOptions = new HoaRenderOptions
                {
                    order = (uint)order,
                    azimuth_deg = (float)state.AzimuthDegrees,
                    elevation_deg = (float)state.ElevationDegrees,
                    distance = (float)state.Distance,
                    spread = (float)state.Spread,
                    distance_model = (uint)state.DistanceModel,
                    air_absorption = (float)state.AirAbsorption,
                    head_model = (uint)state.HeadModel,
                    head_width = (float)state.HeadWidth,
                    room_amount = (float)state.RoomAmount,
                    room_size = (float)state.RoomSize,
                    preview_mode = PreviewModeValue(state.PreviewMode)
                };

                int nativeStatus = preview
                    ? NativeBridge.RenderHoaPreviewEx(
                        renderOptions, stemPath, outputPath)
                    : NativeBridge.RenderHoaMasterEx(
                        renderOptions, stemPath, outputPath);

                result.NativeStatus = nativeStatus;
                if (nativeStatus != NativeBridge.HOA_RENDER_OK)
                {
                    result.Message = NativeBridge.DescribeHoaRenderStatus(nativeStatus);
                    return result;
                }

                result.Success = true;
                if (preview)
                {
                    result.Analysis = AnalyzePreview(outputPath);
                    string balance = result.Analysis == null
                        ? null
                        : result.Analysis.BalanceText;
                    result.Message = "HOA preview ready" +
                        (string.IsNullOrEmpty(balance)
                            ? string.Empty
                            : " (" + balance + ")") +
                        ": " + outputPath;
                }
                else
                {
                    string outputExtension =
                        (Path.GetExtension(outputPath) ?? string.Empty)
                        .TrimStart('.')
                        .ToUpperInvariant();
                    result.Message = "HOA AmbiX master written: " + outputPath +
                        " (" + channels + " ch, 24-bit " + outputExtension + ")";
                }

                report(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "HOA encode failed: " + ex.Message;
                return result;
            }
            finally
            {
                try
                {
                    if (File.Exists(stemPath))
                    {
                        File.Delete(stemPath);
                    }
                }
                catch (IOException)
                {
                    // Best effort temporary-stem cleanup.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best effort temporary-stem cleanup.
                }
            }
        }

        private static AudioTrack ResolveAudioTrack(Project project, int index)
        {
            if (project == null || index < 0)
            {
                return null;
            }

            try
            {
                if (index >= project.Tracks.Count)
                {
                    return null;
                }

                Track track = project.Tracks[index];
                if (track is VideoTrack)
                {
                    return null;
                }

                return track as AudioTrack;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveRange(
            WorkspaceContext context,
            Project project,
            bool preview,
            out long startNanos,
            out long lengthNanos)
        {
            if (context.TryGetLoopRegion(out startNanos, out lengthNanos) &&
                lengthNanos > 0)
            {
                if (preview && lengthNanos > PreviewWindowNanos)
                {
                    lengthNanos = PreviewWindowNanos;
                }

                return null;
            }

            long projectLength = project.Length == null ? 0L : project.Length.Nanos;
            if (projectLength <= 0L)
            {
                startNanos = 0L;
                lengthNanos = 0L;
                return "The VEGAS timeline is empty.";
            }

            if (!preview)
            {
                startNanos = 0L;
                lengthNanos = projectLength;
                return null;
            }

            Timecode playhead = context.GetPlayhead();
            startNanos = playhead == null ? 0L : playhead.Nanos;
            if (startNanos < 0L)
            {
                startNanos = 0L;
            }

            if (startNanos >= projectLength)
            {
                startNanos = Math.Max(0L, projectLength - PreviewWindowNanos);
            }

            lengthNanos = Math.Min(PreviewWindowNanos, projectLength - startNanos);
            if (lengthNanos <= 0L)
            {
                startNanos = 0L;
                lengthNanos = Math.Min(PreviewWindowNanos, projectLength);
            }

            return lengthNanos > 0L
                ? null
                : "No audio range is available for the HOA preview.";
        }

        private static string FindStemTemplate(
            Vegas vegas,
            Project project,
            out RenderTemplate template,
            out string extension)
        {
            template = null;
            extension = ".wav";

            if (vegas == null || project == null)
            {
                return "No VEGAS project is available.";
            }

            RenderTemplate best = null;
            string bestExtension = ".wav";
            int bestScore = int.MinValue;

            try
            {
                foreach (Renderer renderer in vegas.Renderers)
                {
                    if (!renderer.SupportsAudio || renderer.SupportsVideo)
                    {
                        // The HOA stem is audio-only; never select a video
                        // container just because it carries an audio stream.
                        continue;
                    }

                    foreach (RenderTemplate candidate in renderer.Templates)
                    {
                        int channels = candidate.AudioChannelCount;
                        if (channels <= 0 ||
                            candidate.VideoWidth > 0 ||
                            candidate.VideoHeight > 0)
                        {
                            continue;
                        }

                        string candidateExtension = TemplateExtension(candidate);
                        if (candidateExtension == null)
                        {
                            continue;
                        }

                        int score = 0;
                        if (channels == 2)
                        {
                            score += 200;
                        }
                        else if (channels == 1)
                        {
                            score += 190;
                        }
                        else if (channels <= 8)
                        {
                            score += 20;
                        }

                        if (candidate.AudioSampleRate == project.Audio.SampleRate)
                        {
                            score += 50;
                        }

                        string name = renderer.Name ?? string.Empty;
                        if (name.IndexOf("Wave 64", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 30;
                        }
                        else if (name.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 20;
                        }

                        if (candidate.AudioBitsPerSample >= 24)
                        {
                            score += 5;
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                            bestExtension = candidateExtension;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return "Unable to enumerate VEGAS audio render templates: " + ex.Message;
            }

            if (best == null)
            {
                return "No mono/stereo audio render template was found. " +
                    "Add a Wave or Wave 64 template and retry.";
            }

            template = best;
            extension = bestExtension;
            return null;
        }

        private static string TemplateExtension(RenderTemplate template)
        {
            try
            {
                string[] extensions = template.FileExtensions;
                if (extensions != null)
                {
                    foreach (string value in extensions)
                    {
                        if (string.Equals(value, ".w64", StringComparison.OrdinalIgnoreCase))
                        {
                            return ".w64";
                        }
                    }

                    foreach (string value in extensions)
                    {
                        if (string.Equals(value, ".wav", StringComparison.OrdinalIgnoreCase))
                        {
                            return ".wav";
                        }
                    }
                }

                string format = template.FileFormat ?? string.Empty;
                if (format.IndexOf("64", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".w64";
                }

                if (format.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".wav";
                }
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        private static void ApplyIsolation(Project project, AudioTrack selected)
        {
            foreach (Track track in project.Tracks)
            {
                try
                {
                    bool target = selected != null && track.Index == selected.Index;
                    track.Solo = target;
                    track.Mute = !target;
                }
                catch (Exception)
                {
                    // A track that cannot be toggled is left as-is.
                }
            }
        }

        private static void RestoreIsolation(List<TrackState> states)
        {
            if (states == null)
            {
                return;
            }

            foreach (TrackState state in states)
            {
                try
                {
                    state.Track.Solo = state.Solo;
                    state.Track.Mute = state.Mute;
                }
                catch (Exception)
                {
                    // The track may have been deleted during the render.
                }
            }
        }

        /// <summary>
        /// Temporarily mutes the canonical `[7.1]` bus returns so the HOA stem
        /// captures the selected track rather than its 7.1 send spatialization.
        /// The selected track's own main bus is left audible.
        /// </summary>
        private static void ApplyBusIsolation(Project project, AudioTrack selected)
        {
            if (project == null)
            {
                return;
            }

            BusTrack mainBus = null;
            try
            {
                mainBus = selected == null ? null : selected.BusTrack;
            }
            catch (Exception)
            {
                mainBus = null;
            }

            try
            {
                foreach (BusTrack item in project.BusTracks)
                {
                    var bus = item as AudioBusTrack;
                    if (bus == null)
                    {
                        continue;
                    }

                    string name = bus.Name ?? string.Empty;
                    if (name.IndexOf("[7.1]", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    bool isMainBus = mainBus != null &&
                        (bus.Index == mainBus.Index ||
                         string.Equals(bus.Name, mainBus.Name, StringComparison.Ordinal));
                    if (isMainBus)
                    {
                        continue;
                    }

                    try
                    {
                        bus.Solo = false;
                        bus.Mute = true;
                    }
                    catch (Exception)
                    {
                        // Leave a bus that cannot be toggled as-is.
                    }
                }
            }
            catch (Exception)
            {
                // Bus enumeration failure must not abort the stem render.
            }
        }

        private static void RestoreBuses(List<BusState> states)
        {
            if (states == null)
            {
                return;
            }

            foreach (BusState state in states)
            {
                try
                {
                    state.Bus.Solo = state.Solo;
                    state.Bus.Mute = state.Mute;
                }
                catch (Exception)
                {
                    // The bus may have been removed during the render.
                }
            }
        }

        private static string DescribeTrack(AudioTrack track)
        {
            string name = string.IsNullOrWhiteSpace(track.Name)
                ? "Track " + (track.Index + 1)
                : "Track " + (track.Index + 1) + " - " + track.Name;
            return name;
        }

        private static string PreviewLabel(HoaPreviewMode mode)
        {
            switch (mode)
            {
                case HoaPreviewMode.StereoDownmix:
                    return "stereo";
                case HoaPreviewMode.Bypass:
                    return "bypass";
                default:
                    return "binaural";
            }
        }

        private static int PreviewModeValue(HoaPreviewMode mode)
        {
            switch (mode)
            {
                case HoaPreviewMode.StereoDownmix:
                    return NativeBridge.HoaPreviewStereo;
                case HoaPreviewMode.Bypass:
                    return NativeBridge.HoaPreviewBypass;
                default:
                    return NativeBridge.HoaPreviewBinaural;
            }
        }

        /// <summary>
        /// Reads the 16-bit stereo preview and returns its L/R RMS balance. This
        /// is diagnostic only; it never affects playback or project state.
        /// </summary>
        private static string DescribePreviewBalance(string path)
        {
            HoaPreviewAnalysis analysis = AnalyzePreview(path);
            return analysis == null ? null : analysis.BalanceText;
        }

        /// <summary>
        /// Reads the 16-bit stereo preview and returns RMS, peak, and L/R
        /// correlation. This is diagnostic only; it never affects playback.
        /// </summary>
        private static HoaPreviewAnalysis AnalyzePreview(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 44 ||
                    Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                    Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                {
                    return null;
                }

                int channels = 0;
                int bits = 0;
                int dataOffset = -1;
                int dataLength = 0;
                int offset = 12;

                while (offset + 8 <= bytes.Length)
                {
                    string id = Encoding.ASCII.GetString(bytes, offset, 4);
                    int size = BitConverter.ToInt32(bytes, offset + 4);
                    int payload = offset + 8;

                    if (id == "fmt " && payload + 16 <= bytes.Length)
                    {
                        channels = BitConverter.ToInt16(bytes, payload + 2);
                        bits = BitConverter.ToInt16(bytes, payload + 14);
                    }
                    else if (id == "data")
                    {
                        dataOffset = payload;
                        dataLength = Math.Min(size, bytes.Length - payload);
                        break;
                    }

                    offset = payload + size + (size & 1);
                }

                if (channels != 2 || bits != 16 || dataOffset < 0 ||
                    dataLength < 4)
                {
                    return null;
                }

                int frames = dataLength / 4;
                double sumLeft = 0.0;
                double sumRight = 0.0;
                double sumLeftRight = 0.0;
                double peak = 0.0;
                for (int index = 0; index < frames; index++)
                {
                    short left = BitConverter.ToInt16(
                        bytes, dataOffset + index * 4);
                    short right = BitConverter.ToInt16(
                        bytes, dataOffset + index * 4 + 2);
                    sumLeft += (double)left * left;
                    sumRight += (double)right * right;
                    sumLeftRight += (double)left * right;
                    double absolute = Math.Max(Math.Abs((double)left),
                        Math.Abs((double)right));
                    if (absolute > peak)
                    {
                        peak = absolute;
                    }
                }

                double rmsLeft = Math.Sqrt(sumLeft / Math.Max(1, frames)) /
                    32768.0;
                double rmsRight = Math.Sqrt(sumRight / Math.Max(1, frames)) /
                    32768.0;
                double correlation = 0.0;
                if (sumLeft > 0.0 && sumRight > 0.0)
                {
                    correlation = sumLeftRight / Math.Sqrt(sumLeft * sumRight);
                    correlation = Math.Max(-1.0, Math.Min(1.0, correlation));
                }

                return new HoaPreviewAnalysis
                {
                    LeftRmsDb = ToDb(rmsLeft),
                    RightRmsDb = ToDb(rmsRight),
                    PeakDb = ToDb(peak / 32768.0),
                    Correlation = correlation,
                    BalanceText = string.Format(
                        CultureInfo.InvariantCulture,
                        "L {0:0.0} dB / R {1:0.0} dB · corr {2:+0.00;-0.00;0.00}",
                        ToDb(rmsLeft),
                        ToDb(rmsRight),
                        correlation)
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double ToDb(double linear)
        {
            if (linear <= 0.0000001)
            {
                return -96.0;
            }

            return Math.Max(-96.0, 20.0 * Math.Log10(linear));
        }

        private sealed class BusState
        {
            private BusState(AudioBusTrack bus, bool solo, bool mute)
            {
                Bus = bus;
                Solo = solo;
                Mute = mute;
            }

            public AudioBusTrack Bus { get; private set; }
            public bool Solo { get; private set; }
            public bool Mute { get; private set; }

            public static List<BusState> Capture(Project project)
            {
                var states = new List<BusState>();
                if (project == null)
                {
                    return states;
                }

                try
                {
                    foreach (BusTrack item in project.BusTracks)
                    {
                        var bus = item as AudioBusTrack;
                        if (bus == null)
                        {
                            continue;
                        }

                        try
                        {
                            states.Add(new BusState(bus, bus.Solo, bus.Mute));
                        }
                        catch (Exception)
                        {
                            // Skip a bus whose state cannot be read.
                        }
                    }
                }
                catch (Exception)
                {
                    // The caller will render without a complete bus snapshot.
                }

                return states;
            }
        }

        private sealed class TrackState
        {
            private TrackState(Track track, bool solo, bool mute)
            {
                Track = track;
                Solo = solo;
                Mute = mute;
            }

            public Track Track { get; private set; }
            public bool Solo { get; private set; }
            public bool Mute { get; private set; }

            public static List<TrackState> Capture(Project project)
            {
                var states = new List<TrackState>();
                if (project == null)
                {
                    return states;
                }

                try
                {
                    foreach (Track track in project.Tracks)
                    {
                        try
                        {
                            states.Add(new TrackState(track, track.Solo, track.Mute));
                        }
                        catch (Exception)
                        {
                            // Skip a track whose state cannot be read.
                        }
                    }
                }
                catch (Exception)
                {
                    // The caller will render without a complete snapshot.
                }

                return states;
            }
        }
    }
}
