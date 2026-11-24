using System;
using System.Globalization;

namespace VegasSurround71.UI
{
    /// <summary>Ambisonics order supported by the HOA workspace.</summary>
    public enum HoaOrder
    {
        First = 1,
        Second = 2,
        Third = 3
    }

    /// <summary>Preview monitoring mode for the HOA workspace.</summary>
    public enum HoaPreviewMode
    {
        Binaural = 0,
        StereoDownmix = 1,
        Bypass = 2
    }

    /// <summary>HOA render source: the selected track or the full project mix.</summary>
    public enum HoaSourceMode
    {
        SelectedTrack = 0,
        FullMix = 1
    }

    /// <summary>Distance attenuation curve applied to export and preview.</summary>
    public enum HoaDistanceModel
    {
        Inverse = 0,
        InverseSquare = 1,
        Linear = 2,
        None = 3
    }

    /// <summary>Binaural head model used by the monitor preview.</summary>
    public enum HoaHeadModel
    {
        Both = 0,
        ItdOnly = 1,
        IldOnly = 2,
        Off = 3
    }

    /// <summary>HOA delivery targets. IAMF / Eclipsa is a planned placeholder.</summary>
    public enum HoaExportTarget
    {
        AmbiXWavFlac = 0,
        IamfEclipsaPlanned = 1
    }

    /// <summary>
    /// Pure 3D soundstage math for the HOA workspace. All helpers are
    /// UI-independent so the geometry can be verified offline.
    /// </summary>
    public static class HoaSpatialMath
    {
        public const double MinAzimuth = -180.0;
        public const double MaxAzimuth = 180.0;
        public const double MinElevation = -90.0;
        public const double MaxElevation = 90.0;
        public const double MinDistance = 0.0;
        public const double MaxDistance = 1.0;
        public const double MinSpread = 0.0;
        public const double MaxSpread = 1.0;

        /// <summary>ACN channel count for an order: 4, 9, or 16.</summary>
        public static int ChannelCount(int order)
        {
            return order >= NativeBridge.HoaMinOrder && order <= NativeBridge.HoaMaxOrder
                ? (order + 1) * (order + 1)
                : 0;
        }

