//! DSP Module: SMPTE 7.1 channel remapping and Linkwitz-Riley LFE filtering.
//!
//! VEGAS Pro renders eight discrete mono busses (`c0`..`c7`) into a Sony Wave64
//! (`.w64`) container. The helpers in this module emit the FFmpeg
//! `filter_complex` graph that maps those discrete inputs onto the canonical
//! SMPTE / ITU-R BS.775 7.1 channel order expected by the FLAC and Opus
//! encoders.
//!
//! # Canonical bus -> channel mapping
//!
//! | Bus | Channel | Description           |
//! |-----|---------|-----------------------|
//! | c0  | FL      | Front Left            |
//! | c1  | FR      | Front Right           |
//! | c2  | FC      | Front Center          |
//! | c3  | LFE     | Low Frequency Effects |
//! | c4  | BL      | Back / Rear Left      |
//! | c5  | BR      | Back / Rear Right     |
//! | c6  | SL      | Side Surround Left    |
//! | c7  | SR      | Side Surround Right   |
//!
//! The explicit `pan` mapping is intentional: `.w64` files carry no reliable
//! channel mask, so FFmpeg must never infer the layout from container metadata.

/// Standard 7.1 crossover frequency for the LFE low-pass stage.
pub const DEFAULT_LFE_CUTOFF_HZ: u32 = 120;

/// Lowest cutoff accepted by [`build_71_filter_complex`] after clamping.
pub const MIN_LFE_CUTOFF_HZ: u32 = 20;

/// Highest cutoff accepted by [`build_71_filter_complex`] after clamping.
pub const MAX_LFE_CUTOFF_HZ: u32 = 20_000;

/// Ordered (VEGAS bus id, SMPTE channel label) pairs.
///
/// The index in this array is the discrete W64 channel index (`c0`..`c7`).
pub const BUS_CHANNEL_MAP: [(&str, &str); 8] = [
    ("c0", "FL"),
    ("c1", "FR"),
    ("c2", "FC"),
    ("c3", "LFE"),
    ("c4", "BL"),
    ("c5", "BR"),
    ("c6", "SL"),
    ("c7", "SR"),
];

/// SMPTE channel labels ordered by W64 channel index.
pub const BUS_CHANNELS: [&str; 8] = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"];

/// The body of the FFmpeg `pan` filter, mapping input channel `cN` to its
/// canonical 7.1 output slot.
pub const PAN_71_CHANNEL_MAP: &str =
    "FL=c0|FR=c1|FC=c2|LFE=c3|BL=c4|BR=c5|SL=c6|SR=c7";

/// Returns the SMPTE channel label for a discrete bus index (`0..=7`).
pub fn channel_for_bus(index: usize) -> Option<&'static str> {
    BUS_CHANNELS.get(index).copied()
}

