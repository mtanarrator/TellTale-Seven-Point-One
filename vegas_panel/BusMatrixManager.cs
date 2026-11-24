using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ScriptPortal.Vegas;

namespace VegasSurround71
{
    /// <summary>
    /// Canonical 7.1 bus topology contract shared by the C# host and the Rust core.
    /// Bus index order is fixed: FL, FR, FC, LFE, BL, BR, SL, SR.
    /// </summary>
    public static class Bus71Layout
    {
        public const int BusCount = 8;

        /// <summary>User-visible bus labels, indexed by canonical bus index.</summary>
        public static readonly string[] BusNames =
        {
            "[7.1] FL",
            "[7.1] FR",
            "[7.1] FC",
            "[7.1] LFE",
            "[7.1] BL",
            "[7.1] BR",
            "[7.1] SL",
            "[7.1] SR"
        };

        /// <summary>SMPTE channel labels, indexed by canonical bus index.</summary>
        public static readonly string[] ChannelLabels =
        {
            "FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"
        };

        /// <summary>
        /// Speaker mask flags used by VEGAS multichannel render mapping. The order
        /// matches <see cref="BusNames"/> (FL, FR, FC, LFE, BL, BR, SL, SR).
        /// </summary>
        public static readonly AudioChannelFlags[] ChannelFlags =
        {
            AudioChannelFlags.SpeakerFrontLeft,
            AudioChannelFlags.SpeakerFrontRight,
            AudioChannelFlags.SpeakerFrontCenter,
            AudioChannelFlags.SpeakerLowFrequency,
            AudioChannelFlags.SpeakerBackLeft,
            AudioChannelFlags.SpeakerBackRight,
            AudioChannelFlags.SpeakerSideLeft,
            AudioChannelFlags.SpeakerSideRight
        };

        /// <summary>
        /// Extracts the A-Z letter from VEGAS' read-only bus name ("Bus A".."Bus Z").
        /// Returns -1 when the name does not encode a valid bus letter.
        /// </summary>
        public static int GetBusLetterIndex(string busName)
        {
            if (string.IsNullOrEmpty(busName))
            {
                return -1;
            }

            string trimmed = busName.Trim();
            char last = char.ToUpperInvariant(trimmed[trimmed.Length - 1]);
            if (last < 'A' || last > 'Z')
            {
                return -1;
            }

            return last - 'A';
        }

        /// <summary>
        /// Maps a provisioned audio bus to its real send envelope
        /// (BusA..BusZ), using the bus's actual VEGAS letter rather than its
        /// canonical 0..7 index. This is required when a project already contains
        /// busses: the eight 7.1 busses may be named Bus C..Bus J.
        /// </summary>
        public static EnvelopeType GetSendEnvelopeType(AudioBusTrack bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            int letterIndex = GetBusLetterIndex(bus.Name);
            if (letterIndex < 0)
            {
                throw new InvalidOperationException(
                    "Bus '" + bus.Name + "' does not map to an A-Z send envelope.");
            }

            return (EnvelopeType)((int)EnvelopeType.BusA + letterIndex);
        }
    }

    /// <summary>
    /// One track-to-bus send. In ScriptPortal.Vegas a send is represented by the
    /// track's <c>EnvelopeType.BusA..BusH</c> automation envelope; the SDK has no
    /// <c>TrackSend.Level</c> object.
    /// </summary>
    public sealed class BusSendRoute
    {
        internal BusSendRoute(
            int busIndex,
            string canonicalName,
            string busTrackName,
            EnvelopeType envelopeType,
            Envelope envelope)
        {
            BusIndex = busIndex;
            BusName = canonicalName;
            BusTrackName = busTrackName;
            EnvelopeType = envelopeType;
            Envelope = envelope;
        }

        public int BusIndex { get; private set; }
        public string BusName { get; private set; }
        public string BusTrackName { get; private set; }
        public EnvelopeType EnvelopeType { get; private set; }
        public Envelope Envelope { get; private set; }

