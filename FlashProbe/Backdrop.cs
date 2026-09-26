namespace FlashProbe;

/// <summary>
/// A plain grey window over the middle of the primary monitor, where CenterScreen windows open. The target
/// window opens on top of it, so the measurements do not depend on the desktop behind the window and the
/// saved frames do not show it.
/// </summary>
internal sealed class Backdrop : IDisposable
{
    public static readonly Color Colour = Color.FromArgb(0x40, 0x40, 0x40);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new();
    private Form? _form;

    private Backdrop(Rectangle bounds)
    {
        _thread = new Thread(() =>
        {
            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = bounds,
                BackColor = Colour,
                ShowInTaskbar = false,
                Text = "FlashProbe backdrop"
            };
            _form.Shown += (_, _) => _shown.Set();
            Application.Run(_form);
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _shown.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Covers the central 60% of the primary monitor.</summary>
    public static Backdrop Show()
    {
        var screen = Screen.PrimaryScreen!.Bounds;
        int width = screen.Width * 3 / 5, height = screen.Height * 3 / 5;
        return new Backdrop(new Rectangle(screen.X + (screen.Width - width) / 2, screen.Y + (screen.Height - height) / 2, width, height));
    }

    public void Dispose()
    {
        try { _form?.Invoke(_form.Close); }
        catch (InvalidOperationException) { }
        _thread.Join(TimeSpan.FromSeconds(2));
        _shown.Dispose();
    }
}
