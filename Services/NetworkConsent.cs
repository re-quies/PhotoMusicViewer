using System;
using System.Collections.Generic;

namespace PhotoMusicViewer.Services
{
    public enum NetworkDataKind { Text, Image, ServiceProbe }
    /// <summary>Разрешение относится к типу данных + origin (scheme/host/port), не всему сеансу сразу.</summary>
    public static class NetworkConsent
    {
        private static readonly object Gate = new();
        private static readonly HashSet<(NetworkDataKind Kind, string Origin)> Grants = new();
        public static bool IsGranted(NetworkDataKind kind, string endpoint)
        {
            string origin = NetworkEndpointPolicy.Origin(endpoint);
            lock (Gate) return Grants.Contains((kind, origin));
        }
        public static bool IsAllowed(NetworkDataKind kind, string endpoint) =>
            (!TranslationConfig.Current.AskBeforeNetwork &&
             NetworkEndpointPolicy.Origin(endpoint).StartsWith("https://", StringComparison.Ordinal)) || IsGranted(kind, endpoint);
        public static void Grant(NetworkDataKind kind, string endpoint)
        {
            string origin = NetworkEndpointPolicy.Origin(endpoint);
            lock (Gate) Grants.Add((kind, origin));
        }
        public static void RevokeAll() { lock (Gate) Grants.Clear(); }
        public static void Require(NetworkDataKind kind, string endpoint)
        {
            if (IsAllowed(kind, endpoint)) return;
            throw new TranslationException(Loc.T("Sending this data to this destination is not allowed: ",
                "Отправка этого типа данных этому получателю не разрешена: ",
                "No se permite enviar estos datos a este destino: ") + NetworkEndpointPolicy.Origin(endpoint));
        }
    }
}