        /// <summary>
        /// Writes a linear 0..1 send gain. When <paramref name="writeKeyframe"/>
        /// is true the point at <paramref name="cursor"/> is inserted/updated while
        /// preserving existing automation; otherwise the send becomes a single
        /// held point (static gain) so the whole timeline uses the new pan.
        /// </summary>
        public void WriteLevel(Timecode cursor, float linearGain, bool writeKeyframe)
        {
            if (cursor == null)
            {
                throw new ArgumentNullException(nameof(cursor));
            }

            double value = BusMatrixManager.LinearGainToEnvelopeValue(
                linearGain, Envelope.Min, Envelope.Max);

            if (writeKeyframe)
            {
                EnvelopePoint point = Envelope.Points.GetPointAtX(cursor);
                if (point != null && point.X.Nanos == cursor.Nanos)
                {
                    point.Y = value;
                }
                else
                {
                    Envelope.Points.Add(new EnvelopePoint(cursor, value, CurveType.Linear));
                }
            }
            else
            {
                // No direct "send level" property exists, so a static send is a
                // single held point at project start. VEGAS keeps an immutable
                // anchor point at time zero, so update it instead of adding a
                // duplicate (which throws).
                Envelope.Points.Clear();
                Timecode zero = Timecode.FromNanos(0);
                EnvelopePoint anchor = Envelope.Points.GetPointAtX(zero);
                if (anchor != null)
                {
                    anchor.Y = value;
                    anchor.Curve = CurveType.None;
                }
                else
                {
                    Envelope.Points.Add(new EnvelopePoint(zero, value, CurveType.None));
                }
            }
        }
    }

    /// <summary>
    /// A live panning session that writes the eight bus-send envelopes directly
    /// from the extension UI thread. This is safe during playback: unlike
    /// <c>Vegas.RunScriptText</c>, direct envelope-point mutation does not stop
    /// the transport, so the VEGAS Mixing Console and bus meters follow the puck
    /// in real time.
    /// </summary>
    public sealed class LivePanSession : IDisposable
    {
        private readonly Vegas _vegas;
        private readonly Project _project;
        private readonly AudioTrack _track;
        private readonly Envelope[] _envelopes;
        private readonly bool[] _staticCollapsed;
        private UndoBlock _undoBlock;
        private bool _hasWritten;
        private bool _disposed;

        private LivePanSession(
            Vegas vegas,
            Project project,
            AudioTrack track,
            Envelope[] envelopes,
            UndoBlock undoBlock)
        {
            _vegas = vegas;
            _project = project;
            _track = track;
            _envelopes = envelopes;
            _staticCollapsed = new bool[envelopes.Length];
            _undoBlock = undoBlock;
        }

        /// <summary>The audio track whose sends this session drives.</summary>
        public AudioTrack Track
        {
            get { return _track; }
        }

        /// <summary>
        /// Resolves the canonical busses and send envelopes. Existing envelopes
        /// are reused; missing envelopes are created inside one undo block.
        /// </summary>
        public static LivePanSession TryBegin(
            Vegas vegas, AudioTrack track, out string error)
        {
            error = null;
            if (vegas == null)
            {
                error = "VEGAS instance is null.";
                return null;
            }

            if (track == null)
            {
                error = HostScriptBridge.TrackRoutingGuidance;
                return null;
            }

            Project project = vegas.Project;
            if (project == null)
            {
                error = "No active VEGAS project.";
                return null;
            }

            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);
            if (busses == null)
            {
                error = "Initialize the 7.1 busses before live panning.";
                return null;
            }

            for (int i = 0; i < busses.Length; i++)
            {
                try
                {
                    Bus71Layout.GetSendEnvelopeType(busses[i]);
                }
                catch (InvalidOperationException ex)
                {
                    error = ex.Message;
                    return null;
                }
            }

