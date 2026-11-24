using System;
using System.Globalization;

namespace VegasSurround71.UI
{
    /// <summary>Plain 2D point used by the soundstage math (UI-independent).</summary>
    public struct StagePoint
    {
        public StagePoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; private set; }
        public double Y { get; private set; }
    }

    /// <summary>Speaker node in normalized room coordinates (x right, y front).</summary>
    public sealed class SpeakerNode
    {
        public SpeakerNode(string channel, string label, double x, double y)
        {
            Channel = channel;
            Label = label;
            X = x;
            Y = y;
        }

        public string Channel { get; private set; }
        public string Label { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
    }

    /// <summary>
    /// Pure soundstage/meter math shared by the WPF view and Phase 4B tests.
    /// Canonical speaker azimuths: FL -30, FC 0, FR +30, SL -90, SR +90,
    /// BL -150, BR +150; LFE is drawn at the bottom of the room.
    /// </summary>
    public static class SoundstageMath
    {
        public const double MeterFloorDb = -60.0;
        public static readonly string[] ChannelLabels = { "FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR" };

        /// <summary>Human-readable meter row labels shown in the live gain panel.</summary>
        public static readonly string[] MeterLabels =
        {
            "FL \u00B7 Front L",
            "FR \u00B7 Front R",
            "FC \u00B7 Center",
            "LFE \u00B7 Sub",
            "BL \u00B7 Back L",
            "BR \u00B7 Back R",
            "SL \u00B7 Side L",
            "SR \u00B7 Side R"
        };

        public static readonly SpeakerNode[] Speakers =
        {
            FromAngle("FL", "FL", -30.0),
            FromAngle("FC", "FC", 0.0),
            FromAngle("FR", "FR", 30.0),
            FromAngle("SL", "SL", -90.0),
            FromAngle("SR", "SR", 90.0),
            FromAngle("BL", "BL", -150.0),
            FromAngle("BR", "BR", 150.0),
            new SpeakerNode("LFE", "LFE", 0.0, -0.92)
        };

        private static SpeakerNode FromAngle(string channel, string label, double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            return new SpeakerNode(channel, label, Math.Sin(radians), Math.Cos(radians));
        }

        /// <summary>Maps normalized room coordinates to canvas pixels.</summary>
        public static StagePoint ToScreen(
            double x, double y, double centerX, double centerY, double radius)
        {
            return new StagePoint(centerX + x * radius, centerY - y * radius);
        }

        /// <summary>
        /// Maps canvas pixels to normalized room coordinates. The result is
        /// clamped to the unit circle so the puck can never leave the room.
        /// </summary>
        public static StagePoint FromScreen(
            double px, double py, double centerX, double centerY, double radius)
        {
            if (radius <= 0.0)
            {
                return new StagePoint(0.0, 0.0);
            }

            double x = (px - centerX) / radius;
            double y = (centerY - py) / radius;
            double length = Math.Sqrt(x * x + y * y);
            if (length > 1.0)
            {
                x /= length;
                y /= length;
            }

            return new StagePoint(x, y);
        }

        /// <summary>Converts a linear gain to dB, clamped to the meter floor.</summary>
        public static double GainToDb(double gain)
        {
            if (gain <= 0.0 || double.IsNaN(gain))
            {
                return MeterFloorDb;
            }

            double db = 20.0 * Math.Log10(gain);
            return Math.Max(MeterFloorDb, Math.Min(0.0, db));
        }

        /// <summary>Linear 0..1 fill fraction for a meter bar.</summary>
        public static double MeterFillFraction(double gain)
        {
            if (double.IsNaN(gain))
            {
                return 0.0;
            }

            return Math.Max(0.0, Math.Min(1.0, gain));
        }

        public static string FormatDb(double gain)
        {
            double db = GainToDb(gain);
            return db <= MeterFloorDb + 0.0001
                ? "-\u221E"
                : db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        public static string FormatLinear(double gain)
        {
            return Math.Max(0.0, Math.Min(1.0, gain))
                .ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
