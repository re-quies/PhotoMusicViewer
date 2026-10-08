using System;

namespace PhotoMusicViewer.Services
{
    internal enum OpenButtonAction { None, Image, Folder }

    /// <summary>UI-thread click sequence. A confirmed second click consumes the
    /// pending single action; expiry/cancel cannot produce another dialog.</summary>
    internal sealed class OpenButtonGesture
    {
        internal bool SinglePending { get; private set; }
        private bool _pressed, _doublePressed;
        internal void MouseDown(int clickCount)
        {
            _pressed = true;
            _doublePressed = clickCount == 2 && SinglePending;
            if (_doublePressed) SinglePending = false;
        }
        internal OpenButtonAction MouseClick()
        {
            if (!_pressed) return OpenButtonAction.None;
            _pressed = false;
            if (_doublePressed) { Cancel(); return OpenButtonAction.Folder; }
            SinglePending = true;
            return OpenButtonAction.None;
        }
        internal OpenButtonAction SingleExpired()
        {
            if (!SinglePending || _pressed) return OpenButtonAction.None;
            Cancel(); return OpenButtonAction.Image;
        }
        internal OpenButtonAction KeyboardClick() { Cancel(); return OpenButtonAction.Image; }
        internal void Cancel() { SinglePending = false; _pressed = false; _doublePressed = false; }
        internal static int DelayMilliseconds(uint systemDoubleClickTime) => 150;
    }
}
