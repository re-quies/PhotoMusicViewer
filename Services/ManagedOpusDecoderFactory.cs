using Concentus;

namespace PhotoMusicViewer.Services
{
    internal static class ManagedOpusDecoderFactory
    {
        internal static IOpusDecoder CreateStereo48k()
        {
            // Preserve the previous managed-only backend. Do not probe or load
            // a system opus.dll; publishing/installer dependencies stay unchanged.
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            return OpusCodecFactory.CreateDecoder(48000, 2);
        }
    }
}