        /// <summary>Clamps a value into `[min, max]`.</summary>
        public static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value))
            {
                return min;
            }

            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>Wraps an azimuth into the display range `[-180, 180]`.</summary>
        public static double NormalizeAzimuth(double degrees)
        {
            double value = degrees;
            while (value > MaxAzimuth)
            {
                value -= 360.0;
            }

            while (value < MinAzimuth)
            {
                value += 360.0;
            }

            return value;
        }

        /// <summary>
        /// Maps azimuth + distance to the overhead radar canvas. `0°` points up
        /// (front), `+90°` points right, and `±180°` points down (rear).
        /// </summary>
        public static StagePoint ToOverheadScreen(
            double azimuthDegrees,
            double distance,
            double centerX,
            double centerY,
            double radius)
        {
            double azimuth = NormalizeAzimuth(azimuthDegrees) * Math.PI / 180.0;
            double range = Clamp(distance, MinDistance, MaxDistance) * radius;
            return new StagePoint(
                centerX + Math.Sin(azimuth) * range,
                centerY - Math.Cos(azimuth) * range);
        }

        /// <summary>
        /// Converts an overhead canvas point into azimuth and distance. The
        /// result is clamped to the unit circle so the puck stays in the room.
        /// </summary>
        public static void FromOverheadScreen(
            double px,
            double py,
            double centerX,
            double centerY,
            double radius,
            out double azimuthDegrees,
            out double distance)
        {
            if (radius <= 0.0)
            {
                azimuthDegrees = 0.0;
                distance = 0.0;
                return;
            }

            double x = (px - centerX) / radius;
            double y = (centerY - py) / radius;
            double length = Math.Sqrt(x * x + y * y);
            if (length > 1.0)
            {
                x /= length;
                y /= length;
                length = 1.0;
            }

            azimuthDegrees = NormalizeAzimuth(Math.Atan2(x, y) * 180.0 / Math.PI);
            distance = Clamp(length, MinDistance, MaxDistance);
        }

        /// <summary>Maps elevation to the vertical elevation rail.</summary>
        public static double ElevationToScreenY(
            double elevationDegrees, double centerY, double halfHeight)
        {
            if (halfHeight <= 0.0)
            {
                return centerY;
            }

            double elevation = Clamp(elevationDegrees, MinElevation, MaxElevation);
            return centerY - (elevation / MaxElevation) * halfHeight;
        }

        /// <summary>Converts an elevation-rail point into degrees.</summary>
        public static double ScreenYToElevation(
            double py, double centerY, double halfHeight)
        {
            if (halfHeight <= 0.0)
            {
                return 0.0;
            }

            double elevation = (centerY - py) / halfHeight * MaxElevation;
            return Clamp(elevation, MinElevation, MaxElevation);
        }

        /// <summary>Visual spread halo radius in pixels.</summary>
        public static double SpreadHaloRadius(double spread, double radius)
        {
            double clamped = Clamp(spread, MinSpread, MaxSpread);
            return radius * (0.08 + 0.42 * clamped);
        }

        /// <summary>Formats a signed angle readout, e.g. `+37.5°`.</summary>
        public static string FormatDegrees(double value)
        {
            return value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) +
                "\u00B0";
        }

        /// <summary>Formats a normalized 0..1 readout.</summary>
        public static string FormatUnit(double value)
        {
            return Clamp(value, MinDistance, MaxDistance)
                .ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>Exact order label required by the HOA selector.</summary>
        public static string OrderLabel(int order)
        {
            switch (order)
            {
                case 1: return "1st Order (4 ch)";
                case 2: return "2nd Order (9 ch)";
                case 3: return "3rd Order (16 ch)";
                default: return "Unknown order";
            }
        }

        /// <summary>Compact order label used in header pills.</summary>
        public static string OrderShortLabel(int order)
        {
            switch (order)
            {
                case 1: return "1st Order";
                case 2: return "2nd Order";
                case 3: return "3rd Order";
                default: return "—";
            }
        }

        public static string PreviewLabel(HoaPreviewMode mode)
        {
            switch (mode)
            {
                case HoaPreviewMode.StereoDownmix:
                    return "Stereo Downmix";
                case HoaPreviewMode.Bypass:
                    return "Bypass (Reference)";
                default:
                    return "Binaural (Headphones)";
            }
        }

        public static string SourceModeLabel(HoaSourceMode mode)
        {
            return mode == HoaSourceMode.FullMix
                ? "Full Mix (master)"
                : "Selected Track";
        }

        public static string DistanceModelLabel(HoaDistanceModel model)
        {
            switch (model)
            {
                case HoaDistanceModel.InverseSquare:
                    return "Inverse Square (1/d²)";
                case HoaDistanceModel.Linear:
                    return "Linear (1 - d)";
                case HoaDistanceModel.None:
                    return "No Distance Rolloff";
                default:
                    return "Inverse (1/d)";
            }
        }

        public static string HeadModelLabel(HoaHeadModel model)
        {
            switch (model)
            {
                case HoaHeadModel.ItdOnly:
                    return "ITD only";
                case HoaHeadModel.IldOnly:
                    return "ILD / head shadow only";
                case HoaHeadModel.Off:
                    return "HRTF off (stereo pan)";
                default:
                    return "ITD + ILD (default)";
            }
        }

        public static string ExportTargetLabel(HoaExportTarget target)
        {
            return target == HoaExportTarget.IamfEclipsaPlanned
                ? "IAMF / Eclipsa (Planned)"
                : "HOA WAV/FLAC (AmbiX)";
        }
    }

    /// <summary>
    /// Isolated HOA / 3D Spatial workspace state. Keeping this separate from
    /// <see cref="Surround71State"/> guarantees that HOA parameters can never be
    /// routed into the `[7.1] FL..SR` bus envelopes.
    /// </summary>
    public sealed class HoaSpatialState
    {
        public const double DefaultAzimuthDegrees = 0.0;
        public const double DefaultElevationDegrees = 10.0;
        public const double DefaultDistance = 0.70;
        public const double DefaultSpread = 0.10;
        public const int DefaultOrder = (int)HoaOrder.First;

        public double AzimuthDegrees { get; set; }
        public double ElevationDegrees { get; set; }
        public double Distance { get; set; }
        public double Spread { get; set; }
        public HoaOrder Order { get; set; }
        public HoaPreviewMode PreviewMode { get; set; }
        public HoaExportTarget ExportTarget { get; set; }
        public HoaSourceMode SourceMode { get; set; }
        public HoaDistanceModel DistanceModel { get; set; }
        public double AirAbsorption { get; set; }
        public HoaHeadModel HeadModel { get; set; }
        public double HeadWidth { get; set; }
        public double RoomAmount { get; set; }
        public double RoomSize { get; set; }

        public HoaSpatialState()
        {
            Reset();
        }

        /// <summary>ACN channel count implied by the selected order.</summary>
        public int ChannelCount
        {
            get { return HoaSpatialMath.ChannelCount((int)Order); }
        }

        /// <summary>Restores the workspace-local factory position.</summary>
        public void Reset()
        {
            AzimuthDegrees = DefaultAzimuthDegrees;
            ElevationDegrees = DefaultElevationDegrees;
            Distance = DefaultDistance;
            Spread = DefaultSpread;
            Order = (HoaOrder)DefaultOrder;
            PreviewMode = HoaPreviewMode.Binaural;
            ExportTarget = HoaExportTarget.AmbiXWavFlac;
            SourceMode = HoaSourceMode.SelectedTrack;
            DistanceModel = HoaDistanceModel.Inverse;
            AirAbsorption = 0.0;
            HeadModel = HoaHeadModel.Both;
            HeadWidth = 1.0;
            RoomAmount = 0.0;
            RoomSize = 0.5;
        }

        public HoaSpatialState Clone()
        {
            return new HoaSpatialState
            {
                AzimuthDegrees = AzimuthDegrees,
                ElevationDegrees = ElevationDegrees,
                Distance = Distance,
                Spread = Spread,
                Order = Order,
                PreviewMode = PreviewMode,
                ExportTarget = ExportTarget,
                SourceMode = SourceMode,
                DistanceModel = DistanceModel,
                AirAbsorption = AirAbsorption,
                HeadModel = HeadModel,
                HeadWidth = HeadWidth,
                RoomAmount = RoomAmount,
                RoomSize = RoomSize
            };
        }

        /// <summary>
        /// Resets only the effects/monitoring controls. Position, order, preview
        /// mode, export target, and output path are left untouched.
        /// </summary>
        public void ResetEffects()
        {
            DistanceModel = HoaDistanceModel.Inverse;
            AirAbsorption = 0.0;
            HeadModel = HoaHeadModel.Both;
            HeadWidth = 1.0;
            RoomAmount = 0.0;
            RoomSize = 0.5;
        }

        /// <summary>Copies all fields from another state (A/B recall).</summary>
        public void CopyFrom(HoaSpatialState other)
        {
            if (other == null)
            {
                return;
            }

            AzimuthDegrees = other.AzimuthDegrees;
            ElevationDegrees = other.ElevationDegrees;
            Distance = other.Distance;
            Spread = other.Spread;
            Order = other.Order;
            PreviewMode = other.PreviewMode;
            ExportTarget = other.ExportTarget;
            SourceMode = other.SourceMode;
            DistanceModel = other.DistanceModel;
            AirAbsorption = other.AirAbsorption;
            HeadModel = other.HeadModel;
            HeadWidth = other.HeadWidth;
            RoomAmount = other.RoomAmount;
            RoomSize = other.RoomSize;
        }
    }
}