            UndoBlock undoBlock = null;
            try
            {
                // Every extension-side project mutation must be wrapped in an
                // undo block. One block covers the whole drag, so VEGAS gets a
                // single "7.1 Live Pan" undo entry instead of one per tick.
                undoBlock = new UndoBlock(project, "7.1 Live Pan");
                BusSendRoute[] routes = BusMatrixManager.EnsureBusSends(track, busses);

                var envelopes = new Envelope[routes.Length];
                for (int i = 0; i < routes.Length; i++)
                {
                    envelopes[i] = routes[i].Envelope;
                }

                return new LivePanSession(
                    vegas, project, track, envelopes, undoBlock);
            }
            catch (Exception ex)
            {
                if (undoBlock != null)
                {
                    try
                    {
                        undoBlock.Cancel = true;
                        undoBlock.Dispose();
                    }
                    catch (Exception)
                    {
                        // Best effort.
                    }
                }

                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Writes the current puck gains to all eight sends. In keyframe mode the
        /// point is inserted/updated at <paramref name="cursor"/>; in static mode
        /// the envelope is collapsed to one held point on the first write.
        /// </summary>
        public bool Apply(
            Timecode cursor,
            float x,
            float y,
            float lfeSend,
            float spread,
            bool writeKeyframes,
            bool refreshUi,
            out string error)
        {
            error = null;
            if (_disposed)
            {
                error = "Live pan session is disposed.";
                return false;
            }

            if (cursor == null)
            {
                error = "Transport cursor is null.";
                return false;
            }

            try
            {
                ChannelGains71 gains = NativeBridge.calculate_71_gains(
                    x, y, lfeSend, spread);
                float[] levels = gains.ToArray();

                for (int i = 0; i < _envelopes.Length; i++)
                {
                    WriteValue(
                        _envelopes[i], cursor, levels[i], writeKeyframes,
                        ref _staticCollapsed[i]);
                }

                _project.ForceEnvelopeSync();
                _hasWritten = true;

                if (refreshUi)
                {
                    try
                    {
                        _vegas.UpdateUI();
                    }
                    catch (Exception)
                    {
                        // Repainting is best-effort; audio has already changed.
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Reads the post-write send values for logging.</summary>
        public string Readback(Timecode cursor)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _envelopes.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0}={1:0.####}",
                    Bus71Layout.ChannelLabels[i],
                    _envelopes[i].ValueAt(cursor));
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_undoBlock != null)
            {
                try
                {
                    if (!_hasWritten)
                    {
                        // No project data was changed (for example a click
                        // without a drag); do not leave an empty undo entry.
                        _undoBlock.Cancel = true;
                    }
                }
                finally
                {
                    _undoBlock.Dispose();
                    _undoBlock = null;
                }
            }
        }

        private static void WriteValue(
            Envelope envelope,
            Timecode cursor,
            float linearGain,
            bool writeKeyframe,
            ref bool staticCollapsed)
        {
            double value = BusMatrixManager.LinearGainToEnvelopeValue(
                linearGain, envelope.Min, envelope.Max);

            if (writeKeyframe)
            {
                EnvelopePoint point = envelope.Points.GetPointAtX(cursor);
                if (point != null && point.X.Nanos == cursor.Nanos)
                {
                    point.Y = value;
                    point.Curve = CurveType.Linear;
                }
                else
                {
                    envelope.Points.Add(
                        new EnvelopePoint(cursor, value, CurveType.Linear));
                }

                staticCollapsed = false;
                return;
            }

            if (!staticCollapsed)
            {
                // Static mode means a single held value for the whole timeline.
                // Set the time-zero anchor first, then remove any later points,
                // so playback never sees a momentarily empty envelope.
                Timecode zero = Timecode.FromNanos(0);
                EnvelopePoint anchor = envelope.Points.GetPointAtX(zero);
                if (anchor == null)
                {
                    envelope.Points.Add(
                        new EnvelopePoint(zero, value, CurveType.None));
                }
                else
                {
                    anchor.Y = value;
                    anchor.Curve = CurveType.None;
                }

                for (int i = envelope.Points.Count - 1; i >= 0; i--)
                {
                    EnvelopePoint existing = envelope.Points[i];
                    if (existing != null && existing.X.Nanos != 0)
                    {
                        envelope.Points.Remove(existing);
                    }
                }

                staticCollapsed = true;
                return;
            }

            Timecode anchorTime = Timecode.FromNanos(0);
            EnvelopePoint staticAnchor = envelope.Points.GetPointAtX(anchorTime);
            if (staticAnchor != null)
            {
                staticAnchor.Y = value;
                staticAnchor.Curve = CurveType.None;
            }
            else
            {
                envelope.Points.Add(
                    new EnvelopePoint(anchorTime, value, CurveType.None));
            }
        }
    }

    /// <summary>
    /// Provides 8-bus provisioning and VBAP send automation for VEGAS projects.
    /// </summary>
    public static class BusMatrixManager
    {
        /// <summary>
        /// Whether this VEGAS build accepts the mono/stereo/surround render
        /// mapping setters. VEGAS Pro 2027 build 51 throws
        /// <see cref="NotSupportedException"/> from
        /// <c>AudioBusTrack.SetRenderEnabled</c>; provisioning still succeeds and
        /// the limitation is reported by the host validation.
        /// </summary>
        public static bool RenderMappingSupported { get; private set; }

        /// <summary>Details of the render-mapping probe / limitation.</summary>
        public static string RenderMappingStatus { get; private set; }

        /// <summary>
        /// Locates (or creates) the eight canonical 7.1 audio busses and configures
        /// them for discrete mono multichannel rendering. All changes are wrapped
        /// in a single undo block.
        /// </summary>
        public static AudioBusTrack[] Provision71BusTopology(Vegas vegas)
        {
            if (vegas == null)
            {
                throw new ArgumentNullException(nameof(vegas));
            }

            Project project = vegas.Project;
            if (project == null)
            {
                throw new InvalidOperationException("VEGAS has no active project.");
            }

            using (project.CreateBackgroundProjectActiveState())
            using (new UndoBlock(project, "Initialize 7.1 Surround Busses"))
            {
                EnsureAssignableBusMode(vegas);
                return Ensure71BusTopology(project);
            }
        }

        /// <summary>
        /// Applies VBAP gains to a track by driving its eight bus-send envelopes.
        /// The track fader (<c>track.Volume</c>), pan trim, and mute/solo state are
        /// never modified, preserving the existing pre/post-fader balance.
        /// </summary>
        public static ChannelGains71 ApplyTrackPanning(
            Vegas vegas,
            AudioTrack track,
            Timecode cursor,
            float x,
            float y,
            float lfeSend,
            float spread,
            bool writeKeyframes)
        {
            if (vegas == null)
            {
                throw new ArgumentNullException(nameof(vegas));
            }

            if (track == null)
            {
                throw new ArgumentNullException(nameof(track));
            }

            if (cursor == null)
            {
                throw new ArgumentNullException(nameof(cursor));
            }

            if (vegas.Project == null)
            {
                throw new InvalidOperationException("VEGAS has no active project.");
            }

            ChannelGains71 gains = NativeBridge.calculate_71_gains(x, y, lfeSend, spread);
            float[] levels = gains.ToArray();

            using (vegas.Project.CreateBackgroundProjectActiveState())
            using (new UndoBlock(vegas.Project, "Apply 7.1 Surround Pan"))
            {
                EnsureAssignableBusMode(vegas);
                AudioBusTrack[] busses = Ensure71BusTopology(vegas.Project);

                BusSendRoute[] sends = EnsureBusSends(track, busses);
                for (int i = 0; i < Bus71Layout.BusCount; i++)
                {
                    sends[i].WriteLevel(cursor, levels[i], writeKeyframes);
                }

                // Commit automation edits so they participate in undo/redo and
                // are reflected by the VEGAS envelope engine.
                vegas.Project.ForceEnvelopeSync();
            }

            return gains;
        }

        /// <summary>
        /// Clears all 7.1 bus-send envelope points from one track inside a
        /// single undo transaction. Returns the number of points removed.
        /// </summary>
        public static int ClearTrackKeyframes(Vegas vegas, AudioTrack track)
        {
            if (vegas == null)
            {
                throw new ArgumentNullException(nameof(vegas));
            }

            if (track == null)
            {
                throw new ArgumentNullException(nameof(track));
            }

            Project project = vegas.Project;
            if (project == null)
            {
                throw new InvalidOperationException("VEGAS has no active project.");
            }

            AudioBusTrack[] busses = FindCanonicalBusses(project);
            if (busses == null)
            {
                throw new InvalidOperationException(
                    "Click 'Setup 7.1 Busses' to route tracks first.");
            }

            int cleared = 0;
            using (project.CreateBackgroundProjectActiveState())
            using (new UndoBlock(project, "Reset 7.1 Track Keyframes"))
            {
                cleared = ClearTrackKeyframesInTransaction(track, busses);
                project.ForceEnvelopeSync();
            }

            return cleared;
        }

        /// <summary>
        /// Clears all 7.1 bus-send envelope points from every audio track in
        /// one undo transaction. Returns the number of points removed.
        /// </summary>
        public static int ClearAllTrackKeyframes(Vegas vegas)
        {
            if (vegas == null)
            {
                throw new ArgumentNullException(nameof(vegas));
            }

            Project project = vegas.Project;
            if (project == null)
            {
                throw new InvalidOperationException("VEGAS has no active project.");
            }

            AudioBusTrack[] busses = FindCanonicalBusses(project);
            if (busses == null)
            {
                return 0;
            }

            int cleared = 0;
            using (project.CreateBackgroundProjectActiveState())
            using (new UndoBlock(project, "Reset All 7.1 Track Keyframes"))
            {
                foreach (Track item in project.Tracks)
                {
                    var audio = item as AudioTrack;
                    if (audio != null)
                    {
                        cleared += ClearTrackKeyframesInTransaction(audio, busses);
                    }
                }

                project.ForceEnvelopeSync();
            }

            return cleared;
        }

        private static int ClearTrackKeyframesInTransaction(
            AudioTrack track, AudioBusTrack[] busses)
        {
            int cleared = 0;
            for (int i = 0; i < busses.Length; i++)
            {
                EnvelopeType type;
                try
                {
                    type = Bus71Layout.GetSendEnvelopeType(busses[i]);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                Envelope envelope = track.Envelopes.HasEnvelope(type)
                    ? track.Envelopes.FindByType(type)
                    : null;
                if (envelope == null)
                {
                    continue;
                }

                cleared += envelope.Points.Count;
                envelope.Points.Clear();
            }

            return cleared;
        }

        /// <summary>
        /// Applies a batch of keyframe pan points in a single undo transaction.
        /// Used by the playback-safe deferred commit queue.
        /// </summary>
        public static void ApplyTrackPanningBatch(
            Vegas vegas, AudioTrack track, PanPoint[] points)
        {
            if (vegas == null)
            {
                throw new ArgumentNullException(nameof(vegas));
            }

            if (track == null)
            {
                throw new ArgumentNullException(nameof(track));
            }

            if (points == null || points.Length == 0)
            {
                return;
            }

            Project project = vegas.Project;
            if (project == null)
            {
                throw new InvalidOperationException("VEGAS has no active project.");
            }

            using (project.CreateBackgroundProjectActiveState())
            using (new UndoBlock(project, "Apply 7.1 Surround Pan Batch"))
            {
                EnsureAssignableBusMode(vegas);
                AudioBusTrack[] busses = Ensure71BusTopology(project);
                BusSendRoute[] sends = EnsureBusSends(track, busses);

                foreach (PanPoint point in points)
                {
                    ChannelGains71 gains = NativeBridge.calculate_71_gains(
                        (float)point.X, (float)point.Y,
                        (float)point.Lfe, (float)point.Spread);
                    float[] levels = gains.ToArray();
                    Timecode cursor = Timecode.FromNanos(point.Nanos);
                    for (int i = 0; i < Bus71Layout.BusCount; i++)
                    {
                        sends[i].WriteLevel(cursor, levels[i], true);
                    }
                }

                project.ForceEnvelopeSync();
            }
        }

        /// <summary>
        /// Ensures the track owns a send envelope for every canonical bus, using
        /// each bus's real VEGAS letter (BusA..BusZ). Existing envelopes are
        /// reused, so user automation is preserved.
        /// </summary>
        public static BusSendRoute[] EnsureBusSends(Vegas vegas, AudioTrack track)
        {
            if (vegas == null)
            {
                throw new ArgumentNullException(nameof(vegas));
            }

            AudioBusTrack[] busses = FindCanonicalBusses(vegas.Project);
            if (busses == null)
            {
                throw new InvalidOperationException(
                    "The canonical [7.1] bus topology is not provisioned.");
            }

            return EnsureBusSends(track, busses);
        }

        /// <summary>
        /// Ensures the track owns a send envelope for every supplied canonical bus.
        /// </summary>
        public static BusSendRoute[] EnsureBusSends(
            AudioTrack track, AudioBusTrack[] canonicalBusses)
        {
            if (track == null)
            {
                throw new ArgumentNullException(nameof(track));
            }

            if (canonicalBusses == null || canonicalBusses.Length != Bus71Layout.BusCount)
            {
                throw new ArgumentException(
                    "Exactly eight canonical busses are required.", nameof(canonicalBusses));
            }

            var routes = new BusSendRoute[Bus71Layout.BusCount];

            for (int i = 0; i < Bus71Layout.BusCount; i++)
            {
                AudioBusTrack bus = canonicalBusses[i];
                if (bus == null)
                {
                    throw new InvalidOperationException(
                        "Canonical bus at index " + i + " is missing.");
                }

                EnvelopeType type = Bus71Layout.GetSendEnvelopeType(bus);
                Envelope envelope = track.Envelopes.HasEnvelope(type)
                    ? track.Envelopes.FindByType(type)
                    : null;

                if (envelope == null)
                {
                    if (!track.CanAddEnvelope(type))
                    {
                        throw new InvalidOperationException(
                            string.Format("Audio track '{0}' cannot accept the {1} send envelope.",
                                track.Name, type));
                    }

                    envelope = new Envelope(type);
                    track.Envelopes.Add(envelope);
                }

                routes[i] = new BusSendRoute(
                    i, Bus71Layout.BusNames[i], bus.Name, type, envelope);
            }

            return routes;
        }

        /// <summary>
        /// Returns the eight canonical audio busses in FL..SR order, or null when
        /// any are missing.
        /// </summary>
        public static AudioBusTrack[] FindCanonicalBusses(Project project)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            var existing = new List<AudioBusTrack>();
            foreach (BusTrack busTrack in project.BusTracks)
            {
                var audioBus = busTrack as AudioBusTrack;
                if (audioBus != null && audioBus.IsAudio() && !audioBus.IsMaster())
                {
                    existing.Add(audioBus);
                }
            }

            var result = new AudioBusTrack[Bus71Layout.BusCount];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = FindBus(existing, Bus71Layout.BusNames[i]);
                if (result[i] == null)
                {
                    return null;
                }
            }

            return result;
        }

        /// <summary>
        /// Normalizes the project's master bus mode to a valid value.
        /// </summary>
        /// <remarks>
        /// ScriptPortal.Vegas 2027 has no <c>AudioBusMode.Busses</c> or
        /// <c>MinimumBusCount</c>; assignable busses are enabled by creating
        /// <see cref="AudioBusTrack"/> instances (see <see cref="Provision71BusTopology"/>).
        /// Only the two documented modes, Stereo and Surround (5.1), exist, so an
        /// undefined value is normalized to Stereo without forcing 5.1 on the user.
        /// </remarks>
        public static void EnsureAssignableBusMode(Vegas vegas)
        {
            if (vegas == null)
            {
                throw new ArgumentNullException(nameof(vegas));
            }

            if (vegas.Project == null)
            {
                throw new InvalidOperationException("VEGAS has no active project.");
            }

            ProjectAudioProperties audio = vegas.Project.Audio;
            if (audio.MasterBusMode != AudioBusMode.Stereo &&
                audio.MasterBusMode != AudioBusMode.Surround)
            {
                audio.MasterBusMode = AudioBusMode.Stereo;
            }
        }

        /// <summary>
        /// Converts a linear 0..1 VBAP gain to the envelope's native value domain.
        /// VEGAS 2027 bus-send envelopes (<c>EnvelopeType.BusX</c>) are linear
        /// 0..2 with neutral 1.0 (1.0 = unity / 0 dB); they must NOT be decoded as
        /// dB. Volume/other envelopes may use a dB domain and are handled below.
        /// </summary>
        public static double LinearGainToEnvelopeValue(
            double linearGain, double envelopeMin, double envelopeMax)
        {
            if (double.IsNaN(linearGain))
            {
                linearGain = 0.0;
            }

            linearGain = Math.Max(0.0, Math.Min(1.0, linearGain));

            if (envelopeMax <= envelopeMin)
            {
                return envelopeMin;
            }

            bool linearDomain = envelopeMin >= 0.0 &&
                                envelopeMax <= 2.0001 &&
                                envelopeMin <= 1.0001 &&
                                envelopeMax >= 0.9999;
            if (linearDomain)
            {
                // Bus-send envelopes are linear 0..2 with neutral 1.0
                // (1.0 = unity / 0 dB). Do NOT convert to dB here.
                return Math.Max(envelopeMin, Math.Min(envelopeMax, linearGain));
            }

            if (linearGain <= 0.0)
            {
                return envelopeMin;
            }

            double decibels = 20.0 * Math.Log10(linearGain);
            return Math.Max(envelopeMin, Math.Min(envelopeMax, decibels));
        }

        private static AudioBusTrack[] Ensure71BusTopology(Project project)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            var existing = new List<AudioBusTrack>();
            foreach (BusTrack busTrack in project.BusTracks)
            {
                var audioBus = busTrack as AudioBusTrack;
                if (audioBus == null || !audioBus.IsAudio() || audioBus.IsMaster())
                {
                    continue;
                }

                existing.Add(audioBus);
            }

            var result = new AudioBusTrack[Bus71Layout.BusCount];
            RenderMappingSupported = true;
            RenderMappingStatus = "Render mapping setters accepted by this VEGAS build.";

            for (int i = 0; i < Bus71Layout.BusCount; i++)
            {
                string desiredName = Bus71Layout.BusNames[i];
                AudioBusTrack bus = FindBus(existing, desiredName);

                if (bus == null)
                {
                    bus = project.AddAudioBusTrack();
                    existing.Add(bus);
                }

                // AudioBusTrack.Name is read-only in ScriptPortal.Vegas; the
                // mutable, user-visible label is Description.
                bus.Description = desiredName;

                // Discrete 8-channel render mapping: only the mono downmix of
                // each bus is published, in canonical SMPTE order 0..7.
                // VEGAS Pro 2027 build 51 reports SetRenderEnabled as
                // unsupported; keep the topology valid and record the limitation.
                TrySetRenderMapping(() => bus.MonoRenderEnabled = true, "MonoRenderEnabled", true);
                TrySetRenderMapping(() => bus.MonoRenderOrder = i, "MonoRenderOrder", true);
                TrySetRenderMapping(() => bus.StereoRenderEnabled = false, "StereoRenderEnabled", false);
                TrySetRenderMapping(() => bus.SurroundRenderEnabled = false, "SurroundRenderEnabled", false);

                result[i] = bus;
            }

            return result;
        }

        private static void TrySetRenderMapping(Action setter, string name, bool required)
        {
            try
            {
                setter();
            }
            catch (NotSupportedException ex)
            {
                if (required)
                {
                    RenderMappingSupported = false;
                    RenderMappingStatus = name + " is not supported by this VEGAS build: " + ex.Message;
                }
                else
                {
                    RenderMappingStatus = name + " is optional and unsupported by this VEGAS build: " + ex.Message;
                }
            }
            catch (COMException ex)
            {
                if (required)
                {
                    RenderMappingSupported = false;
                    RenderMappingStatus = name + " failed with COM error: " + ex.Message;
                }
                else
                {
                    RenderMappingStatus = name + " is optional and failed with COM error: " + ex.Message;
                }
            }
        }

        private static AudioBusTrack FindBus(List<AudioBusTrack> busses, string desiredName)
        {
            for (int i = 0; i < busses.Count; i++)
            {
                AudioBusTrack bus = busses[i];
                if (string.Equals(bus.Description, desiredName, StringComparison.Ordinal) ||
                    string.Equals(bus.Name, desiredName, StringComparison.Ordinal))
                {
                    return bus;
                }
            }

            return null;
        }
    }
}
