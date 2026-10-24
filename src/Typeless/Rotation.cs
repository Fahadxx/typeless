using System;

namespace Typeless
{
    // A rotate/flip request reduced to one canonical pair. Convention, shared by ImageBackend,
    // MediaBackend and the rotate window: first mirror horizontally (when HFlip), then turn
    // Degrees clockwise (0, 90, 180 or 270). Not named Rotation, which WPF imaging already has.
    public readonly record struct RotateSpec(int Degrees, bool HFlip)
    {
        public bool IsIdentity => Degrees == 0 && !HFlip;

        // Input order: the flips first, in source orientation (h and v commute), then rotate
        // clockwise by any multiple of 90 (negative turns counter-clockwise). A vertical flip is a
        // horizontal flip plus a half turn, so v toggles HFlip and adds 180.
        public static RotateSpec Normalize(int rotate, bool flipH, bool flipV)
        {
            if (rotate % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.");
            return new RotateSpec(Mod360(rotate + (flipV ? 180 : 0)), flipH ^ flipV);
        }

        public static int Mod360(int degrees) => ((degrees % 360) + 360) % 360;
    }
}