/// Builds the FFmpeg `-filter_complex` graph that remaps the discrete 8-channel
/// VEGAS Wave64 render (input `1:a`) into an explicit 7.1 output stream `[aout]`.
///
/// # Arguments
/// - `apply_lfe_lowpass`: when `true`, the LFE channel (`c3`) is passed through a
///   4th-order Linkwitz-Riley low-pass (two cascaded 2nd-order Butterworth
///   stages, i.e. 24 dB/octave) before being rejoined into the 7.1 layout.
/// - `lfe_cutoff_hz`: crossover frequency for the LFE cascade. Values are
///   clamped to [`MIN_LFE_CUTOFF_HZ`]..=[`MAX_LFE_CUTOFF_HZ`].
///
/// # Notes
/// A Linkwitz-Riley 4th-order low-pass is exactly two cascaded Butterworth
/// 2nd-order sections (each `Q = 1/sqrt(2)`), which FFmpeg implements as
/// `lowpass=...:poles=2`. Cascading two identical sections yields the required
/// 24 dB/octave slope with a -6 dB point at the crossover frequency.
pub fn build_71_filter_complex(apply_lfe_lowpass: bool, lfe_cutoff_hz: u32) -> String {
    let pan = format!("[1:a]pan=7.1|{PAN_71_CHANNEL_MAP}");

    if !apply_lfe_lowpass {
        return format!("{pan}[aout]");
    }

    let cutoff = lfe_cutoff_hz.clamp(MIN_LFE_CUTOFF_HZ, MAX_LFE_CUTOFF_HZ);

    format!(
        "{pan},channelsplit=channel_layout=7.1[fl][fr][fc][lfe_raw][bl][br][sl][sr];\
         [lfe_raw]lowpass=f={cutoff}:poles=2,lowpass=f={cutoff}:poles=2[lfe];\
         [fl][fr][fc][lfe][bl][br][sl][sr]join=inputs=8:channel_layout=7.1[aout]"
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Explicit mapping without the LFE stage: must match the canonical graph
    /// byte-for-byte, independent of the cutoff argument.
    #[test]
    fn test_filter_complex_without_lfe_lowpass() {
        let expected = "[1:a]pan=7.1|FL=c0|FR=c1|FC=c2|LFE=c3|BL=c4|BR=c5|SL=c6|SR=c7[aout]";

        assert_eq!(build_71_filter_complex(false, 120), expected);
        assert_eq!(build_71_filter_complex(false, 80), expected);
    }

    /// 120 Hz LR4 cascade: exact graph mandated by the delivery specification.
    #[test]
    fn test_filter_complex_with_lfe_lowpass_120hz() {
        let expected = "[1:a]pan=7.1|FL=c0|FR=c1|FC=c2|LFE=c3|BL=c4|BR=c5|SL=c6|SR=c7,\
                        channelsplit=channel_layout=7.1[fl][fr][fc][lfe_raw][bl][br][sl][sr];\
                        [lfe_raw]lowpass=f=120:poles=2,lowpass=f=120:poles=2[lfe];\
                        [fl][fr][fc][lfe][bl][br][sl][sr]join=inputs=8:channel_layout=7.1[aout]";

        assert_eq!(build_71_filter_complex(true, DEFAULT_LFE_CUTOFF_HZ), expected);
    }

    /// The LFE filter must only touch `c3` and must be a two-stage cascade.
    #[test]
    fn test_lfe_lowpass_custom_cutoff_and_isolation() {
        let graph = build_71_filter_complex(true, 80);

        assert!(graph.contains("[lfe_raw]lowpass=f=80:poles=2,lowpass=f=80:poles=2[lfe]"));
        // Exactly two Butterworth stages -> 4th order (24 dB/octave).
        assert_eq!(graph.matches("lowpass=f=80:poles=2").count(), 2);
        // Only the LFE branch is filtered; the pan stage is untouched.
        assert!(graph.starts_with(
            "[1:a]pan=7.1|FL=c0|FR=c1|FC=c2|LFE=c3|BL=c4|BR=c5|SL=c6|SR=c7,channelsplit="
        ));
        assert!(!graph.contains("lowpass=f=80:poles=2,lowpass=f=80:poles=2,"));
    }

    /// Out-of-range cutoffs must be clamped to a filter-safe range.
    #[test]
    fn test_lfe_cutoff_clamped() {
        assert!(build_71_filter_complex(true, 0).contains("lowpass=f=20:poles=2"));
        assert!(build_71_filter_complex(true, u32::MAX).contains("lowpass=f=20000:poles=2"));
    }

    /// The canonical bus -> channel table must stay in SMPTE 7.1 order.
    #[test]
    fn test_bus_channel_mapping() {
        assert_eq!(
            BUS_CHANNEL_MAP,
            [
                ("c0", "FL"),
                ("c1", "FR"),
                ("c2", "FC"),
                ("c3", "LFE"),
                ("c4", "BL"),
                ("c5", "BR"),
                ("c6", "SL"),
                ("c7", "SR"),
            ]
        );
        assert_eq!(channel_for_bus(0), Some("FL"));
        assert_eq!(channel_for_bus(3), Some("LFE"));
        assert_eq!(channel_for_bus(7), Some("SR"));
        assert_eq!(channel_for_bus(8), None);
    }
}
