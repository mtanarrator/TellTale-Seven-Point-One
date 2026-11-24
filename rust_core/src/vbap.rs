//! 2D 7.1 Vector Base Amplitude Panning (VBAP) Engine
//!
//! Based on standard ITU-R BS.775 / SMPTE 7.1 speaker layouts on the unit circle:
//! - FL:  -30° (Front Left)
//! - FR:  +30° (Front Right)
//! - FC:    0° (Front Center)
//! - LFE: Non-directional subwoofer
//! - BL: -150° (Back Left / Rear Left)
//! - BR: +150° (Back Right / Rear Right)
//! - SL:  -90° (Side Left)
//! - SR:  +90° (Side Right)

use std::f32::consts::PI;

/// Discrete 8-channel gain distribution for 7.1 surround.
/// Layout matches C# P/Invoke struct byte-for-byte.
#[repr(C)]
#[derive(Debug, Copy, Clone, PartialEq)]
pub struct ChannelGains71 {
    pub fl: f32,
    pub fr: f32,
    pub fc: f32,
    pub lfe: f32,
    pub bl: f32,
    pub br: f32,
    pub sl: f32,
    pub sr: f32,
}

impl Default for ChannelGains71 {
    fn default() -> Self {
        Self {
            fl: 0.0,
            fr: 0.0,
            fc: 0.0,
            lfe: 0.0,
            bl: 0.0,
            br: 0.0,
            sl: 0.0,
            sr: 0.0,
        }
    }
}

/// Speaker definition in 2D Cartesian space (unit circle).
#[derive(Debug, Copy, Clone)]
struct SpeakerDef {
    angle_rad: f32,
    x: f32,
    y: f32,
}

impl SpeakerDef {
    fn from_angle_deg(deg: f32) -> Self {
        let rad = deg * PI / 180.0;
        let mut normalized_rad = rad;
        if normalized_rad < 0.0 {
            normalized_rad += 2.0 * PI;
        }
        Self {
            angle_rad: normalized_rad,
            x: rad.sin(),
            y: rad.cos(),
        }
    }
}

/// Enumeration of the 7 satellite speakers.
#[derive(Debug, Copy, Clone, PartialEq, Eq)]
enum SatelliteSpeaker {
    Fc = 0,
    Fr = 1,
    Sr = 2,
    Br = 3,
    Bl = 4,
    Sl = 5,
    Fl = 6,
}

/// Ordered speaker segments covering [0, 2*PI) continuously.
struct SpeakerRing {
    speakers: [SpeakerDef; 7],
}

impl SpeakerRing {
    fn new() -> Self {
        Self {
            speakers: [
                SpeakerDef::from_angle_deg(0.0),    // FC: 0°
                SpeakerDef::from_angle_deg(30.0),   // FR: 30°
                SpeakerDef::from_angle_deg(90.0),   // SR: 90°
                SpeakerDef::from_angle_deg(150.0),  // BR: 150°
                SpeakerDef::from_angle_deg(-150.0), // BL: 210° (-150°)
                SpeakerDef::from_angle_deg(-90.0),  // SL: 270° (-90°)
                SpeakerDef::from_angle_deg(-30.0),  // FL: 330° (-30°)
            ],
        }
    }

    /// Finds the adjacent speaker pair enclosing azimuth `azimuth_rad` in [0, 2*PI).
    fn find_pair(&self, azimuth_rad: f32) -> (usize, usize) {
        // Speaker angles:
        // [0] FC: 0.0
        // [1] FR: PI/6   (30°)
        // [2] SR: PI/2   (90°)
        // [3] BR: 5*PI/6 (150°)
        // [4] BL: 7*PI/6 (210°)
        // [5] SL: 3*PI/2 (270°)
        // [6] FL: 11*PI/6 (330°)
        if azimuth_rad < self.speakers[1].angle_rad {
            (0, 1) // FC -> FR
        } else if azimuth_rad < self.speakers[2].angle_rad {
            (1, 2) // FR -> SR
        } else if azimuth_rad < self.speakers[3].angle_rad {
            (2, 3) // SR -> BR
        } else if azimuth_rad < self.speakers[4].angle_rad {
            (3, 4) // BR -> BL (across rear center)
        } else if azimuth_rad < self.speakers[5].angle_rad {
            (4, 5) // BL -> SL
        } else if azimuth_rad < self.speakers[6].angle_rad {
            (5, 6) // SL -> FL
        } else {
            (6, 0) // FL -> FC (across front left to center)
        }
    }
}

