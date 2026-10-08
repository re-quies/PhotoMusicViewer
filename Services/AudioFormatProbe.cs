using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    internal enum AudioContainer { Unknown, Ogg, Mp4, Wave, Flac, MpegAudio, Adts }
    internal enum AudioCodec { Unknown, Opus, Vorbis, Aac, Alac, Pcm, Flac, MpegAudio }
    internal readonly record struct AudioFormat(AudioContainer Container, AudioCodec Codec);

    /// <summary>Выбор декодера по содержимому, а не расширению. Только чтение.</summary>
    internal static class AudioFormatProbe
    {
        internal static AudioFormat Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[12];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Position = 0;
            if (read >= 4 && head[..4].SequenceEqual("OggS"u8))
            {
                var packets = new OggPacketStream(stream);
                byte[] packet = packets.ReadPacket() ?? throw new InvalidDataException("Missing Ogg header.");
                if (packet.AsSpan().StartsWith("OpusHead"u8)) return new(AudioContainer.Ogg, AudioCodec.Opus);
                if (packet.Length >= 7 && packet[0] == 1 && packet.AsSpan(1, 6).SequenceEqual("vorbis"u8))
                    return new(AudioContainer.Ogg, AudioCodec.Vorbis);
                throw new NotSupportedException(Loc.T("Unsupported Ogg audio codec.", "Неподдерживаемый аудиокодек Ogg.", "Códec de audio Ogg no compatible."));
            }
            if (read >= 8 && (head.Slice(4, 4).SequenceEqual("ftyp"u8) ||
                head.Slice(4, 4).SequenceEqual("free"u8) || head.Slice(4, 4).SequenceEqual("skip"u8) ||
                head.Slice(4, 4).SequenceEqual("wide"u8)))
                return new(AudioContainer.Mp4, Mp4OpusIndex.ProbeCodec(stream));
            if (read >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WAVE"u8))
                return new(AudioContainer.Wave, AudioCodec.Unknown); // WAVE can contain non-PCM data.
            if (read >= 4 && head[..4].SequenceEqual("fLaC"u8)) return new(AudioContainer.Flac, AudioCodec.Flac);
            if (read >= 2 && head[0] == 0xff && (head[1] & 0xf6) == 0xf0)
                return new(AudioContainer.Adts, AudioCodec.Aac);
            if ((read >= 3 && head[..3].SequenceEqual("ID3"u8)) ||
                (read >= 2 && head[0] == 0xff && (head[1] & 0xe0) == 0xe0))
                return new(AudioContainer.MpegAudio, AudioCodec.Unknown); // ID3 is metadata, not proof of the codec.
            return new(AudioContainer.Unknown, AudioCodec.Unknown);
        }
    }
}
