using System;
using System.Collections.Generic;
using System.IO;
using ScriptPortal.Vegas;

namespace VegasSurround71
{
    /// <summary>One selectable video renderer/template pair for Pass 1.</summary>
    public sealed class VideoTemplateItem
    {
        public VideoTemplateItem(
            Renderer renderer,
            RenderTemplate template,
            VideoCodecFamily family,
            bool matchesProject,
            bool isCustom,
            int score)
        {
            Renderer = renderer;
            Template = template;
            Family = family;
            MatchesProject = matchesProject;
            IsCustom = isCustom;
            Score = score;
            RendererName = renderer == null ? string.Empty : renderer.Name;
            TemplateName = template == null ? string.Empty : template.Name;
        }

        public Renderer Renderer { get; private set; }
        public RenderTemplate Template { get; private set; }
        public string RendererName { get; private set; }
        public string TemplateName { get; private set; }
        public VideoCodecFamily Family { get; private set; }
        public bool MatchesProject { get; private set; }
        public bool IsCustom { get; private set; }
        public int Score { get; private set; }

        public string DisplayName
        {
            get { return RendererName + " — " + TemplateName; }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// 3-pass 7.1 render orchestrator:
    ///   1. render the timeline video stem,
    ///   2. render the mapped 8-channel Wave64 stem,
    ///   3. mux/encode atomically through the native FFmpeg core.
    /// </summary>
    public static class RenderOrchestrator
    {
        public static RenderResult Run(
            Vegas vegas, RenderPlan plan, Action<string> progress)
        {
            return Run(vegas, plan, null, progress);
        }

        public static RenderResult Run(
            Vegas vegas,
            RenderPlan plan,
            RenderTemplate selectedVideoTemplate,
            Action<string> progress)
        {
            Action<string> report = progress ?? delegate { };
            var result = new RenderResult { OutputPath = plan == null ? null : plan.OutputPath };

            if (vegas == null)
            {
                result.Message = "Vegas instance is null.";
                return result;
            }

            if (plan == null)
            {
                result.Message = "Render plan is null.";
                return result;
            }

            string validation = plan.Validate();
            if (validation != null)
            {
                result.Message = validation;
                return result;
            }

            Project project = vegas.Project;
            if (project == null)
            {
                result.Message = "No active VEGAS project.";
                return result;
            }

            if (!plan.RenderLoopRegionOnly &&
                (project.Length == null || project.Length.Nanos <= 0))
            {
                result.Message = "The timeline is empty.";
                return result;
            }

            if (plan.RenderLoopRegionOnly && plan.LoopLengthNanos <= 0)
            {
                result.Message = "Set In/Out loop points on the VEGAS timeline first.";
                return result;
            }

            try
            {
                report("Verifying [7.1] bus topology and mono render mapping...");
                string mappingError = EnsureBusMapping(vegas, true);
                if (mappingError != null)
                {
                    result.Message = mappingError;
                    return result;
                }

                RenderTemplate videoTemplate = selectedVideoTemplate ??
                    FindDefaultVideoTemplate(vegas, project, plan.Preset);
                if (videoTemplate == null)
                {
                    result.Message = "No compatible video render template was found.";
                    return result;
                }

                Renderer videoRenderer = FindRenderer(vegas, videoTemplate);
                VideoCodecFamily videoFamily = DeliveryPresets.DetectVideoFamily(
                    videoRenderer == null ? string.Empty : videoRenderer.Name,
                    videoTemplate.VideoCodec);
                if (videoFamily == VideoCodecFamily.Unknown)
                {
                    videoFamily = plan.VideoCodec;
                }

                string videoCompatibility = DeliveryPresets.CompatibilityError(
                    plan.Preset, videoFamily);
                if (videoCompatibility != null)
                {
                    result.Message = videoCompatibility;
                    return result;
                }

                bool usedMapping;
                RenderTemplate audioTemplate = FindAudioTemplate(vegas, project, out usedMapping);
                if (audioTemplate == null)
                {
                    result.Message =
                        "No Wave64 render template found. Create an 8-channel Wave64 template " +
                        "(Render As > Wave 64 > Customize Template) and retry.";
                    return result;
                }

                result.UsedChannelMappingFallback = usedMapping;
                Timecode start;
                Timecode length;
                if (plan.RenderLoopRegionOnly)
                {
                    start = Timecode.FromNanos(plan.LoopStartNanos);
                    length = Timecode.FromNanos(plan.LoopLengthNanos);
                }
                else
                {
                    start = Timecode.FromNanos(0);
                    length = project.Length;
                }

                report(plan.RenderLoopRegionOnly
                    ? "Render range: loop region (" + start + " -> " +
                      Timecode.FromNanos(plan.LoopStartNanos + plan.LoopLengthNanos) + ")"
                    : "Render range: full timeline");

                report("Pass 1/3: rendering video stem...");
                RenderStatus videoStatus = vegas.Render(
                    plan.VideoTempPath, videoTemplate, start, length);
                if (videoStatus != RenderStatus.Complete ||
                    !File.Exists(plan.VideoTempPath))
                {
                    result.Message = "Pass 1 failed: " + videoStatus +
                        " (template: " + videoTemplate.Name + ")";
                    return result;
                }

                report("Pass 2/3: rendering 8-channel Wave64 stem" +
                    (usedMapping ? " (channel mapping fallback)..." : "..."));
                var audioArgs = new RenderArgs
                {
                    OutputFile = plan.AudioTempPath,
                    RenderTemplate = audioTemplate,
                    Start = start,
                    Length = length,
                    UseChannelMapping = true
                };

                RenderStatus audioStatus = vegas.Render(audioArgs);
                if (audioStatus != RenderStatus.Complete ||
                    !File.Exists(plan.AudioTempPath))
                {
                    result.Message = "Pass 2 failed: " + audioStatus +
                        " (template: " + audioTemplate.Name + ")";
                    return result;
                }

                report("Pass 3/3: muxing and encoding final 7.1 container...");
                int muxStatus = NativeBridge.Mux71Master(
                    plan.VideoTempPath,
                    plan.AudioTempPath,
                    plan.OutputPath,
                    plan.Codec,
                    plan.ApplyLfeLowpass);

                result.MuxStatus = muxStatus;
                if (muxStatus != NativeBridge.MUX_OK)
                {
                    result.Message = NativeBridge.DescribeMuxStatus(muxStatus);
                    return result;
                }

                result.Success = true;
                result.Message = "Render complete: " + plan.OutputPath;
                report(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "Render failed: " + ex.Message;
                return result;
            }
            finally
            {
                plan.CleanupTemps();
            }
        }

        /// <summary>
        /// Enumerates video-producing renderer/template pairs, filtered to codecs
        /// that can be delivered by the 7.1 presets. Custom-looking templates and
        /// project width/height/fps matches are sorted first.
        /// </summary>
        public static List<VideoTemplateItem> BuildVideoTemplateCatalog(
            Vegas vegas, Project project)
        {
            var items = new List<VideoTemplateItem>();
            if (vegas == null || project == null)
            {
                return items;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Renderer renderer in vegas.Renderers)
            {
                if (!renderer.SupportsVideo)
                {
                    continue;
                }

                foreach (RenderTemplate template in renderer.Templates)
                {
                    if (template.VideoWidth <= 0 || template.VideoHeight <= 0)
                    {
                        continue;
                    }

                    VideoCodecFamily family = DeliveryPresets.DetectVideoFamily(
                        renderer.Name, template.VideoCodec);
                    if (family == VideoCodecFamily.Unknown)
                    {
                        continue;
                    }

                    string key = renderer.Name + "|" +
                                 template.TemplateGuid.ToString("N") + "|" +
                                 template.Name;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    bool sizeMatch = template.VideoWidth == project.Video.Width &&
                                     template.VideoHeight == project.Video.Height;
                    bool fpsMatch = project.Video.FrameRate > 0.0 &&
                        Math.Abs(template.VideoFrameRate -
                                 project.Video.FrameRate) < 0.01;
                    bool matchesProject = sizeMatch && fpsMatch;
                    bool isCustom = IsCustomTemplate(template);

                    int score = (isCustom ? 1000 : 0) +
                                (sizeMatch ? 200 : 0) +
                                (fpsMatch ? 100 : 0) +
                                RendererPriority(renderer.Name);

                    items.Add(new VideoTemplateItem(
                        renderer, template, family, matchesProject, isCustom, score));
                }
            }

            items.Sort(delegate(VideoTemplateItem a, VideoTemplateItem b)
            {
                int score = b.Score.CompareTo(a.Score);
                if (score != 0)
                {
                    return score;
                }

                return string.Compare(a.DisplayName, b.DisplayName,
                    StringComparison.OrdinalIgnoreCase);
            });

            return items;
        }

        /// <summary>Picks the highest-priority template compatible with a preset.</summary>
        public static RenderTemplate FindDefaultVideoTemplate(
            Vegas vegas, Project project, DeliveryPreset preset)
        {
            List<VideoTemplateItem> catalog = BuildVideoTemplateCatalog(vegas, project);
            foreach (VideoTemplateItem item in catalog)
            {
                if (DeliveryPresets.SupportsVideo(preset, item.Family))
                {
                    return item.Template;
                }
            }

            return catalog.Count > 0 ? catalog[0].Template : null;
        }

        /// <summary>Finds the renderer that owns a selected template.</summary>
        public static Renderer FindRenderer(Vegas vegas, RenderTemplate template)
        {
            if (vegas == null || template == null)
            {
                return null;
            }

            try
            {
                Renderer renderer = vegas.Renderers.FindByRendererID(template.RendererID);
                if (renderer != null)
                {
                    return renderer;
                }
            }
            catch (Exception)
            {
                // Fall through to a name/template scan.
            }

            foreach (Renderer renderer in vegas.Renderers)
            {
                foreach (RenderTemplate candidate in renderer.Templates)
                {
                    if (candidate == template ||
                        (candidate.TemplateGuid == template.TemplateGuid &&
                         candidate.Name == template.Name))
                    {
                        return renderer;
                    }
                }
            }

            return null;
        }

        private static bool IsCustomTemplate(RenderTemplate template)
        {
            // VEGAS 2027 does not expose a custom-template flag. Treat explicit
            // "Custom" names and high user-defined IDs as user templates.
            return template.Name.IndexOf("custom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   template.TemplateID > 1000;
        }

        private static int RendererPriority(string rendererName)
        {
            string name = rendererName ?? string.Empty;
            if (name.IndexOf("legacy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 5;
            }

            if (name.IndexOf("AVC (H.264)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 60;
            }

            if (name.IndexOf("HEVC", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 55;
            }

            if (name.IndexOf("ProRes", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 50;
            }

            if (name.IndexOf("AV1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 45;
            }

            if (name.IndexOf("XAVC", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 40;
            }

            return 0;
        }

        private static string EnsureBusMapping(Vegas vegas, bool repair)
        {
            Project project = vegas.Project;
            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);

            if (busses == null || !AllMapped(busses))
            {
                if (repair)
                {
                    string error;
                    if (!HostScriptBridge.RunProvision(vegas, out error))
                    {
                        return "Unable to provision the 7.1 busses: " + error;
                    }

                    busses = BusMatrixManager.FindCanonicalBusses(project);
                }
            }

            if (busses == null)
            {
                return "The eight [7.1] audio busses are missing.";
            }

            for (int i = 0; i < busses.Length; i++)
            {
                if (Bus71Layout.GetBusLetterIndex(busses[i].Name) < 0)
                {
                    return "Bus '" + busses[i].Name +
                        "' does not map to an A-Z send envelope.";
                }

                try
                {
                    if (!busses[i].MonoRenderEnabled)
                    {
                        return "Bus " + busses[i].Name +
                            " is not mono-render enabled.";
                    }

                    if (busses[i].MonoRenderOrder != i)
                    {
                        return "Bus " + busses[i].Name + " has render order " +
                            busses[i].MonoRenderOrder + ", expected " + i + ".";
                    }
                }
                catch (NotSupportedException)
                {
                    return "Mono render mapping is not supported by this VEGAS build.";
                }
            }

            return null;
        }

        private static bool AllMapped(AudioBusTrack[] busses)
        {
            if (busses == null || busses.Length != Bus71Layout.BusCount)
            {
                return false;
            }

            for (int i = 0; i < busses.Length; i++)
            {
                try
                {
                    if (busses[i] == null ||
                        !busses[i].MonoRenderEnabled ||
                        busses[i].MonoRenderOrder != i)
                    {
                        return false;
                    }
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }

            return true;
        }

        private static RenderTemplate FindVideoTemplate(Vegas vegas, Project project)
        {
            string[] preferred = { "AVC (H.264)", "HEVC (H.265)", "Apple ProRes" };
            foreach (string name in preferred)
            {
                Renderer renderer = vegas.Renderers.FindByName(name);
                if (renderer == null || !renderer.SupportsVideo)
                {
                    continue;
                }

                RenderTemplate template = FindBestVideoTemplate(renderer, project);
                if (template != null)
                {
                    return template;
                }
            }

            foreach (Renderer renderer in vegas.Renderers)
            {
                if (!renderer.SupportsVideo)
                {
                    continue;
                }

                RenderTemplate template = FindBestVideoTemplate(renderer, project);
                if (template != null)
                {
                    return template;
                }
            }

            return null;
        }

        private static RenderTemplate FindBestVideoTemplate(
            Renderer renderer, Project project)
        {
            RenderTemplate fallback = null;
            foreach (RenderTemplate template in renderer.Templates)
            {
                if (template.VideoWidth <= 0 || template.VideoHeight <= 0)
                {
                    continue;
                }

                if (template.VideoWidth == project.Video.Width &&
                    template.VideoHeight == project.Video.Height)
                {
                    return template;
                }

                if (fallback == null)
                {
                    fallback = template;
                }
            }

            return fallback;
        }

        private static RenderTemplate FindAudioTemplate(
            Vegas vegas, Project project, out bool useChannelMappingFallback)
        {
            useChannelMappingFallback = false;

            Renderer renderer = vegas.Renderers.FindByName("Wave 64");
            if (renderer == null)
            {
                return null;
            }

            RenderTemplate sixChannel = null;
            foreach (RenderTemplate template in renderer.Templates)
            {
                if (template.AudioChannelCount == Bus71Layout.BusCount)
                {
                    return template;
                }

                if (template.AudioChannelCount >= 6 &&
                    template.AudioSampleRate == project.Audio.SampleRate)
                {
                    if (sixChannel == null)
                    {
                        sixChannel = template;
                    }
                }
            }

            if (sixChannel != null)
            {
                useChannelMappingFallback = true;
                return sixChannel;
            }

            return null;
        }
    }
}