/// Calculates 7.1 VBAP channel gains for a given 2D soundstage position.
///
/// # Arguments
/// - `x`: Horizontal position in [-1.0, 1.0] (-1.0 = full left, +1.0 = full right)
/// - `y`: Vertical position in [-1.0, 1.0] (+1.0 = front, -1.0 = back)
/// - `lfe_send`: Manual LFE channel send level in [0.0, 1.0]
/// - `spread`: Spatial diffusion / source width in [0.0, 1.0] (0.0 = point source)
pub fn calculate_gains(x: f32, y: f32, lfe_send: f32, spread: f32) -> ChannelGains71 {
    let r_sq = x * x + y * y;
    let r = r_sq.sqrt();

    // 1. Calculate LFE gain based on dedicated send and radial proximity to listener (0,0)
    let proximity = (1.0 - r.min(1.0)).max(0.0);
    let lfe = (lfe_send.clamp(0.0, 1.0) * (1.0 + proximity)).clamp(0.0, 1.0);

    // If source is at the listener origin, distribute power equally across satellites
    let omni_gain = 1.0 / (7.0_f32).sqrt();
    if r < 1e-6 {
        return ChannelGains71 {
            fl: omni_gain,
            fr: omni_gain,
            fc: omni_gain,
            lfe,
            bl: omni_gain,
            br: omni_gain,
            sl: omni_gain,
            sr: omni_gain,
        };
    }

    // 2. Compute azimuth angle in [0, 2*PI)
    // x is horizontal (sin), y is vertical (cos): atan2(x, y) gives 0 at Front Center (0, 1)
    let mut azimuth = x.atan2(y);
    if azimuth < 0.0 {
        azimuth += 2.0 * PI;
    }

    // Normalized directional unit vector
    let u_x = x / r;
    let u_y = y / r;

    // 3. Find enclosing speaker pair
    let ring = SpeakerRing::new();
    let (idx_a, idx_b) = ring.find_pair(azimuth);
    let spk_a = ring.speakers[idx_a];
    let spk_b = ring.speakers[idx_b];

    // 2D 2x2 Matrix Inversion: L12 = [ [x_a, x_b], [y_a, y_b] ]
    // det = x_a * y_b - x_b * y_a
    let det = spk_a.x * spk_b.y - spk_b.x * spk_a.y;
    let mut g_raw = [0.0_f32; 7];

    if det.abs() > 1e-7 {
        // g = L12^-1 * u
        let g_a = (spk_b.y * u_x - spk_b.x * u_y) / det;
        let g_b = (-spk_a.y * u_x + spk_a.x * u_y) / det;

        g_raw[idx_a] = g_a.max(0.0);
        g_raw[idx_b] = g_b.max(0.0);
    } else {
        // Degenerate fallback (collinear speakers)
        g_raw[idx_a] = 1.0;
    }

    // Normalize raw VBAP pair power
    let pair_power: f32 = g_raw[idx_a] * g_raw[idx_a] + g_raw[idx_b] * g_raw[idx_b];
    if pair_power > 1e-12 {
        let norm = 1.0 / pair_power.sqrt();
        g_raw[idx_a] *= norm;
        g_raw[idx_b] *= norm;
    }

    // 4. Apply distance attenuation / center diffusion and spread
    // As r approaches 0, source spreads into ambient room presence
    let clamped_spread = spread.clamp(0.0, 1.0);
    let distance_diffusion = (1.0 - r.min(1.0)).max(0.0);
    // Effective spread blends user spread with origin proximity
    let effective_spread = clamped_spread + distance_diffusion * (1.0 - clamped_spread);
    let effective_spread = effective_spread.clamp(0.0, 1.0);

    let mut satellite_gains = [0.0_f32; 7];
    for i in 0..7 {
        satellite_gains[i] = (1.0 - effective_spread) * g_raw[i] + effective_spread * omni_gain;
    }

    // 5. Enforce power normalization: sum(g_i^2) = 1.0
    let total_power: f32 = satellite_gains.iter().map(|&g| g * g).sum();
    if total_power > 1e-12 {
        let power_norm = 1.0 / total_power.sqrt();
        for g in satellite_gains.iter_mut() {
            *g *= power_norm;
        }
    }

    // Map ordered satellite indices back to ChannelGains71:
    // [0] FC, [1] FR, [2] SR, [3] BR, [4] BL, [5] SL, [6] FL
    ChannelGains71 {
        fl: satellite_gains[SatelliteSpeaker::Fl as usize],
        fr: satellite_gains[SatelliteSpeaker::Fr as usize],
        fc: satellite_gains[SatelliteSpeaker::Fc as usize],
        lfe,
        bl: satellite_gains[SatelliteSpeaker::Bl as usize],
        br: satellite_gains[SatelliteSpeaker::Br as usize],
        sl: satellite_gains[SatelliteSpeaker::Sl as usize],
        sr: satellite_gains[SatelliteSpeaker::Sr as usize],
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Pure Front Center (0.0, 1.0) must route 100% energy to FC.
    #[test]
    fn test_pure_front_center() {
        let gains = calculate_gains(0.0, 1.0, 0.0, 0.0);

        assert!(
            (gains.fc - 1.0).abs() < 1e-4,
            "FC expected ~1.0, got {}",
            gains.fc
        );
        assert!(gains.fl < 1e-4, "FL expected 0.0, got {}", gains.fl);
        assert!(gains.fr < 1e-4, "FR expected 0.0, got {}", gains.fr);
        assert!(gains.sl < 1e-4, "SL expected 0.0, got {}", gains.sl);
        assert!(gains.sr < 1e-4, "SR expected 0.0, got {}", gains.sr);
        assert!(gains.bl < 1e-4, "BL expected 0.0, got {}", gains.bl);
        assert!(gains.br < 1e-4, "BR expected 0.0, got {}", gains.br);
        assert_eq!(gains.lfe, 0.0);

        let energy = gains.fc * gains.fc;
        assert!((energy - 1.0).abs() < 1e-4);
    }

    /// Hard Left (-1.0, 0.0) must route energy cleanly to SL.
    #[test]
    fn test_hard_left_side() {
        let gains = calculate_gains(-1.0, 0.0, 0.0, 0.0);

        assert!(
            (gains.sl - 1.0).abs() < 1e-4,
            "SL expected ~1.0, got {}",
            gains.sl
        );
        assert!(gains.fc < 1e-4, "FC expected 0.0, got {}", gains.fc);
        assert!(gains.fl < 1e-4, "FL expected 0.0, got {}", gains.fl);
        assert!(gains.fr < 1e-4, "FR expected 0.0, got {}", gains.fr);
        assert!(gains.sr < 1e-4, "SR expected 0.0, got {}", gains.sr);
        assert!(gains.bl < 1e-4, "BL expected 0.0, got {}", gains.bl);
        assert!(gains.br < 1e-4, "BR expected 0.0, got {}", gains.br);
        assert_eq!(gains.lfe, 0.0);

        let energy = gains.sl * gains.sl;
        assert!((energy - 1.0).abs() < 1e-4);
    }

    /// Hard Right (+1.0, 0.0) must route energy cleanly to SR.
    #[test]
    fn test_hard_right_side() {
        let gains = calculate_gains(1.0, 0.0, 0.0, 0.0);

        assert!(
            (gains.sr - 1.0).abs() < 1e-4,
            "SR expected ~1.0, got {}",
            gains.sr
        );
        assert!(gains.sl < 1e-4);
        assert!(gains.fc < 1e-4);
    }

    /// Energy conservation (sum(g_i^2) ≈ 1.0) must hold across 100 randomized coordinates.
    #[test]
    fn test_energy_conservation_100_random_points() {
        // Linear Congruential Generator for deterministic, cross-platform pseudo-random tests
        let mut seed: u64 = 0xDEADBEEFCAFE;
        let mut next_f32 = |min: f32, max: f32| -> f32 {
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1);
            let val = ((seed >> 33) as f32) / ((1u32 << 31) as f32);
            min + val * (max - min)
        };

        for i in 0..100 {
            let x = next_f32(-1.0, 1.0);
            let y = next_f32(-1.0, 1.0);
            let spread = next_f32(0.0, 1.0);
            let lfe_send = next_f32(0.0, 1.0);

            let gains = calculate_gains(x, y, lfe_send, spread);

            let satellite_energy = gains.fl * gains.fl
                + gains.fr * gains.fr
                + gains.fc * gains.fc
                + gains.bl * gains.bl
                + gains.br * gains.br
                + gains.sl * gains.sl
                + gains.sr * gains.sr;

            assert!(
                (satellite_energy - 1.0).abs() < 1e-4,
                "Iteration {}: x={}, y={}, spread={}, satellite energy={} (expected ~1.0)",
                i,
                x,
                y,
                spread,
                satellite_energy
            );

            // All satellite gains must be non-negative
            assert!(gains.fl >= 0.0);
            assert!(gains.fr >= 0.0);
            assert!(gains.fc >= 0.0);
            assert!(gains.bl >= 0.0);
            assert!(gains.br >= 0.0);
            assert!(gains.sl >= 0.0);
            assert!(gains.sr >= 0.0);
            assert!(gains.lfe >= 0.0 && gains.lfe <= 1.0);
        }
    }

    /// LFE gain should be computed from lfe_send and proximity to listener origin.
    #[test]
    fn test_lfe_radial_proximity() {
        // At origin (0, 0), proximity = 1.0, lfe = clamp(lfe_send * (1 + 1))
        let gains_center = calculate_gains(0.0, 0.0, 0.4, 0.0);
        assert!((gains_center.lfe - 0.8).abs() < 1e-4);

        // At perimeter (0, 1), proximity = 0.0, lfe = lfe_send
        let gains_perimeter = calculate_gains(0.0, 1.0, 0.4, 0.0);
        assert!((gains_perimeter.lfe - 0.4).abs() < 1e-4);

        // If lfe_send is 0.0, lfe must remain 0.0 everywhere
        let gains_zero_lfe = calculate_gains(0.0, 0.0, 0.0, 0.0);
        assert_eq!(gains_zero_lfe.lfe, 0.0);
    }
}
