using System;
using System.Globalization;
using System.IO;
using ScriptPortal.Vegas;

namespace VegasSurround71.UI
{
    /// <summary>Top-level workspaces hosted by the panel shell.</summary>
    public enum WorkspaceKind
    {
        Surround71 = 0,
        HoaSpatial = 1
    }

    /// <summary>Diagnostic line raised by the shared workspace logger.</summary>
    public sealed class WorkspaceLogEventArgs : EventArgs
    {
        public WorkspaceLogEventArgs(string message, DateTime timestamp)
        {
            Message = message;
            Timestamp = timestamp;
        }

        public string Message { get; private set; }
        public DateTime Timestamp { get; private set; }
    }

    /// <summary>
    /// Lightweight project-level context shared by every workspace: the VEGAS
    /// instance, the selected track, playhead / loop-region helpers, project
    /// identity, and the diagnostics logger.
    ///
    /// Workspace state itself is deliberately not stored here. The 7.1 panner
    /// keeps its own <see cref="Surround71State"/> and the HOA workspace keeps
    /// its own <see cref="HoaSpatialState"/>, so switching pages can never
    /// change, reset, or write the other workspace's automation.
    /// </summary>
    public sealed class WorkspaceContext
    {
        private static readonly string DiagnosticLogPath =
            Path.Combine(ExtensionSettings.SettingsDirectory, "panel.log");

        // Only the stable index and a display name are cached. Never retain an
        // AudioTrack COM object here: a track can be deleted at any time, and
        // touching a dead wrapper from a timer can take VEGAS down.
        private int _selectedTrackIndex = -1;
        private string _selectedTrackName = string.Empty;

        public WorkspaceContext(Vegas vegas)
        {
            Vegas = vegas;
            ProjectIdentity = "No project";
            RefreshProjectIdentity();
        }

        /// <summary>The VEGAS host instance (may be null in offline tests).</summary>
        public Vegas Vegas { get; private set; }

        /// <summary>Raised when either workspace selects a different audio track.</summary>
        public event EventHandler SelectedTrackChanged;

        /// <summary>Raised for every diagnostic line, regardless of workspace.</summary>
        public event EventHandler<WorkspaceLogEventArgs> DiagnosticMessage;

        /// <summary>The shared track index, or -1 when no track is selected.</summary>
        public int SelectedTrackIndex
        {
            get { return _selectedTrackIndex; }
        }

        /// <summary>The selected track name captured at selection time.</summary>
        public string SelectedTrackName
        {
            get { return _selectedTrackName; }
        }

        /// <summary>True when an audio-track index is shared.</summary>
        public bool HasSelectedTrack
        {
            get { return _selectedTrackIndex >= 0; }
        }

        /// <summary>Short project identity label (path, or an untitled marker).</summary>
        public string ProjectIdentity { get; private set; }

        /// <summary>
        /// Publishes a track selection to every workspace. Only the index and
        /// name are stored; the caller resolves the live track when needed.
        /// </summary>
        public void SetSelectedTrack(int index, string name)
        {
            int safeIndex = index < 0 ? -1 : index;
            string safeName = safeIndex < 0
                ? string.Empty
                : (name ?? string.Empty);

            if (_selectedTrackIndex == safeIndex &&
                string.Equals(_selectedTrackName, safeName, StringComparison.Ordinal))
            {
                return;
            }

            _selectedTrackIndex = safeIndex;
            _selectedTrackName = safeName;

            EventHandler handler = SelectedTrackChanged;
            if (handler != null)
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // A workspace listener must never break the other workspace.
                }
            }
        }

        /// <summary>Clears the shared selection.</summary>
        public void ClearSelectedTrack()
        {
            SetSelectedTrack(-1, null);
        }

        /// <summary>Re-reads the project label after open/close transitions.</summary>
        public void RefreshProjectIdentity()
        {
            Project project = GetProject();
            if (project == null)
            {
                ProjectIdentity = "No project";
                return;
            }

            try
            {
                string path = project.FilePath;
                ProjectIdentity = string.IsNullOrWhiteSpace(path)
                    ? "Untitled project · " + project.Tracks.Count + " track(s)"
                    : path;
            }
            catch (Exception)
            {
                ProjectIdentity = "Project unavailable";
            }
        }

        /// <summary>Returns the active VEGAS project, or null between projects.</summary>
        public Project GetProject()
        {
            if (Vegas == null)
            {
                return null;
            }

            try
            {
                return Vegas.Project;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True while the VEGAS transport is playing.</summary>
        public bool IsTransportPlaying()
        {
            try
            {
                return Vegas != null && Vegas.Transport != null && Vegas.Transport.IsPlaying;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the current edit cursor, or null when no project/transport is
        /// available. Callers must null-check (the VEGAS SDK cannot construct a
        /// <see cref="Timecode"/> without an active project).
        /// </summary>
        public Timecode GetPlayhead()
        {
            try
            {
                if (Vegas == null || Vegas.Transport == null)
                {
                    return null;
                }

                if (Vegas.Transport.IsPlaying &&
                    Vegas.Transport.PlayCursorPosition != null)
                {
                    return Vegas.Transport.PlayCursorPosition;
                }

                if (Vegas.Transport.CursorPosition != null)
                {
                    return Vegas.Transport.CursorPosition;
                }

                return Vegas.Cursor;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads the VEGAS In/Out loop region when it is set.</summary>
        public bool TryGetLoopRegion(out long startNanos, out long lengthNanos)
        {
            startNanos = 0L;
            lengthNanos = 0L;

            try
            {
                TransportControl transport = Vegas == null ? null : Vegas.Transport;
                if (transport == null ||
                    transport.LoopRegionStart == null ||
                    transport.LoopRegionLength == null)
                {
                    return false;
                }

                startNanos = transport.LoopRegionStart.Nanos;
                lengthNanos = transport.LoopRegionLength.Nanos;
                return lengthNanos > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>One-line playhead / loop summary shown in the shell header.</summary>
        public string DescribeTransport()
        {
            try
            {
                long start;
                long length;
                if (TryGetLoopRegion(out start, out length))
                {
                    return "Loop " + Timecode.FromNanos(start) + " → " +
                        Timecode.FromNanos(start + length);
                }

                Timecode playhead = GetPlayhead();
                return playhead == null ? string.Empty : playhead.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Mirrors a diagnostic line to the shared transcript and the persistent
        /// panel log. This is the only shared write path; it never touches VEGAS
        /// automation or bus envelopes.
        /// </summary>
        public void WriteDiagnostic(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            DateTime timestamp = DateTime.Now;

            try
            {
                Directory.CreateDirectory(ExtensionSettings.SettingsDirectory);
                File.AppendAllText(
                    DiagnosticLogPath,
                    timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "  " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Persistent logging is best effort.
            }

            EventHandler<WorkspaceLogEventArgs> handler = DiagnosticMessage;
            if (handler != null)
            {
                try
                {
                    handler(this, new WorkspaceLogEventArgs(message, timestamp));
                }
                catch (Exception)
                {
                    // Diagnostics must never take down a workspace.
                }
            }
        }
    }
}
