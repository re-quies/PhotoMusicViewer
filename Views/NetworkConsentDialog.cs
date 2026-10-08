using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    internal static class NetworkConsentDialog
    {
        public static bool Ensure(Window owner, NetworkDataKind kind, IEnumerable<string> endpoints)
        {
            var origins = endpoints.Select(NetworkEndpointPolicy.Origin).Distinct(StringComparer.Ordinal).ToArray();
            var pending = origins.Where(o => !NetworkConsent.IsAllowed(kind, o)).ToArray();
            if (pending.Length == 0) return true;
            string question = kind switch
            {
                NetworkDataKind.Image => Loc.T(
                    "Everything visible in this photo will be sent to the destination below. Metadata may also be sent if stripping is disabled.",
                    "Всё видимое на фотографии будет отправлено указанному ниже получателю. Если удаление метаданных отключено, они также могут быть отправлены.",
                    "Todo lo visible en la foto se enviará al destino indicado. También pueden enviarse metadatos si no se eliminan."),
                NetworkDataKind.ServiceProbe => Loc.T(
                    "Key checks/model lists contact these destinations. Checks can include a test 'ping' and consume quota. Your own text/photos are not sent. Alternative Qwen regions are tried only if needed.",
                    "Проверка ключей/список моделей обращаются к этим получателям. Проверка может отправлять тестовый «ping» и расходовать квоту. Ваш текст и фотографии не отправляются. Другие регионы Qwen проверяются только при необходимости.",
                    "La comprobación/lista contacta con estos destinos. Puede enviar 'ping' y consumir cuota, no su texto ni fotos. Otros destinos Qwen se prueban solo si es necesario."),
                _ => Loc.T("The text will be sent to the destination below.",
                    "Текст будет отправлен указанному ниже получателю.", "El texto se enviará al destino indicado.")
            };
            question += "\n\n" + string.Join("\n", pending);
            if (pending.Any(o => o.StartsWith("http://", StringComparison.Ordinal)))
                question += "\n\n" + Loc.T("Warning: local HTTP is unencrypted. The local service receives the API key and data.",
                    "Внимание: локальный HTTP не шифруется. Локальный сервис получает API-ключ и данные.",
                    "Advertencia: HTTP local no está cifrado. El servicio recibe la clave y los datos.");
            question += "\n\n" + Loc.T("Allow this data type for these destinations until the app closes?",
                "Разрешить этот тип данных для этих получателей до закрытия приложения?",
                "¿Permitir este tipo de datos para estos destinos hasta cerrar la aplicación?");
            if (MessageBox.Show(owner, question, owner.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            foreach (string origin in pending) NetworkConsent.Grant(kind, origin);
            return true;
        }
        public static string Declined => Loc.T("Nothing was sent.", "Ничего не отправлено.", "No se envió nada.");
    }
}
