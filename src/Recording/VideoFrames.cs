using System.Runtime.InteropServices;

namespace WinSnipper.Recording;

/// <summary>
/// Turns a decoded RGB32 sample into a packed, top-down BGRA frame.
///
/// This exists because a decoder's row pitch is almost never the display
/// stride: rows get padded for alignment, and some pipelines hand back the
/// macroblock-aligned *coded* frame as a plain 1-D buffer while the media type
/// still advertises the display stride. Copying with the wrong pitch shears the
/// image into diagonal smears. Both the filmstrip and the trimmer feed through
/// here so they agree on what a frame looks like.
/// </summary>
internal static class VideoFrames
{
    /// <summary>Copies <paramref name="sample"/> into <paramref name="dest"/> as w*4-byte rows, top-down.</summary>
    public static void CopyPacked(IMFSample sample, int w, int h, int typeStride, IntPtr dest)
    {
        Mf.Check(sample.ConvertToContiguousBuffer(out var buffer));
        try
        {
            LockAndCopy(buffer, w, h, typeStride, dest);
        }
        finally
        {
            Mf.Release(buffer);
        }
    }

    /// <summary>Managed copy of <paramref name="sample"/> as packed top-down BGRA rows.</summary>
    public static byte[] ToPackedBytes(IMFSample sample, int w, int h, int typeStride)
    {
        var pixels = new byte[w * 4 * h];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            CopyPacked(sample, w, h, typeStride, pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }
        return pixels;
    }

    private static void LockAndCopy(IMFMediaBuffer buffer, int w, int h, int typeStride, IntPtr dest)
    {
        // IMF2DBuffer reports the true pitch; prefer it whenever it is offered.
        IntPtr scan0 = IntPtr.Zero;
        int pitch = 0;
        bool locked2d = buffer is IMF2DBuffer b2 && b2.Lock2D(out scan0, out pitch) == 0;
        if (!locked2d)
        {
            Mf.Check(buffer.Lock(out scan0, IntPtr.Zero, IntPtr.Zero));
            pitch = typeStride != 0 ? typeStride : w * 4;

            // 1-D buffer whose length doesn't match the advertised stride — it
            // is the coded frame (e.g. 1136×640 for a 1124×628 video). Derive
            // the real pitch from the buffer size instead of trusting the type.
            buffer.GetCurrentLength(out int len);
            if (pitch * h != len)
            {
                int codedH = (h + 15) & ~15;
                int derived = codedH > 0 ? len / codedH : 0;
                if (derived >= w * 4 && derived % 4 == 0)
                    pitch = derived;
            }
        }
        try
        {
            // pitch is signed: scan0 is always display row 0, a negative pitch
            // walks upward in memory (bottom-up frames).
            int rowBytes = w * 4;
            for (int row = 0; row < h; row++)
                CopyMemory(dest + row * rowBytes, scan0 + row * pitch, (UIntPtr)rowBytes);
        }
        finally
        {
            if (locked2d) ((IMF2DBuffer)buffer).Unlock2D();
            else buffer.Unlock();
        }
    }

    /// <summary>Wraps the packed pixels in a fresh MF sample the sink writer can take verbatim.</summary>
    public static IMFSample PackSample(IMFSample decoded, int w, int h, int typeStride)
    {
        int cb = w * h * 4;
        Mf.Check(Mf.MFCreateMemoryBuffer(cb, out var buffer));
        try
        {
            Mf.Check(buffer.Lock(out IntPtr dst, IntPtr.Zero, IntPtr.Zero));
            try
            {
                CopyPacked(decoded, w, h, typeStride, dst);
            }
            finally
            {
                Mf.Check(buffer.Unlock());
            }
            Mf.Check(buffer.SetCurrentLength(cb));

            Mf.Check(Mf.MFCreateSample(out var sample));
            Mf.Check(sample.AddBuffer(buffer));
            return sample;
        }
        finally
        {
            Mf.Release(buffer);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);
}
