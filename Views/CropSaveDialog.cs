using System.Windows;
using System.Windows.Controls;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    /// <summary>
    /// Сохранение обрезки. По умолчанию выбрана копия: Enter в окне сохраняет копию, а не
    /// перезаписывает оригинал (раньше двойной Enter — «применить» и «Да» — стирал оригинал).
    /// Для JPEG по умолчанию — обрезка без потерь.
    /// </summary>
    internal sealed class CropSaveDialog : Window
    {
        private readonly RadioButton _copy = new() { IsChecked = true, GroupName = "target" };
        private readonly RadioButton _replace = new() { GroupName = "target", Margin = new Thickness(0, 6, 0, 0) };
        private readonly RadioButton _lossless = new() { GroupName = "method" };
        private readonly RadioButton _reencode = new() { GroupName = "method", Margin = new Thickness(0, 6, 0, 0) };

        internal bool ReplaceOriginal => _replace.IsChecked == true;
        internal CropMethod Method => _lossless.IsChecked == true ? CropMethod.Lossless : CropMethod.Reencode;

        /// <param name="lossless">План обрезки без потерь; null — недоступна (не JPEG, нет jpegtran, необычный файл).</param>
        /// <param name="losslessUnavailableReason">Почему без потерь нельзя (для JPEG).</param>
        internal CropSaveDialog(Window? owner, string path, int width, int height,
            JpegLossless.LosslessCropPlan? lossless, string? losslessUnavailableReason)
        {
            Owner = owner;
            Title = Loc.T("Save cropped image", "Сохранение обрезки", "Guardar imagen recortada");
            SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var panel = new StackPanel { Margin = new Thickness(20), Width = 520 }; Content = panel;

            bool jpeg = JpegLossless.IsJpeg(path);
            bool inPlace = CropAndSaveService.CanEncodeInPlace(path);
            panel.Children.Add(Text(Loc.T($"Selected area: {width} × {height} px", $"Выбранная область: {width} × {height} px", $"Área seleccionada: {width} × {height} px"), 0, 12));

            if (jpeg)
            {
                panel.Children.Add(Header(Loc.T("How to crop", "Как обрезать", "Cómo recortar")));
                if (lossless is { } plan)
                {
                    var d = plan.Display;
                    _lossless.Content = plan.Exact
                        ? Loc.T($"Without quality loss, exactly as selected ({d.Width} × {d.Height})",
                                $"Без потери качества, точно по рамке ({d.Width} × {d.Height})",
                                $"Sin pérdida de calidad, exacto ({d.Width} × {d.Height})")
                        : Loc.T($"Without quality loss — the frame grows to the JPEG grid: {Describe(plan)} ({d.Width} × {d.Height})",
                                $"Без потери качества — рамка расширится до сетки JPEG: {Describe(plan)} ({d.Width} × {d.Height})",
                                $"Sin pérdida de calidad — el marco crece hasta la cuadrícula JPEG: {Describe(plan)} ({d.Width} × {d.Height})");
                    _lossless.IsChecked = true;
                    panel.Children.Add(Wrap(_lossless));
                    panel.Children.Add(Text(Loc.T("Pixels are not re-compressed. Date, GPS, camera data, color profile and XMP are kept.",
                        "Пиксели не пересжимаются. Дата съёмки, GPS, данные камеры, цветовой профиль и XMP сохраняются.",
                        "Los píxeles no se recomprimen. Se conservan fecha, GPS, cámara, perfil de color y XMP."), 22, 4, hint: true));
                }
                else
                {
                    panel.Children.Add(Text(Loc.T("Lossless crop is unavailable: ", "Обрезка без потерь недоступна: ", "Recorte sin pérdidas no disponible: ") +
                        (losslessUnavailableReason ?? "?"), 0, 6, hint: true));
                    _reencode.IsChecked = true;
                }
                _reencode.Content = Loc.T("Exactly as selected — re-encode JPEG (quality 95)",
                    "Точно по рамке — перекодировать JPEG (качество 95)", "Exacto — recodificar JPEG (calidad 95)");
                panel.Children.Add(Wrap(_reencode));
                panel.Children.Add(Text(Loc.T("Slight quality loss. Date, GPS, camera data, color profile and XMP are copied from the original.",
                    "Небольшая потеря качества. Дата съёмки, GPS, данные камеры, цветовой профиль и XMP переносятся из оригинала.",
                    "Ligera pérdida de calidad. Se copian fecha, GPS, cámara, perfil de color y XMP."), 22, 4, hint: true));
            }
            else if (inPlace)
            {
                _reencode.IsChecked = true;
                panel.Children.Add(Text(Loc.T("The image is re-encoded (PNG, BMP and TIFF without quality loss). Metadata of this format is not kept.",
                    "Изображение перекодируется (PNG, BMP и TIFF — без потери качества). Метаданные этого формата не сохраняются.",
                    "La imagen se recodifica (PNG, BMP y TIFF sin pérdida). No se conservan los metadatos de este formato."), 0, 6, hint: true));
            }
            else
            {
                _reencode.IsChecked = true;
                panel.Children.Add(Text(Loc.T("This format can't be re-encoded in place, so the crop is saved as a PNG copy.",
                    "Этот формат нельзя перекодировать на месте, поэтому обрезка сохраняется PNG-копией.",
                    "Este formato no se puede recodificar, así que el recorte se guarda como copia PNG."), 0, 6, hint: true));
            }

            panel.Children.Add(Header(Loc.T("Where to save", "Куда сохранить", "Dónde guardar")));
            _copy.Content = Loc.T("Save as a new copy (recommended)", "Сохранить новой копией (рекомендуется)", "Guardar como copia nueva (recomendado)");
            panel.Children.Add(Wrap(_copy));
            _replace.Content = Loc.T("Replace the original and keep a backup copy",
                "Заменить оригинал и оставить резервную копию", "Reemplazar el original y conservar una copia de seguridad");
            _replace.IsEnabled = inPlace;
            panel.Children.Add(Wrap(_replace));
            panel.Children.Add(Text(Loc.T("The backup *.pmv-original-….bak stays next to the photo (unencrypted, with its metadata). \"Restore original\" works after restarting too, as long as the file isn't changed by another program; \"Delete backup\" moves it to the Recycle Bin.",
                "Резервная копия *.pmv-original-….bak остаётся рядом с фото (незашифрованной, с метаданными). «Вернуть оригинал» работает и после перезапуска, пока файл не изменила другая программа; «Удалить резервную копию» перемещает её в корзину.",
                "La copia *.pmv-original-….bak queda junto a la foto (sin cifrar, con metadatos). «Restaurar original» funciona también tras reiniciar si otro programa no cambió el archivo; «Eliminar copia» la mueve a la papelera."), 22, 4, hint: true));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(buttons);
            var cancel = new Button { Content = Loc.T("Cancel — continue cropping", "Отмена — продолжить обрезку", "Cancelar — seguir recortando"), IsCancel = true, Padding = new Thickness(12, 6, 12, 6) };
            var save = new Button { Content = Loc.T("Save", "Сохранить", "Guardar"), IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
            save.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(cancel); buttons.Children.Add(save);
            Loaded += (_, _) => save.Focus();
        }

        private static string Describe(JpegLossless.LosslessCropPlan p)
        {
            var parts = new System.Collections.Generic.List<string>();
            void Add(int px, string en, string ru, string es) { if (px > 0) parts.Add(Loc.T($"{en} +{px}", $"{ru} +{px}", $"{es} +{px}")); }
            Add(p.ExtraLeft, "left", "слева", "izquierda");
            Add(p.ExtraTop, "top", "сверху", "arriba");
            Add(p.ExtraRight, "right", "справа", "derecha");
            Add(p.ExtraBottom, "bottom", "снизу", "abajo");
            return string.Join(", ", parts) + " px";
        }

        private static UIElement Wrap(RadioButton radio)
        {
            if (radio.Content is string text) radio.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 490 };
            return radio;
        }

        private static TextBlock Header(string text) =>
            new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 6) };

        private static TextBlock Text(string text, double left, double bottom, bool hint = false) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left, 0, 0, bottom), Opacity = hint ? 0.75 : 1 };
    }
}
