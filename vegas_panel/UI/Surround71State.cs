using System;

namespace VegasSurround71.UI
{
    /// <summary>
    /// Isolated 7.1 panner state. This is intentionally separate from
    /// <see cref="HoaSpatialState"/>: the HOA workspace must never be able to
    /// mutate the 7.1 puck, LFE send, spread, or the `[7.1] FL..SR` bus
    /// automation written from this state.
    /// </summary>
    public sealed class Surround71State
    {
        public const double DefaultPuckX = 0.0;
        public const double DefaultPuckY = 0.70;
        public const double DefaultLfeSend = 0.25;
        public const double DefaultSpread = 0.10;

        public double PuckX { get; set; }
        public double PuckY { get; set; }
        public double LfeSend { get; set; }
        public double Spread { get; set; }

        public static Surround71State CreateDefault()
        {
            return new Surround71State
            {
                PuckX = DefaultPuckX,
                PuckY = DefaultPuckY,
                LfeSend = DefaultLfeSend,
                Spread = DefaultSpread
            };
        }

        public Surround71State Clone()
        {
            return new Surround71State
            {
                PuckX = PuckX,
                PuckY = PuckY,
                LfeSend = LfeSend,
                Spread = Spread
            };
        }

        public bool ApproximatelyEquals(Surround71State other)
        {
            if (other == null)
            {
                return false;
            }

            return Math.Abs(PuckX - other.PuckX) < 1e-6 &&
                   Math.Abs(PuckY - other.PuckY) < 1e-6 &&
                   Math.Abs(LfeSend - other.LfeSend) < 1e-6 &&
                   Math.Abs(Spread - other.Spread) < 1e-6;
        }
    }
}
