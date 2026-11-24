using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ScriptPortal.Vegas;

namespace VegasSurround71
{
    /// <summary>
    /// In-host Phase 4A validation. Runs inside VEGAS against the live project:
    /// native status, bus topology reuse/idempotence, BusA..BusH send
    /// correspondence, automation preservation, and undo/redo behavior.
    /// </summary>
    public static class HostValidation
    {
        public const string TestTrackName = "[TEST] VegasSurround71 7.1 Routing";

        // Canonical user points used to prove automation preservation.
        private const double PreservePointASeconds = 2.0;
        private const double PreservePointCSeconds = 4.0;
        private const double KeyframeSeconds = 3.0;
        private const double StaticSeconds = 5.0;
        private const double ProbeKeyframeSeconds = 6.0;
        private const double ProbeStaticSeconds = 7.0;

        public static bool IsValidationEnabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("VEGAS_SURROUND71_RUN_VALIDATION"),
                "1",
                StringComparison.Ordinal);
        }

        public static ValidationReport Run(Vegas vegas, string reportPath)
        {
            return Run(vegas, reportPath, false);
        }

        /// <summary>
        /// Runs the validation. Set <paramref name="createScratchProject"/> when
        /// the host was launched without a project (automated probes) so bus
        /// mutations never touch user work.
        /// </summary>
        public static ValidationReport Run(Vegas vegas, string reportPath, bool createScratchProject)
        {
            var report = new ValidationReport();
            report.Observe("Timestamp: " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));

            if (!IsValidationEnabled())
            {
                report.Observe("Validation disabled: set VEGAS_SURROUND71_RUN_VALIDATION=1 to run.");
                report.Check("Validation startup gate", true);
                Finish(report, reportPath);
                return report;
            }

            try
            {
                if (vegas == null)
                {
                    report.Check("Vegas instance available", false, "vegas == null");
                    Finish(report, reportPath);
                    return report;
                }

                report.Observe("VEGAS: " + Safe(() => vegas.Version));
                report.Observe("Executable: " + Safe(() => vegas.ExeName));
                report.Observe("Install dir: " + Safe(() => vegas.InstallationDirectory));

                RunNativeChecks(report);

                if (createScratchProject)
                {
                    bool created = false;
                    TryAction(report, "Create scratch project",
                        () => created = vegas.NewProject(false, false));
                    report.Check("Scratch project created", created);
                    TryAction(report, "Wait for VEGAS idle", () => vegas.WaitForIdle());
                }

                Project project = vegas.Project;
                report.Check("Active project available", project != null);
                if (project == null)
                {
                    Finish(report, reportPath);
                    return report;
                }

                report.Observe("Project path: " + Safe(() => project.FilePath));
                report.Observe("Baseline bus count: " + project.BusTracks.Count);
                report.Observe("Baseline track count: " + project.Tracks.Count);
                report.Observe("Master bus mode: " + project.Audio.MasterBusMode);

                AudioBusTrack prefabFl = null;

                try
                {
                    CreateFixture(vegas, report, out prefabFl);
                }
                catch (Exception ex)
                {
                    report.Check("Create fixture project", false, ex.ToString());
                }

                project = vegas.Project;
                if (project == null)
                {
                    Finish(report, reportPath);
                    return report;
                }

                prefabFl = FindBus(project, Bus71Layout.BusNames[0]);

                report.Check("Pre-existing [7.1] FL bus exists", prefabFl != null);
                report.Check("Legacy bus A preserved", FindBus(project, "Legacy A") != null);
                report.Check("Legacy bus B preserved", FindBus(project, "Legacy B") != null);

                // -----------------------------------------------------------------
                // Provisioning
                // -----------------------------------------------------------------
                int beforeProvision = CountAudioBusses(project);
                AudioBusTrack[] busses = null;
                try
                {
                    busses = BusMatrixManager.Provision71BusTopology(vegas);
                }
                catch (Exception ex)
                {
                    report.Check("Provision71BusTopology", false, ex.ToString());
                }

                report.Check("Provision returns eight busses",
                    busses != null && busses.Length == Bus71Layout.BusCount);
                if (busses == null)
                {
                    Finish(report, reportPath);
                    return report;
                }

                int afterProvision = CountAudioBusses(project);
                report.Check("Provision creates exactly seven missing busses",
                    afterProvision == beforeProvision + 7,
                    string.Format(CultureInfo.InvariantCulture,
                        "before={0} after={1}", beforeProvision, afterProvision));
                report.Check("Provision reuses pre-existing [7.1] FL",
                    busses[0] != null && Equals(busses[0], prefabFl));

                report.Observe("Render mapping supported: " + BusMatrixManager.RenderMappingSupported);
                report.Observe("Render mapping status: " + BusMatrixManager.RenderMappingStatus);

                DescribeTopology(report, busses);

                for (int i = 0; i < busses.Length; i++)
                {
                    AudioBusTrack bus = busses[i];
                    report.Check("bus[" + i + "] canonical description",
                        bus != null && string.Equals(bus.Description, Bus71Layout.BusNames[i],
                            StringComparison.Ordinal),
                        bus == null ? "<null>" : bus.Description);

                    if (BusMatrixManager.RenderMappingSupported)
                    {
                        report.Check("bus[" + i + "] mono render enabled",
                            bus != null && bus.MonoRenderEnabled);
                        report.Check("bus[" + i + "] mono render order == " + i,
                            bus != null && bus.MonoRenderOrder == i,
                            bus == null ? "<null>" : bus.MonoRenderOrder.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        report.Observe("bus[" + i + "] render mapping check skipped: " +
                            BusMatrixManager.RenderMappingStatus);
                    }

                    if (bus != null)
                    {
                        int letterIndex = Bus71Layout.GetBusLetterIndex(bus.Name);
                        string letter = letterIndex >= 0
                            ? ((char)('A' + letterIndex)).ToString()
                            : "?";
                        report.Check("bus[" + i + "] has a valid VEGAS bus letter",
                            letterIndex >= 0,
                            "name='" + bus.Name + "' letter='" + letter + "'");

                        if (letterIndex >= 0)
                        {
                            report.Observe("bus[" + i + "] canonical " +
                                Bus71Layout.ChannelLabels[i] + " -> " +
                                Bus71Layout.GetSendEnvelopeType(bus) +
                                " ('" + bus.Name + "')");
                        }
                    }
                }

                // Provisioning undo/redo runs in a separate VEGAS script
                // transaction (see RunUndoPhase); undoing inside this transaction
                // does not commit the in-flight undo block.
                report.Observe("Provisioning undo/redo is verified in the separate undo phase.");

                AudioBusTrack[] canonical = FindCanonicalBusses(project);
                report.Check("Canonical topology present before undo phase",
                    canonical != null && canonical.Length == Bus71Layout.BusCount);

                // -----------------------------------------------------------------
                // Repeatable provisioning
                // -----------------------------------------------------------------
                if (canonical != null)
                {
                    AudioBusTrack[] secondPass = null;
                    try
                    {
                        secondPass = BusMatrixManager.Provision71BusTopology(vegas);
                    }
                    catch (Exception ex)
                    {
                        report.Check("Repeat provisioning", false, ex.ToString());
                    }

                    report.Check("Repeat provisioning returns eight busses",
                        secondPass != null && secondPass.Length == Bus71Layout.BusCount);
                    report.Check("Repeat provisioning does not add busses",
                        CountAudioBusses(project) == afterProvision);

                    if (secondPass != null)
                    {
                        for (int i = 0; i < canonical.Length; i++)
                        {
                            report.Check("Repeat provisioning reuses bus[" + i + "]",
                                Equals(canonical[i], secondPass[i]));
                        }
                    }
                }

                // -----------------------------------------------------------------
                // Automation preservation and undo
                // -----------------------------------------------------------------
                AudioTrack testTrack = null;
                double preservedA = double.NaN;
                double preservedC = double.NaN;

                try
                {
                    CreateAutomationFixture(vegas, report, out testTrack,
                        out preservedA, out preservedC);
                }
                catch (Exception ex)
                {
                    report.Check("Create automation fixture", false, ex.ToString());
                }

                if (testTrack != null)
                {
                    RunAutomationChecks(vegas, project, testTrack, report,
                        preservedA, preservedC);
                }

                report.Check("Project remains responsive after validation",
                    Safe(() => vegas.Project != null) == "True");
            }
            catch (Exception ex)
            {
                report.Check("Unhandled host validation exception", false, ex.ToString());
            }

            Finish(report, reportPath);
            return report;
        }

        private static void CreateFixture(
            Vegas vegas,
            ValidationReport report,
            out AudioBusTrack prefabFl)
        {
            prefabFl = null;

            Project project = vegas.Project;
            int baselineBusses = CountAudioBusses(project);

            using (project.CreateBackgroundProjectActiveState())
            {
                // Two unrelated busses must survive provisioning untouched.
                AudioBusTrack legacyA = project.AddAudioBusTrack();
                legacyA.Description = "Legacy A";
                AudioBusTrack legacyB = project.AddAudioBusTrack();
                legacyB.Description = "Legacy B";

                // A pre-existing canonical bus must be reused, not duplicated.
                prefabFl = project.AddAudioBusTrack();
                prefabFl.Description = Bus71Layout.BusNames[0];
            }

            report.Check("Fixture adds two legacy busses and one prefab FL",
                CountAudioBusses(project) == baselineBusses + 3,
                "baseline=" + baselineBusses + " current=" + CountAudioBusses(project));
        }

        private static void CreateAutomationFixture(
            Vegas vegas,
            ValidationReport report,
            out AudioTrack testTrack,
            out double preservedA,
            out double preservedC)
        {
            testTrack = null;
            preservedA = double.NaN;
            preservedC = double.NaN;

            Project project = vegas.Project;
            using (project.CreateBackgroundProjectActiveState())
            {
                testTrack = project.AddAudioTrack();
                testTrack.Name = TestTrackName;

                AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);
                if (busses == null)
                {
                    throw new InvalidOperationException(
                        "Canonical 7.1 busses are missing while creating the automation fixture.");
                }

                BusSendRoute[] sends = BusMatrixManager.EnsureBusSends(testTrack, busses);
                sends[0].Envelope.Points.Add(new EnvelopePoint(
                    Timecode.FromSeconds(PreservePointASeconds),
                    sends[0].Envelope.Neutral,
                    CurveType.Linear));
                sends[2].Envelope.Points.Add(new EnvelopePoint(
                    Timecode.FromSeconds(PreservePointCSeconds),
                    sends[2].Envelope.Neutral,
                    CurveType.Linear));

                preservedA = PointValue(sends[0].Envelope, PreservePointASeconds);
                preservedC = PointValue(sends[2].Envelope, PreservePointCSeconds);
            }

            report.Check("Automation fixture track created", testTrack != null);
            report.Check("User BusA point created at 2s", !double.IsNaN(preservedA),
                "value=" + Format(preservedA));
            report.Check("User BusC point created at 4s", !double.IsNaN(preservedC),
                "value=" + Format(preservedC));
        }

        private static void RunAutomationChecks(
            Vegas vegas,
            Project project,
            AudioTrack track,
            ValidationReport report,
            double preservedA,
            double preservedC)
        {
            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);
            report.Check("Canonical busses available for send verification", busses != null);
            if (busses == null)
            {
                return;
            }

            BusSendRoute[] sends = BusMatrixManager.EnsureBusSends(track, busses);
            report.Check("Track owns eight bus send envelopes",
                sends != null && sends.Length == Bus71Layout.BusCount);

            if (sends == null)
            {
                return;
            }

            for (int i = 0; i < sends.Length; i++)
            {
                EnvelopeType expected = Bus71Layout.GetSendEnvelopeType(busses[i]);
                report.Check("send[" + i + "] envelope " + expected +
                    " matches '" + busses[i].Name + "'",
                    sends[i].EnvelopeType == expected &&
                    string.Equals(sends[i].BusTrackName, busses[i].Name, StringComparison.Ordinal),
                    "envelope=" + sends[i].EnvelopeType +
                    " bus='" + sends[i].BusTrackName + "'");
            }

            double beforeA = PointValue(sends[0].Envelope, PreservePointASeconds);
            double beforeC = PointValue(sends[2].Envelope, PreservePointCSeconds);
            report.Check("BusA user point available before panning",
                !double.IsNaN(beforeA), "value=" + Format(beforeA));
            report.Check("BusC user point available before panning",
                !double.IsNaN(beforeC), "value=" + Format(beforeC));

            if (!double.IsNaN(beforeA))
            {
                preservedA = beforeA;
            }

            if (!double.IsNaN(beforeC))
            {
                preservedC = beforeC;
            }

            const float x = -0.5f;
            const float y = 0.5f;
            const float lfeSend = 0.25f;
            const float spread = 0.1f;

            ChannelGains71 gains = NativeBridge.calculate_71_gains(x, y, lfeSend, spread);
            report.Observe("Native gains @ (-0.5, 0.5, lfe=0.25, spread=0.1): " +
                FormatGains(gains));

            TryAction(report, "Apply keyframed 7.1 pan at 3s",
                () => BusMatrixManager.ApplyTrackPanning(
                    vegas, track, Timecode.FromSeconds(KeyframeSeconds),
                    x, y, lfeSend, spread, true));

            sends = BusMatrixManager.EnsureBusSends(track, busses);
            float[] levels = gains.ToArray();
            for (int i = 0; i < sends.Length; i++)
            {
                Envelope env = sends[i].Envelope;
                double expected = BusMatrixManager.LinearGainToEnvelopeValue(
                    levels[i], env.Min, env.Max);
                double actual = PointValue(env, KeyframeSeconds);
                report.Check("keyframe bus[" + i + "] value at 3s",
                    !double.IsNaN(actual) && Math.Abs(actual - expected) < 1e-6,
                    "expected=" + Format(expected) + " actual=" + Format(actual));
            }

            double afterA = PointValue(sends[0].Envelope, PreservePointASeconds);
            double afterC = PointValue(sends[2].Envelope, PreservePointCSeconds);
            report.Check("Keyframing preserves BusA point at 2s",
                !double.IsNaN(afterA) && Math.Abs(afterA - preservedA) < 1e-6,
                "before=" + Format(preservedA) + " after=" + Format(afterA));
            report.Check("Keyframing preserves BusC point at 4s",
                !double.IsNaN(afterC) && Math.Abs(afterC - preservedC) < 1e-6,
                "before=" + Format(preservedC) + " after=" + Format(afterC));

            report.Observe("Pan undo/redo is verified in the separate undo phase.");

            // Static mode intentionally collapses sends to a single held point.
            TryAction(report, "Apply static 7.1 pan at 5s",
                () => BusMatrixManager.ApplyTrackPanning(
                    vegas, track, Timecode.FromSeconds(StaticSeconds),
                    x, y, lfeSend, spread, false));

            sends = BusMatrixManager.EnsureBusSends(track, busses);
            for (int i = 0; i < sends.Length; i++)
            {
                Envelope env = sends[i].Envelope;
                double expected = BusMatrixManager.LinearGainToEnvelopeValue(
                    levels[i], env.Min, env.Max);
                report.Check("static bus[" + i + "] has a single point",
                    env.Points.Count == 1,
                    "count=" + env.Points.Count.ToString(CultureInfo.InvariantCulture));
                double actual = PointValue(env, 0.0);
                report.Check("static bus[" + i + "] value at 0s",
                    !double.IsNaN(actual) && Math.Abs(actual - expected) < 1e-6,
                    "expected=" + Format(expected) + " actual=" + Format(actual));
            }
        }

        /// <summary>
        /// Probe transaction: add one audio bus. Runs in the script host as its
        /// own undoable transaction.
        /// </summary>
        public static void ProbeAddBus(Vegas vegas)
        {
            Project project = vegas.Project;
            using (project.CreateBackgroundProjectActiveState())
            using (new UndoBlock(project, "VegasSurround71 Probe: Add Bus"))
            {
                AudioBusTrack bus = project.AddAudioBusTrack();
                bus.Description = "Probe Bus";
            }
        }

        /// <summary>Probe transaction: apply a keyframe pan at 6s.</summary>
        public static void ProbeKeyframe(Vegas vegas)
        {
            Project project = vegas.Project;
            AudioTrack track = FindAudioTrack(project, TestTrackName);
            AudioBusTrack[] busses = FindCanonicalBusses(project);
            if (track == null || busses == null)
            {
                throw new InvalidOperationException(
                    "Keyframe probe fixture (track + canonical busses) is missing.");
            }

            BusMatrixManager.ApplyTrackPanning(
                vegas, track, Timecode.FromSeconds(ProbeKeyframeSeconds),
                1f, 0f, 0f, 0f, true);
        }

        /// <summary>Probe transaction: apply a static pan.</summary>
        public static void ProbeStatic(Vegas vegas)
        {
            Project project = vegas.Project;
            AudioTrack track = FindAudioTrack(project, TestTrackName);
            AudioBusTrack[] busses = FindCanonicalBusses(project);
            if (track == null || busses == null)
            {
                throw new InvalidOperationException(
                    "Static probe fixture (track + canonical busses) is missing.");
            }

            BusMatrixManager.ApplyTrackPanning(
                vegas, track, Timecode.FromSeconds(ProbeStaticSeconds),
                0f, 1f, 0.1f, 0f, false);
        }

        /// <summary>
        /// Runs each undo probe in its own script transaction, then verifies
        /// Undo/Redo from the extension UI thread under active project state.
        /// </summary>
        public static void RunUndoProbesDirect(Vegas vegas, string reportPath)
        {
            var report = new ValidationReport();
            report.Observe("Timestamp: " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            report.Observe("Phase: undo/redo probes (one operation per host transaction)");

            if (!IsValidationEnabled())
            {
                report.Observe("Validation disabled: set VEGAS_SURROUND71_RUN_VALIDATION=1 to run.");
                report.Check("Validation startup gate", true);
                Finish(report, reportPath);
                return;
            }

            try
            {
                Project project = vegas.Project;

                // Pan probes run first so bus-collection undo/redo cannot
                // invalidate the track/envelope fixture.

                // --- Keyframe pan probe ------------------------------------
                project = vegas.Project;
                AudioTrack track = FindAudioTrack(project, TestTrackName);
                AudioBusTrack[] busses = FindCanonicalBusses(project);
                Envelope[] envelopes = FindSendEnvelopes(track, busses);
                report.Observe("Keyframe fixture: track=" + (track != null) +
                    " busses=" + (busses != null) +
                    " envelopes=" + (envelopes != null));
                if (track != null)
                {
                    report.Observe("Keyframe fixture track='" + track.Name +
                        "' envelopeCount=" + track.Envelopes.Count);
                }

                report.Check("Keyframe probe fixture present", envelopes != null);

                if (envelopes != null)
                {
                    report.Check("6s keyframe absent before probe",
                        double.IsNaN(PointValue(envelopes[0], ProbeKeyframeSeconds)));

                    RunProbeScript(vegas, "ProbeKeyframe");
                    project = vegas.Project;
                    track = FindAudioTrack(project, TestTrackName);
                    busses = FindCanonicalBusses(project);
                    envelopes = FindSendEnvelopes(track, busses);
                    report.Check("Keyframe probe writes 6s point",
                        envelopes != null &&
                        !double.IsNaN(PointValue(envelopes[0], ProbeKeyframeSeconds)));

                    UndoOne(vegas);
                    project = vegas.Project;
                    track = FindAudioTrack(project, TestTrackName);
                    busses = FindCanonicalBusses(project);
                    envelopes = FindSendEnvelopes(track, busses);
                    report.Check("Undo removes the 6s keyframe",
                        envelopes != null &&
                        double.IsNaN(PointValue(envelopes[0], ProbeKeyframeSeconds)));

                    RedoOne(vegas);
                    project = vegas.Project;
                    track = FindAudioTrack(project, TestTrackName);
                    busses = FindCanonicalBusses(project);
                    envelopes = FindSendEnvelopes(track, busses);
                    report.Check("Redo restores the 6s keyframe",
                        envelopes != null &&
                        !double.IsNaN(PointValue(envelopes[0], ProbeKeyframeSeconds)));
                }

                // --- Static pan probe --------------------------------------
                project = vegas.Project;
                track = FindAudioTrack(project, TestTrackName);
                busses = FindCanonicalBusses(project);
                envelopes = FindSendEnvelopes(track, busses);
                report.Check("Static probe fixture present", envelopes != null);

                if (envelopes != null)
                {
                    report.Check("Pre-static probe envelope has automation",
                        envelopes[0].Points.Count > 1);

                    RunProbeScript(vegas, "ProbeStatic");
                    project = vegas.Project;
                    track = FindAudioTrack(project, TestTrackName);
                    busses = FindCanonicalBusses(project);
                    envelopes = FindSendEnvelopes(track, busses);
                    report.Check("Static probe collapses envelope to one point",
                        envelopes != null && envelopes[0].Points.Count == 1);

                    UndoOne(vegas);
                    project = vegas.Project;
                    track = FindAudioTrack(project, TestTrackName);
                    busses = FindCanonicalBusses(project);
                    envelopes = FindSendEnvelopes(track, busses);
                    report.Check("Undo restores pre-static automation",
                        envelopes != null &&
                        envelopes[0].Points.Count > 1 &&
                        !double.IsNaN(PointValue(envelopes[0], ProbeKeyframeSeconds)));

                    RedoOne(vegas);
                    project = vegas.Project;
                    track = FindAudioTrack(project, TestTrackName);
                    busses = FindCanonicalBusses(project);
                    envelopes = FindSendEnvelopes(track, busses);
                    report.Check("Redo re-applies the static pan",
                        envelopes != null && envelopes[0].Points.Count == 1);
                }

                // --- Bus creation probe (last: bus undo may invalidate caches) ---
                int beforeBus = CountAudioBusses(vegas.Project);
                RunProbeScript(vegas, "ProbeAddBus");
                int afterBus = CountAudioBusses(vegas.Project);
                report.Check("Probe bus transaction added exactly one bus",
                    afterBus == beforeBus + 1,
                    "before=" + beforeBus + " after=" + afterBus);

                UndoOne(vegas);
                int undoneBus = CountAudioBusses(vegas.Project);
                report.Check("Undo removes the probe bus",
                    undoneBus == beforeBus,
                    "expected=" + beforeBus + " actual=" + undoneBus);

                RedoOne(vegas);
                int redoneBus = CountAudioBusses(vegas.Project);
                report.Check("Redo restores the probe bus",
                    redoneBus == afterBus,
                    "expected=" + afterBus + " actual=" + redoneBus);
            }
            catch (Exception ex)
            {
                report.Check("Undo probe exception", false, ex.ToString());
            }

            Finish(report, reportPath);
        }

        private static void RunProbeScript(Vegas vegas, string methodName)
        {
            string assemblyPath = typeof(HostValidation).Assembly.Location;
            string assemblyLiteral = "@\"" +
                assemblyPath.Replace("\"", "\"\"") + "\"";
            string code = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        Assembly asm = Assembly.LoadFrom(" + assemblyLiteral + @");
        Type type = asm.GetType(""VegasSurround71.HostValidation"", true);
        MethodInfo method = type.GetMethod(""" + methodName + @""", new Type[] { typeof(Vegas) });
        if (method == null) throw new MissingMethodException(""VegasSurround71.HostValidation." + methodName + @""");
        method.Invoke(null, new object[] { vegas });
    }
}";
            vegas.RunScriptText(ScriptEngineType.CSharp, code);
        }

        /// <summary>
        /// Undo/redo validation executed on the extension UI thread. VEGAS
        /// requires <see cref="Project.CreateBackgroundProjectActiveState"/> plus
        /// <see cref="Vegas.WaitForIdle"/> for programmatic Undo/Redo to take effect.
        /// </summary>
        public static void RunUndoPhaseDirect(Vegas vegas, string reportPath)
        {
            var report = new ValidationReport();
            report.Observe("Timestamp: " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            report.Observe("Phase: undo/redo (extension UI thread, active project state)");

            try
            {
                Project project = vegas.Project;
                report.Check("Active project available for undo phase", project != null);
                if (project == null)
                {
                    Finish(report, reportPath);
                    return;
                }

                int provisionedBusCount = CountAudioBusses(project);
                report.Observe("Bus count before undo phase: " + provisionedBusCount);

                AudioTrack track = FindAudioTrack(project, TestTrackName);
                AudioBusTrack[] busses = FindCanonicalBusses(project);
                Envelope[] envelopes = FindSendEnvelopes(track, busses);

                report.Check("Undo-phase validation track present", track != null);
                report.Check("Undo-phase canonical busses present", busses != null);
                report.Check("Undo-phase send envelopes present", envelopes != null);

                if (track == null || busses == null || envelopes == null)
                {
                    Finish(report, reportPath);
                    return;
                }

                bool staticCollapsed = true;
                for (int i = 0; i < envelopes.Length; i++)
                {
                    staticCollapsed &= envelopes[i].Points.Count == 1 &&
                                       !double.IsNaN(PointValue(envelopes[i], 0.0));
                }

                report.Check("Static pan state present before undo", staticCollapsed);
                report.Check("Keyframe 3s absent in static state",
                    double.IsNaN(PointValue(envelopes[0], KeyframeSeconds)));
                report.Check("User 2s point absent in static state",
                    double.IsNaN(PointValue(envelopes[0], PreservePointASeconds)));
                report.Check("User 4s point absent in static state",
                    double.IsNaN(PointValue(envelopes[2], PreservePointCSeconds)));

                // 1) Undo static pan.
                UndoOne(vegas);
                project = vegas.Project;
                track = FindAudioTrack(project, TestTrackName);
                busses = FindCanonicalBusses(project);
                envelopes = FindSendEnvelopes(track, busses);

                report.Check("Undo static pan restores 3s keyframe",
                    envelopes != null &&
                    !double.IsNaN(PointValue(envelopes[0], KeyframeSeconds)));
                report.Check("Undo static pan restores user BusA 2s point",
                    envelopes != null &&
                    !double.IsNaN(PointValue(envelopes[0], PreservePointASeconds)));
                report.Check("Undo static pan restores user BusC 4s point",
                    envelopes != null &&
                    !double.IsNaN(PointValue(envelopes[2], PreservePointCSeconds)));

                // 2) Undo keyframe pan.
                UndoOne(vegas);
                project = vegas.Project;
                track = FindAudioTrack(project, TestTrackName);
                busses = FindCanonicalBusses(project);
                envelopes = FindSendEnvelopes(track, busses);

                report.Check("Second undo removes 3s keyframe",
                    envelopes != null &&
                    double.IsNaN(PointValue(envelopes[0], KeyframeSeconds)) &&
                    double.IsNaN(PointValue(envelopes[7], KeyframeSeconds)));
                report.Check("Second undo preserves user BusA 2s point",
                    envelopes != null &&
                    !double.IsNaN(PointValue(envelopes[0], PreservePointASeconds)));
                report.Check("Second undo preserves user BusC 4s point",
                    envelopes != null &&
                    !double.IsNaN(PointValue(envelopes[2], PreservePointCSeconds)));

                // 3) Redo keyframe + static.
                RedoOne(vegas);
                project = vegas.Project;
                track = FindAudioTrack(project, TestTrackName);
                busses = FindCanonicalBusses(project);
                envelopes = FindSendEnvelopes(track, busses);
                report.Check("Redo restores 3s keyframe",
                    envelopes != null &&
                    !double.IsNaN(PointValue(envelopes[0], KeyframeSeconds)));

                RedoOne(vegas);
                project = vegas.Project;
                track = FindAudioTrack(project, TestTrackName);
                busses = FindCanonicalBusses(project);
                envelopes = FindSendEnvelopes(track, busses);
                bool staticRestored = envelopes != null;
                if (envelopes != null)
                {
                    for (int i = 0; i < envelopes.Length; i++)
                    {
                        staticRestored &= envelopes[i].Points.Count == 1;
                    }
                }

                report.Check("Redo restores static collapsed state", staticRestored);

                // 4) Walk back to the provisioning entry and restore.
                int before = CountAudioBusses(project);
                int depth = 0;
                while (CountAudioBusses(vegas.Project) >= before && depth < 30)
                {
                    UndoOne(vegas);
                    depth++;
                }

                project = vegas.Project;
                int after = CountAudioBusses(project);
                report.Check("Provisioning undo reduces bus count",
                    after < before,
                    "before=" + before + " after=" + after + " undoDepth=" + depth);
                report.Check("Provisioning undo removes [7.1] FR",
                    FindBus(project, Bus71Layout.BusNames[1]) == null);
                report.Check("Provisioning undo preserves pre-existing [7.1] FL",
                    FindBus(project, Bus71Layout.BusNames[0]) != null);

                for (int i = 0; i < depth; i++)
                {
                    RedoOne(vegas);
                }

                project = vegas.Project;
                report.Check("Redo restores full provisioned topology",
                    CountAudioBusses(project) == provisionedBusCount &&
                    FindCanonicalBusses(project) != null,
                    "expected=" + provisionedBusCount + " actual=" + CountAudioBusses(project));

                report.Observe("Provisioning undo depth: " + depth);
            }
            catch (Exception ex)
            {
                report.Check("Undo phase exception", false, ex.ToString());
            }

            Finish(report, reportPath);
        }

        private static void UndoOne(Vegas vegas)
        {
            using (vegas.Project.CreateBackgroundProjectActiveState())
            {
                vegas.WaitForIdle();
                vegas.Project.Undo();
                vegas.WaitForIdle();
            }
        }

        private static void RedoOne(Vegas vegas)
        {
            using (vegas.Project.CreateBackgroundProjectActiveState())
            {
                vegas.WaitForIdle();
                vegas.Project.Redo();
                vegas.WaitForIdle();
            }
        }

        private static Envelope[] FindSendEnvelopes(
            AudioTrack track, AudioBusTrack[] busses)
        {
            if (track == null || busses == null)
            {
                return null;
            }

            var envelopes = new Envelope[Bus71Layout.BusCount];
            for (int i = 0; i < envelopes.Length; i++)
            {
                EnvelopeType type;
                try
                {
                    type = Bus71Layout.GetSendEnvelopeType(busses[i]);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }

                envelopes[i] = track.Envelopes.HasEnvelope(type)
                    ? track.Envelopes.FindByType(type)
                    : null;
                if (envelopes[i] == null)
                {
                    return null;
                }
            }

            return envelopes;
        }

        /// <summary>
        /// Second-phase validation, executed in a fresh VEGAS script transaction
        /// after the setup/automation phase has committed. This is required because
        /// a script cannot undo changes made within the same in-flight transaction.
        /// </summary>
        public static void RunUndoPhase(Vegas vegas, string reportPath)
        {
            var report = new ValidationReport();
            report.Observe("Timestamp: " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            report.Observe("Phase: undo/redo (separate VEGAS script transaction)");

            try
            {
                Project project = vegas.Project;
                report.Check("Active project available for undo phase", project != null);
                if (project == null)
                {
                    Finish(report, reportPath);
                    return;
                }

                AudioTrack track = FindAudioTrack(project, TestTrackName);
                AudioBusTrack[] busses = FindCanonicalBusses(project);

                report.Check("Undo-phase validation track present", track != null);
                report.Check("Undo-phase canonical busses present", busses != null);

                if (track == null || busses == null)
                {
                    Finish(report, reportPath);
                    return;
                }

                int provisionedBusCount = CountAudioBusses(project);
                report.Observe("Provisioned bus count: " + provisionedBusCount);

                BusSendRoute[] sends = BusMatrixManager.EnsureBusSends(track, busses);

                bool staticCollapsed = true;
                for (int i = 0; i < sends.Length; i++)
                {
                    staticCollapsed &= sends[i].Envelope.Points.Count == 1 &&
                                       !double.IsNaN(PointValue(sends[i].Envelope, 0.0));
                }

                report.Check("Static pan state present before undo", staticCollapsed);
                report.Check("Keyframe 3s absent in static state",
                    double.IsNaN(PointValue(sends[0].Envelope, KeyframeSeconds)));
                report.Check("User 2s point absent in static state",
                    double.IsNaN(PointValue(sends[0].Envelope, PreservePointASeconds)));
                report.Check("User 4s point absent in static state",
                    double.IsNaN(PointValue(sends[2].Envelope, PreservePointCSeconds)));

                // 1) Undo static pan -> keyframe pan + user points return.
                TryAction(report, "Undo static pan", () => project.Undo());
                sends = BusMatrixManager.EnsureBusSends(track, busses);
                report.Check("Undo restores 3s keyframe",
                    !double.IsNaN(PointValue(sends[0].Envelope, KeyframeSeconds)));
                report.Check("Undo restores user BusA 2s point",
                    !double.IsNaN(PointValue(sends[0].Envelope, PreservePointASeconds)));
                report.Check("Undo restores user BusC 4s point",
                    !double.IsNaN(PointValue(sends[2].Envelope, PreservePointCSeconds)));

                // 2) Undo keyframe pan -> only user automation remains.
                TryAction(report, "Undo keyframed pan", () => project.Undo());
                sends = BusMatrixManager.EnsureBusSends(track, busses);
                report.Check("Second undo removes 3s keyframe",
                    double.IsNaN(PointValue(sends[0].Envelope, KeyframeSeconds)) &&
                    double.IsNaN(PointValue(sends[7].Envelope, KeyframeSeconds)));
                report.Check("Second undo preserves user BusA 2s point",
                    !double.IsNaN(PointValue(sends[0].Envelope, PreservePointASeconds)));
                report.Check("Second undo preserves user BusC 4s point",
                    !double.IsNaN(PointValue(sends[2].Envelope, PreservePointCSeconds)));

                // 3) Redo keyframe + static pans.
                TryAction(report, "Redo keyframed pan", () => project.Redo());
                sends = BusMatrixManager.EnsureBusSends(track, busses);
                report.Check("Redo restores 3s keyframe",
                    !double.IsNaN(PointValue(sends[0].Envelope, KeyframeSeconds)));

                TryAction(report, "Redo static pan", () => project.Redo());
                sends = BusMatrixManager.EnsureBusSends(track, busses);
                bool staticRestored = true;
                for (int i = 0; i < sends.Length; i++)
                {
                    staticRestored &= sends[i].Envelope.Points.Count == 1;
                }

                report.Check("Redo restores static collapsed state", staticRestored);

                // 4) Walk the undo stack back to provisioning. The exact depth
                // includes automation-track/envelope creation, so stop at the
                // first bus-count reduction.
                int before = CountAudioBusses(project);
                int depth = 0;
                while (CountAudioBusses(project) >= before && depth < 30)
                {
                    project.Undo();
                    depth++;
                }

                int after = CountAudioBusses(project);
                report.Check("Provisioning undo reduces bus count",
                    after < before,
                    "before=" + before + " after=" + after + " undoDepth=" + depth);
                report.Check("Provisioning undo removes [7.1] FR",
                    FindBus(project, Bus71Layout.BusNames[1]) == null);
                report.Check("Provisioning undo preserves pre-existing [7.1] FL",
                    FindBus(project, Bus71Layout.BusNames[0]) != null);

                for (int i = 0; i < depth; i++)
                {
                    project.Redo();
                }

                report.Check("Redo restores full provisioned topology",
                    CountAudioBusses(project) == provisionedBusCount &&
                    FindCanonicalBusses(project) != null,
                    "expected=" + provisionedBusCount + " actual=" + CountAudioBusses(project));

                report.Observe("Provisioning undo depth: " + depth);
            }
            catch (Exception ex)
            {
                report.Check("Undo phase exception", false, ex.ToString());
            }

            Finish(report, reportPath);
        }

        /// <summary>
        /// In-host diagnostic for the VEGAS 2027 send model. Writes envelope
        /// ranges, value mapping and read-back values; used to fix the Mixing
        /// Console send-level discrepancy.
        /// </summary>
        public static void RunSendDiagnostics(Vegas vegas, string reportPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("TellTale 7-Point-One Diagnostic — Send Model");
            sb.AppendLine("============================================");

            try
            {
                bool created = vegas.NewProject(false, false);
                sb.AppendLine("NewProject=" + created);
                Project project = vegas.Project;

                using (project.CreateBackgroundProjectActiveState())
                {
                    AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);
                    if (busses == null)
                    {
                        busses = BusMatrixManager.Provision71BusTopology(vegas);
                    }

                    var track = project.AddAudioTrack();
                    track.Name = "[DIAG] Send Levels";
                    sb.AppendLine("Track index=" + track.Index + " name='" + track.Name + "'");

                    foreach (PropertyInfo property in typeof(AudioTrack).GetProperties(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (property.Name.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            property.Name.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sb.AppendLine("TRACK PROP " + property.Name + " : " +
                                property.PropertyType.Name);
                        }
                    }

                    BusSendRoute[] sends = BusMatrixManager.EnsureBusSends(track, busses);
                    for (int i = 0; i < sends.Length; i++)
                    {
                        Envelope env = sends[i].Envelope;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "ENV[{0}] bus='{1}' desc='{2}' type={3} min={4} max={5} neutral={6} count={7}",
                            i, busses[i].Name, busses[i].Description, env.Type,
                            env.Min, env.Max, env.Neutral, env.Points.Count));

                        try
                        {
                            sb.AppendLine("  valueAt0=" + env.ValueAt(Timecode.FromSeconds(0)));
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("  valueAt0 EX " + ex.Message);
                        }

                        double mid = env.Min + (env.Max - env.Min) * 0.25;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  mapped 0.05={0} 0.25={1} 0.99={2}",
                            BusMatrixManager.LinearGainToEnvelopeValue(0.05, env.Min, env.Max),
                            BusMatrixManager.LinearGainToEnvelopeValue(0.25, env.Min, env.Max),
                            BusMatrixManager.LinearGainToEnvelopeValue(0.99, env.Min, env.Max)));

                        try
                        {
                            env.Points.Add(new EnvelopePoint(
                                Timecode.FromSeconds(1), env.Neutral, CurveType.Linear));
                            sb.AppendLine("  add(neutral,1s) OK valueAt1=" +
                                env.ValueAt(Timecode.FromSeconds(1)));
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("  add(neutral,1s) EX " + ex.GetType().Name +
                                ": " + ex.Message);
                        }

                        if (env.Max > env.Min)
                        {
                            try
                            {
                                env.Points.Add(new EnvelopePoint(
                                    Timecode.FromSeconds(2), mid, CurveType.Linear));
                                sb.AppendLine("  add(mid,2s) OK valueAt2=" +
                                    env.ValueAt(Timecode.FromSeconds(2)));
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine("  add(mid,2s) EX " + ex.GetType().Name +
                                    ": " + ex.Message);
                            }
                        }

                        try
                        {
                            sb.AppendLine("  envelope COM type=" +
                                (env.GetType().GetProperty("COM") == null
                                    ? "<none>"
                                    : env.GetType().GetProperty("COM").PropertyType.FullName));
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("  envelope COM EX " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("DIAG EXCEPTION " + ex);
            }

            string directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static void RunNativeChecks(ValidationReport report)
        {
            report.Check("ChannelGains71 managed size == 32", ChannelGains71.MarshalSize == 32);

            string error;
            bool loaded = NativeBridge.IsNativeAvailable(out error);
            report.Check("Native core loaded", loaded, error);
            report.Observe("Native DLL: " + NativeBridge.DllFileName);
            report.Observe("FLAC supported: " +
                NativeBridge.IsCodecSupported(AudioCodec71.Flac));
            report.Observe("Opus supported: " +
                NativeBridge.IsCodecSupported(AudioCodec71.Opus));

            if (!loaded)
            {
                return;
            }

            ChannelGains71 center = NativeBridge.calculate_71_gains(0f, 1f, 0.5f, 0f);
            report.Check("Native VBAP center routes to FC",
                Math.Abs(center.fc - 1.0f) < 1e-4f, "fc=" + Format(center.fc));
            report.Check("Native LFE send preserved",
                Math.Abs(center.lfe - 0.5f) < 1e-4f, "lfe=" + Format(center.lfe));
        }

        private static void DescribeTopology(ValidationReport report, AudioBusTrack[] busses)
        {
            for (int i = 0; i < busses.Length; i++)
            {
                AudioBusTrack bus = busses[i];
                if (bus == null)
                {
                    report.Observe("bus[" + i + "] = <null>");
                    continue;
                }

                report.Observe(string.Format(CultureInfo.InvariantCulture,
                    "bus[{0}] desc='{1}' name='{2}' index={3} monoEnabled={4} monoOrder={5} type={6}",
                    i, bus.Description, bus.Name, bus.Index, bus.MonoRenderEnabled,
                    bus.MonoRenderOrder, bus.GetType().Name));
            }
        }

        private static int CountAudioBusses(Project project)
        {
            int count = 0;
            foreach (BusTrack bus in project.BusTracks)
            {
                var audio = bus as AudioBusTrack;
                if (audio != null && audio.IsAudio() && !audio.IsMaster())
                {
                    count++;
                }
            }

            return count;
        }

        private static AudioBusTrack FindBus(Project project, string description)
        {
            foreach (BusTrack bus in project.BusTracks)
            {
                var audio = bus as AudioBusTrack;
                if (audio == null || !audio.IsAudio() || audio.IsMaster())
                {
                    continue;
                }

                if (string.Equals(audio.Description, description, StringComparison.Ordinal))
                {
                    return audio;
                }
            }

            return null;
        }

        private static AudioBusTrack[] FindCanonicalBusses(Project project)
        {
            var result = new AudioBusTrack[Bus71Layout.BusCount];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = FindBus(project, Bus71Layout.BusNames[i]);
                if (result[i] == null)
                {
                    return null;
                }
            }

            return result;
        }

        private static AudioTrack FindAudioTrack(Project project, string name)
        {
            foreach (Track track in project.Tracks)
            {
                var audio = track as AudioTrack;
                if (audio != null && string.Equals(audio.Name, name, StringComparison.Ordinal))
                {
                    return audio;
                }
            }

            return null;
        }

        private static double PointValue(Envelope envelope, double seconds)
        {
            if (envelope == null)
            {
                return double.NaN;
            }

            EnvelopePoint point = envelope.Points.GetPointAtX(Timecode.FromSeconds(seconds));
            return point == null ? double.NaN : point.Y;
        }

        private static void TryAction(ValidationReport report, string name, Action action)
        {
            try
            {
                action();
                report.Check(name, true);
            }
            catch (Exception ex)
            {
                report.Check(name, false, ex.Message);
            }
        }

        private static string Safe(Func<object> getter)
        {
            try
            {
                object value = getter();
                return value == null ? "<null>" : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                return "<error: " + ex.Message + ">";
            }
        }

        private static string Format(double value)
        {
            return double.IsNaN(value) ? "<missing>"
                : value.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static string Format(float value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string FormatGains(ChannelGains71 gains)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "FL={0} FR={1} FC={2} LFE={3} BL={4} BR={5} SL={6} SR={7}",
                Format(gains.fl), Format(gains.fr), Format(gains.fc), Format(gains.lfe),
                Format(gains.bl), Format(gains.br), Format(gains.sl), Format(gains.sr));
        }

        private static void Finish(ValidationReport report, string reportPath)
        {
            report.Observe("Finished: " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            string text = report.ToText();

            try
            {
                string dir = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(reportPath, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // Never let report I/O take down the VEGAS host.
                System.Diagnostics.Debug.WriteLine("HostValidation report write failed: " + ex);
            }
        }
    }

    public sealed class ValidationReport
    {
        private readonly List<CheckResult> _checks = new List<CheckResult>();
        private readonly List<string> _observations = new List<string>();

        public int PassedCount { get; private set; }
        public int FailedCount { get; private set; }
        public bool Passed { get { return FailedCount == 0; } }
        public IList<CheckResult> Checks { get { return _checks; } }

        public void Observe(string text)
        {
            _observations.Add(text);
        }

        public void Check(string name, bool condition, string detail = null)
        {
            var result = new CheckResult(name, condition, detail);
            _checks.Add(result);
            if (condition)
            {
                PassedCount++;
            }
            else
            {
                FailedCount++;
            }
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("TellTale 7-Point-One Diagnostic");
            sb.AppendLine("================================");
            foreach (string observation in _observations)
            {
                sb.AppendLine("OBS  " + observation);
            }

            sb.AppendLine();
            foreach (CheckResult check in _checks)
            {
                sb.Append(check.Passed ? "PASS " : "FAIL ");
                sb.Append(check.Name);
                if (!string.IsNullOrEmpty(check.Detail))
                {
                    sb.Append(" [").Append(check.Detail).Append(']');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "SUMMARY: {0} passed, {1} failed, {2} total{3}",
                PassedCount, FailedCount, PassedCount + FailedCount, Environment.NewLine);
            sb.AppendLine(Passed ? "RESULT: PASS" : "RESULT: FAIL");
            return sb.ToString();
        }
    }

    public sealed class CheckResult
    {
        public CheckResult(string name, bool passed, string detail)
        {
            Name = name;
            Passed = passed;
            Detail = detail;
        }

        public string Name { get; private set; }
        public bool Passed { get; private set; }
        public string Detail { get; private set; }
    }
}
