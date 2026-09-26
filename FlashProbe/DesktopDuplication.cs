using System.Runtime.InteropServices;

namespace FlashProbe;

/// <summary>Brightness statistics of one screen area (Rec. 709 luma, 0-255).</summary>
internal readonly record struct Stats(double Avg, double LightPct, double DarkPct);

/// <summary>What one composed frame looked like at the window position.</summary>
internal sealed class Measurement
{
    public Stats Client { get; init; }
    public Stats Title { get; init; }
    public byte[]? Rgb { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
}

/// <summary>
/// DXGI Desktop Duplication of every attached monitor. AcquireNextFrame returns each frame DWM composes,
/// so a flash that lasts a single frame is still recorded. The COM interfaces are called through their
/// vtables (slot numbers from dxgi.h, dxgi1_2.h and d3d11.h), which keeps the tool free of packages.
/// </summary>
internal sealed unsafe class DesktopDuplication
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int NotFound = unchecked((int)0x887A0002);
    private const uint FormatB8G8R8A8 = 87, UsageDefault = 0, UsageStaging = 3, CpuAccessRead = 0x20000, MapRead = 1;

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private struct OutputDesc { public fixed char DeviceName[32]; public RECT Desktop; public int Attached; public int Rotation; public nint Monitor; }
    public struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }
    private struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    private struct Mapped { public byte* Data; public uint RowPitch, DepthPitch; }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint levelCount,
        uint sdkVersion, out nint device, out int level, out nint context);

    /// <summary>One monitor: its duplication, a CPU-readable staging texture and a copy of the last frame.</summary>
    public sealed class Output
    {
        public required string Name { get; init; }
        public required RECT Bounds { get; init; }
        internal nint Device, Context, Duplication, Staging, Last;
    }

    public List<Output> Outputs { get; } = [];

    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }

    public DesktopDuplication()
    {
        Check(CreateDXGIFactory1(IidFactory1, out var factory), "CreateDXGIFactory1");
        for (uint a = 0; ; a++)
        {
            nint adapter;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, a, &adapter); // EnumAdapters1
            if (hr == NotFound) break;
            Check(hr, "EnumAdapters1");
            for (uint i = 0; ; i++)
            {
                nint output;
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, 7))(adapter, i, &output); // EnumOutputs
                if (hr == NotFound) break;
                Check(hr, "EnumOutputs");
                OutputDesc desc;
                Check(((delegate* unmanaged[Stdcall]<nint, OutputDesc*, int>)Slot(output, 7))(output, &desc), "IDXGIOutput.GetDesc");
                if (desc.Attached != 0) Outputs.Add(Open(adapter, output, desc));
                Marshal.Release(output);
            }
            Marshal.Release(adapter);
        }
        Marshal.Release(factory);
    }

    // A monitor can only be duplicated with a device on the adapter that drives it (hybrid laptops!).
    private static Output Open(nint adapter, nint output, OutputDesc desc)
    {
        Check(D3D11CreateDevice(adapter, 0, 0, 0, 0, 0, 7, out var device, out _, out var context), "D3D11CreateDevice");
        Check(Marshal.QueryInterface(output, IidOutput1, out var output1), "QueryInterface(IDXGIOutput1)");
        nint duplication;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(output1, 22))(output1, device, &duplication), "DuplicateOutput");
        Marshal.Release(output1);
        return new Output
        {
            Name = new string(desc.DeviceName).TrimStart('\\', '.'),
            Bounds = desc.Desktop,
            Device = device,
            Context = context,
            Duplication = duplication,
            Staging = CreateTexture(device, desc.Desktop, UsageStaging, CpuAccessRead),
            Last = CreateTexture(device, desc.Desktop, UsageDefault, 0)
        };
    }

    private static nint CreateTexture(nint device, RECT bounds, uint usage, uint cpuAccess)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)bounds.Width, Height = (uint)bounds.Height, MipLevels = 1, ArraySize = 1,
            Format = FormatB8G8R8A8, SampleCount = 1, Usage = usage, CpuAccessFlags = cpuAccess
        };
        nint texture;
        Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Slot(device, 5))(device, &desc, 0, &texture), "CreateTexture2D");
        return texture;
    }

    /// <summary>
    /// Takes the next composed frame of the monitor, if there is one, keeps a copy as <see cref="Output.Last"/>
    /// and passes the frame texture to <paramref name="onFrame"/>. Mouse-only updates are skipped.
    /// </summary>
    public bool Poll(Output o, uint timeoutMs, Action<nint, FrameInfo>? onFrame)
    {
        FrameInfo info;
        nint resource;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Slot(o.Duplication, 8))(o.Duplication, timeoutMs, &info, &resource); // AcquireNextFrame
        if (hr == WaitTimeout) return false;
        Check(hr, "AcquireNextFrame");
        try
        {
            if (info.LastPresentTime == 0) return false;
            Check(Marshal.QueryInterface(resource, IidTexture2D, out var texture), "QueryInterface(ID3D11Texture2D)");
            try
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(o.Context, 47))(o.Context, o.Last, texture); // CopyResource
                onFrame?.Invoke(texture, info);
            }
            finally { Marshal.Release(texture); }
            return true;
        }
        finally
        {
            Marshal.Release(resource);
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(o.Duplication, 14))(o.Duplication); // ReleaseFrame
        }
    }

    /// <summary>Consumes pending frames so that <see cref="Output.Last"/> shows the current desktop.</summary>
    public void Settle()
    {
        foreach (var o in Outputs)
            for (var n = 0; n < 10 && Poll(o, n == 0 ? 100u : 20u, null); n++) { }
    }

    /// <summary>
    /// Measures the client area and the title bar of <paramref name="frame"/> in a frame texture, optionally
    /// keeping an RGB copy of the whole window scaled down by <paramref name="imageStep"/>.
    /// </summary>
    public Measurement Measure(Output o, nint source, RECT frame, RECT client, int? imageStep)
    {
        var region = RECT.Intersect(frame, o.Bounds);
        if (region.Width <= 0 || region.Height <= 0) return new Measurement();
        var box = new Box
        {
            Left = (uint)(region.Left - o.Bounds.Left), Top = (uint)(region.Top - o.Bounds.Top), Front = 0,
            Right = (uint)(region.Right - o.Bounds.Left), Bottom = (uint)(region.Bottom - o.Bounds.Top), Back = 1
        };
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Slot(o.Context, 46))(
            o.Context, o.Staging, 0, 0, 0, 0, source, 0, &box); // CopySubresourceRegion
        Mapped mapped;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Slot(o.Context, 14))(o.Context, o.Staging, 0, MapRead, 0, &mapped), "Map");
        try
        {
            var titleBar = new RECT { Left = region.Left, Top = region.Top, Right = region.Right, Bottom = Math.Clamp(client.Top, region.Top, region.Bottom) };
            byte[]? rgb = null;
            int width = 0, height = 0;
            if (imageStep is { } step)
            {
                width = region.Width / step;
                height = region.Height / step;
                rgb = new byte[width * height * 3];
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var p = mapped.Data + (long)y * step * mapped.RowPitch + x * step * 4;
                        var d = (y * width + x) * 3;
                        rgb[d] = p[2]; rgb[d + 1] = p[1]; rgb[d + 2] = p[0];
                    }
            }
            return new Measurement
            {
                Client = Analyze(mapped, region, RECT.Intersect(client, region)),
                Title = Analyze(mapped, region, titleBar),
                Rgb = rgb,
                ImageWidth = width,
                ImageHeight = height
            };
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(o.Context, 15))(o.Context, o.Staging, 0); // Unmap
        }
    }

    // Every second pixel in both directions; "light" is luma >= 160, "dark" is luma < 64.
    private static Stats Analyze(Mapped mapped, RECT origin, RECT area)
    {
        if (area.Width <= 0 || area.Height <= 0) return default;
        long count = 0, light = 0, dark = 0;
        double sum = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
        {
            var row = mapped.Data + (long)(y - origin.Top) * mapped.RowPitch;
            for (var x = area.Left; x < area.Right; x += 2)
            {
                var p = row + (x - origin.Left) * 4;
                var luma = 0.0722 * p[0] + 0.7152 * p[1] + 0.2126 * p[2];
                sum += luma;
                count++;
                if (luma >= 160) light++;
                else if (luma < 64) dark++;
            }
        }
        return new Stats(sum / count, 100.0 * light / count, 100.0 * dark / count);
    }

    /// <summary>Saves the last composed frame of a monitor, scaled down 4x.</summary>
    public void SaveLast(Output o, string path)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(o.Context, 47))(o.Context, o.Staging, o.Last);
        Mapped mapped;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Slot(o.Context, 14))(o.Context, o.Staging, 0, MapRead, 0, &mapped), "Map");
        try
        {
            int width = o.Bounds.Width / 4, height = o.Bounds.Height / 4;
            var rgb = new byte[width * height * 3];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var p = mapped.Data + (long)y * 4 * mapped.RowPitch + x * 16;
                    var d = (y * width + x) * 3;
                    rgb[d] = p[2]; rgb[d + 1] = p[1]; rgb[d + 2] = p[0];
                }
            Png.Save(path, width, height, rgb);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(o.Context, 15))(o.Context, o.Staging, 0);
        }
    }
}
