using System;
namespace PhotoMusicViewer.Services
{
    public sealed class TranslationException : Exception
    {
        public TranslationException(string message) : base(message) { }
    }
}
