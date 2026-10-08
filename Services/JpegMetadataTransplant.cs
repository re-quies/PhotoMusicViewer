using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Перенос метаданных исходного JPEG в перекодированный (обрезка «точно по рамке»).
    /// WPF-кодировщик пишет голый JPEG: без даты съёмки, GPS, камеры и цветового профиля.
    /// Здесь эти сегменты копируются из оригинала байт в байт и правятся под новый снимок:
    ///
    /// - EXIF (APP1): ориентация → 1 (поворот уже «запечён» в пиксели), новые размеры,
    ///   устаревшая миниатюра отключается. Битый EXIF не переносится (а не ломает файл).
    /// - XMP (APP1): сохраняется вместе с рейтингами и ключевыми словами; tiff:Orientation → 1.
    /// - ICC-профиль (APP2 ICC_PROFILE): только если число цветовых каналов не изменилось —
    ///   профиль CMYK на RGB-данных исказил бы цвета.
    /// - IPTC (APP13 Photoshop 3.0) и комментарий (COM).
    ///
    /// Не переносятся: MPF/Ultra HDR, служебные APPn производителей, Adobe APP14 (описывает
    /// кодирование, а не снимок) и данные после конца картинки — они относятся к исходным
    /// байтам и после перекодирования стали бы неверными.
    /// </summary>
    internal static class JpegMetadataTransplant
    {
        private static readonly byte[] XmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        private static readonly byte[] ExtendedXmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
        private static readonly byte[] IccHeader = Encoding.ASCII.GetBytes("ICC_PROFILE\0");
        private static readonly byte[] PhotoshopHeader = Encoding.ASCII.GetBytes("Photoshop 3.0\0");

        internal sealed record Result(bool Exif, bool Xmp, bool Icc, bool Iptc, bool DroppedBrokenExif);

        private static bool StartsWith(byte[] data, byte[] prefix) =>
            data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

        /// <summary>Читает сегменты до SOS. Возвращает их и число компонентов кадра.</summary>
        private static (List<(int Marker, byte[] Data)> Segments, int Components) ReadHeaderSegments(Stream source, CancellationToken token)
        {
            if (source.ReadByte() != 0xFF || source.ReadByte() != 0xD8) throw new InvalidDataException("Not a JPEG.");
            var list = new List<(int, byte[])>(); int components = 0;
            for (int i = 0; i < 4096; i++)
            {
                token.ThrowIfCancellationRequested();
                var (marker, data) = JpegLossless.ReadMarkerSegment(source);
                if (marker is 0xDA or 0xD9) return (list, components);
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && data.Length >= 6) components = data[5];
                list.Add((marker, data));
            }
            throw new InvalidDataException("Too many JPEG markers.");
        }

        /// <summary>Сегменты исходника, которые переносятся в новый файл (уже исправленные).</summary>
        internal static (List<(int Marker, byte[] Data)> Segments, Result Info, int Components) Collect(Stream original, int width, int height, CancellationToken token)
        {
            var (segments, components) = ReadHeaderSegments(original, token);
            var keep = new List<(int, byte[])>();
            bool exif = false, xmp = false, icc = false, iptc = false, broken = false;
            foreach (var (marker, data) in segments)
            {
                if (marker == 0xE1 && JpegLossless.IsExifPayload(data))
                {
                    if (exif) continue; // второй EXIF неоднозначен — переносим только первый
                    var copy = (byte[])data.Clone();
                    try { JpegLossless.PatchExif(copy, width, height, normalizeOrientation: true); }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
                    {
                        AppLog.Warn("JpegMetadataTransplant.Exif", ex);
                        broken = true; continue;
                    }
                    keep.Add((marker, copy)); exif = true;
                }
                else if (marker == 0xE1 && StartsWith(data, XmpHeader))
                {
                    keep.Add((marker, PatchXmpOrientation(data))); xmp = true;
                }
                else if (marker == 0xE1 && StartsWith(data, ExtendedXmpHeader)) keep.Add((marker, data));
                else if (marker == 0xE2 && StartsWith(data, IccHeader)) { keep.Add((marker, data)); icc = true; }
                else if (marker == 0xED && StartsWith(data, PhotoshopHeader)) { keep.Add((marker, data)); iptc = true; }
                else if (marker == 0xFE) keep.Add((marker, data));
            }
            return (keep, new Result(exif, xmp, icc, iptc, broken), components);
        }

        /// <summary>
        /// XMP — текст. Значение ориентации 1–8 — одна цифра, поэтому замена на «1» не меняет
        /// длину сегмента и не может его сломать.
        /// </summary>
        internal static byte[] PatchXmpOrientation(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data, XmpHeader.Length, data.Length - XmpHeader.Length);
            string patched = Regex.Replace(text, @"(tiff:Orientation\s*=\s*[""'])[1-8]([""'])", "${1}1${2}");
            patched = Regex.Replace(patched, @"(<tiff:Orientation>\s*)[1-8](\s*</tiff:Orientation>)", "${1}1${2}");
            if (patched == text) return data;
            byte[] body = Encoding.UTF8.GetBytes(patched);
            if (body.Length != data.Length - XmpHeader.Length) return data; // не должно случиться; не рискуем
            var result = new byte[data.Length];
            XmpHeader.CopyTo(result, 0); body.CopyTo(result, XmpHeader.Length);
            return result;
        }

        /// <summary>
        /// Пишет в output перекодированный JPEG (encoded) с метаданными исходника (original).
        /// Свои APP1/APP2-ICC кодировщика заменяются исходными; остальное (таблицы, кадр,
        /// данные сканирования, APP14) остаётся от кодировщика.
        /// </summary>
        internal static Result Merge(Stream original, Stream encoded, Stream output, int width, int height, CancellationToken token = default)
        {
            var (keep, info, sourceComponents) = Collect(original, width, height, token);
            if (encoded.ReadByte() != 0xFF || encoded.ReadByte() != 0xD8) throw new InvalidDataException("Encoder output is not a JPEG.");

            var head = new List<(int Marker, byte[] Data)>();
            int encodedComponents = 0;
            for (int i = 0; ; i++)
            {
                if (i >= 4096) throw new InvalidDataException("Too many JPEG markers in encoder output.");
                token.ThrowIfCancellationRequested();
                var (marker, data) = JpegLossless.ReadMarkerSegment(encoded);
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && data.Length >= 6) encodedComponents = data[5];
                head.Add((marker, data));
                if (marker == 0xDA) break;
                if (marker == 0xD9) throw new InvalidDataException("Encoder output has no image data.");
            }
            bool iccAllowed = info.Icc && sourceComponents == encodedComponents;
            if (!iccAllowed && info.Icc) keep.RemoveAll(s => s.Marker == 0xE2);
            info = info with { Icc = iccAllowed };

            output.WriteByte(0xFF); output.WriteByte(0xD8);
            int insertAt = head.Count > 0 && head[0].Marker == 0xE0 ? 1 : 0; // после JFIF APP0
            bool inserted = false;
            for (int i = 0; i < head.Count; i++)
            {
                if (i == insertAt && !inserted) { foreach (var s in keep) WriteSegment(output, s.Marker, s.Data); inserted = true; }
                var (marker, data) = head[i];
                bool replaced = marker == 0xE1 || (marker == 0xE2 && StartsWith(data, IccHeader)) || (marker == 0xED && info.Iptc) || marker == 0xFE;
                if (replaced) continue;
                WriteSegment(output, marker, data);
            }
            // Данные сканирования и всё до EOI — как есть.
            byte[] buffer = new byte[65536]; int n; long total = 0;
            while ((n = encoded.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                total = checked(total + n);
                if (total > ImageSafetyPolicy.MaxFileBytes) throw new IOException("JPEG output exceeds safe file limit.");
                output.Write(buffer, 0, n);
            }
            return info;
        }

        private static void WriteSegment(Stream output, int marker, byte[] data)
        {
            output.WriteByte(0xFF); output.WriteByte((byte)marker);
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7 || marker == 1) return;
            int length = checked(data.Length + 2);
            if (length > 0xFFFF) throw new InvalidDataException("JPEG segment too large.");
            output.WriteByte((byte)(length >> 8)); output.WriteByte((byte)length); output.Write(data);
        }
    }
}
