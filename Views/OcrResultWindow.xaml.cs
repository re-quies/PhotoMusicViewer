using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    public partial class OcrResultWindow : Window
    {
        public OcrResultWindow(IReadOnlyList<OcrService.OcrVariant> variants)
        {
            InitializeComponent();

            Title = Loc.T("Scanned text", "Распознанный текст", "Texto reconocido");
            CloseButton.Content = Loc.T("Close", "Закрыть", "Cerrar");

            // Текст в окне: один вариант - как есть, несколько - с заголовками по языкам
            if (variants.Count == 1)
            {
                ResultTextBox.Text = variants[0].Text;
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var v in variants)
                {
                    sb.AppendLine($"====== {v.Language} ======");
                    sb.AppendLine(v.Text);
                    sb.AppendLine();
                }
                ResultTextBox.Text = sb.ToString().Trim();
            }

            // Сколько вариантов - столько и кнопок; каждая копирует только свой текст,
            // без заголовков "====== ... ======"
            foreach (var variant in variants)
            {
                var label = variants.Count == 1
                    ? Loc.T("Copy all", "Копировать всё", "Copiar todo")
                    : Loc.T("Copy", "Копировать", "Copiar") + " " + variant.Code;

                var button = new Button
                {
                    Content = label,
                    MinWidth = 110,
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var text = variant.Text;
                button.Click += (_, _) => CopyVariant(button, label, text);
                CopyButtonsPanel.Children.Add(button);
            }
        }

        private static void CopyVariant(Button button, string originalLabel, string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // буфер обмена может быть временно занят другим приложением
                return;
            }

            button.Content = Loc.T("Copied!", "Скопировано!", "¡Copiado!");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                button.Content = originalLabel;
            };
            timer.Start();
        }
    }
}
