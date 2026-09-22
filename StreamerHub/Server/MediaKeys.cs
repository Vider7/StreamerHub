using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StreamerHub;

// Global keyboard media keys (play/pause, next, previous). mpv is told not to
// grab them itself so every press flows through the queue logic instead.
sealed class MediaKeys : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint VK_MEDIA_PLAY_PAUSE = 0xB3;
    const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    const uint VK_MEDIA_PREV_TRACK = 0xB1;

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    sealed class HotkeyWindow : NativeWindow
    {
        readonly Action<int> _pressed;

        public HotkeyWindow(Action<int> pressed)
        {
            _pressed = pressed;
            CreateHandle(new CreateParams());
            Register(1, VK_MEDIA_PLAY_PAUSE, "play/pause");
            Register(2, VK_MEDIA_NEXT_TRACK, "next");
            Register(3, VK_MEDIA_PREV_TRACK, "previous");
        }

        void Register(int id, uint vk, string name)
        {
            if (!RegisterHotKey(Handle, id, 0, vk))
                Log.Warn("media keys: '" + name + "' is already used by another app, skipping it");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    readonly HotkeyWindow _window;

    public MediaKeys(Action onPlayPause, Action onNext, Action onPrev)
    {
        _window = new HotkeyWindow(id =>
        {
            try
            {
                if (id == 1) onPlayPause();
                else if (id == 2) onNext();
                else if (id == 3) onPrev();
            }
            catch (Exception ex)
            {
                Log.Warn("media keys: press failed: " + ex.Message);
            }
        });
    }

    public void Dispose()
    {
        for (var id = 1; id <= 3; id++)
        {
            try { UnregisterHotKey(_window.Handle, id); } catch { }
        }
        try { _window.DestroyHandle(); } catch { }
    }
}
