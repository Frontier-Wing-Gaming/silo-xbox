using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display.Core;

namespace Silolux.Services
{
    /// <summary>
    /// Switches the TV to a refresh rate that fits the video (Xbox only), so 23.976, 24 and 25 fps
    /// films don't judder at the console's default 60 Hz, and signals HDR10 for HDR video.
    /// RestoreAsync puts the user's own display mode back.
    /// </summary>
    /// <remarks>
    /// The console offers 24 Hz modes only when "Allow 24 Hz" is on (Settings → General →
    /// TV &amp; display options → Video modes); without them, 24 fps video stays at 60 Hz.
    /// </remarks>
    public sealed class DisplayModeMatcher
    {
        // Refresh rate must be a whole multiple of the frame rate within 0.05%: tight enough to tell
        // 23.976 from 24 (0.1% apart, a dropped or doubled frame about every 40 seconds).
        const double Tolerance = 0.0005;
        // Second choice when nothing matches exactly, e.g. a console that offers 23.976 Hz but not
        // 24 Hz playing 24 fps: one repeated frame every ~42 s beats 3:2 judder at 59.94 Hz.
        const double LooseTolerance = 0.0015;

        readonly HdmiDisplayInformation hdmi;
        bool changed;

        DisplayModeMatcher(HdmiDisplayInformation hdmi)
        {
            this.hdmi = hdmi;
        }

        /// <summary>The matcher for this device, or null where the display mode can't be set (a PC).</summary>
        public static DisplayModeMatcher Create()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Graphics.Display.Core.HdmiDisplayInformation")) return null;
                var hdmi = HdmiDisplayInformation.GetForCurrentView();
                return hdmi == null ? null : new DisplayModeMatcher(hdmi);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Asks for the best mode for the video; true when the TV now runs at a fitting rate.</summary>
        /// <summary>What the last MatchAsync decided, for the playback report.</summary>
        public string LastDecision { get; private set; } = "not run";

        public async Task<bool> MatchAsync(double frameRate, bool hdr)
        {
            var asked = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###} fps {1}: ", frameRate, hdr ? "HDR" : "SDR");
            if (frameRate <= 0 && !hdr) { LastDecision = asked + "nothing to match"; return false; }
            try
            {
                var current = hdmi.GetCurrentDisplayMode();
                var modes = hdmi.GetSupportedDisplayModes();
                var mode = frameRate > 0 ? Pick(modes, current, frameRate, hdr, Tolerance) ?? Pick(modes, current, frameRate, hdr, LooseTolerance) : null;
                var rateFits = mode != null;
                // No fitting rate (e.g. 24 Hz not allowed): HDR video still wants HDR at the current rate.
                if (mode == null && hdr)
                    mode = modes.FirstOrDefault(m => SameShape(m, current) && Math.Abs(m.RefreshRate - current.RefreshRate) < 0.01 && m.IsSmpte2084Supported);
                if (mode == null) { LastDecision = asked + "no fitting mode offered, display left alone"; return false; }
                // Already at a fitting rate: leave the TV alone rather than blank it for a resync. For HDR
                // that also covers consoles offering no PQ mode, where an explicit "no HDR" request could
                // stop the console's own switch to HDR on playback.
                if (mode.IsEqual(current) && (!hdr || !mode.IsSmpte2084Supported))
                {
                    LastDecision = asked + "current mode already fits, display left alone" + (hdr ? " (HDR left to the console)" : "");
                    return true;
                }

                var hdrOption = hdr && mode.IsSmpte2084Supported ? HdmiDisplayHdrOption.Eotf2084 : HdmiDisplayHdrOption.None;
                var ok = await hdmi.RequestSetCurrentDisplayModeAsync(mode, hdrOption);
                changed |= ok;
                LastDecision = asked + (ok ? "switched to " : "console refused ") + Describe(mode) + " HDR option " + hdrOption;
                return ok && rateFits;
            }
            catch (Exception e)
            {
                LastDecision = asked + "failed: " + e.Message;
                return false;
            }
        }

        public static string Describe(HdmiDisplayMode m) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}×{1} @ {2:0.###} Hz, {3}-bit, {4}, {5}{6}", m.ResolutionWidthInRawPixels, m.ResolutionHeightInRawPixels,
            m.RefreshRate, m.BitsPerPixel / 3, m.ColorSpace, m.PixelEncoding, m.IsSmpte2084Supported ? ", PQ" : "");

        /// <summary>Puts back the display mode the user chose in the console's settings.</summary>
        public async Task RestoreAsync()
        {
            if (!changed) return;
            changed = false;
            try { await hdmi.SetDefaultDisplayModeAsync(); }
            catch (Exception) { }
        }

        /// <summary>
        /// The mode at the current resolution whose refresh rate is the smallest whole multiple of the
        /// frame rate (24 Hz for film over 48 Hz, 50 Hz for 25 fps), preferring the current colour setup.
        /// </summary>
        static HdmiDisplayMode Pick(IEnumerable<HdmiDisplayMode> modes, HdmiDisplayMode current, double frameRate, bool hdr, double tolerance)
        {
            HdmiDisplayMode best = null;
            var bestScore = double.MaxValue;
            foreach (var m in modes)
            {
                if (m.StereoEnabled || !SameShape(m, current)) continue;
                var multiple = Math.Round(m.RefreshRate / frameRate);
                if (multiple < 1 || Math.Abs(m.RefreshRate / (frameRate * multiple) - 1) > tolerance) continue;
                var score = multiple * 100;
                if (hdr && !m.IsSmpte2084Supported) score += 50;
                if (m.ColorSpace != current.ColorSpace) score += 5;
                if (m.BitsPerPixel != current.BitsPerPixel) score += 2;
                if (m.PixelEncoding != current.PixelEncoding) score += 1;
                if (score < bestScore)
                {
                    best = m;
                    bestScore = score;
                }
            }
            return best;
        }

        static bool SameShape(HdmiDisplayMode m, HdmiDisplayMode current) =>
            m.ResolutionWidthInRawPixels == current.ResolutionWidthInRawPixels &&
            m.ResolutionHeightInRawPixels == current.ResolutionHeightInRawPixels;
    }
}
