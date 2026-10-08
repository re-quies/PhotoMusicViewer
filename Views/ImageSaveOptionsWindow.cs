using System.Windows;
using System.Windows.Controls;
using PhotoMusicViewer.Services;
namespace PhotoMusicViewer.Views
{
    internal sealed class ImageSaveOptionsWindow : Window
    {
        private readonly RadioButton _copy = new() { IsChecked = true };
        private readonly CheckBox _reencode = new() { IsChecked = false };
        internal bool Copy => _copy.IsChecked == true;
        internal bool Reencode => _reencode.IsChecked == true;
        internal ImageSaveOptionsWindow(Window? owner, string path, bool strip)
        {
            Owner = owner; Title = Loc.T("Save image", "Сохранить изображение", "Guardar imagen");
            SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(20), Width = 510 }; Content = panel;
            bool jpeg = JpegLossless.IsJpeg(path);
            string message = jpeg
                ? Loc.T("Default: perfect lossless JPEG transform. Incompatible dimensions are rejected; no cropping or automatic re-encoding.", "По умолчанию: JPEG без потерь. Несовместимые размеры отклоняются: без подрезки и автоматического перекодирования.", "Predeterminado: JPEG sin pérdidas. Se rechazan dimensiones incompatibles; sin recorte ni recodificación automática.")
                : Loc.T("This format will be re-encoded; metadata is removed. Multi-page TIFF is rejected.", "Этот формат будет перекодирован; метаданные удаляются. Многостраничный TIFF отклоняется.", "Se recodificará este formato y se eliminarán los metadatos. Se rechaza TIFF multipágina.");
            message += strip ? Loc.T("\nMetadata removal includes GPS and color profiles. Color appearance can change.", "\nУдаление метаданных включает GPS и цветовые профили. Цвета могут измениться.", "\nSe eliminan GPS y perfiles de color. Los colores pueden cambiar.") : Loc.T("\nLossless JPEG keeps EXIF/GPS/camera/ICC, normalizes orientation; EXIF thumbnail is disconnected and XMP removed.", "\nJPEG без потерь сохраняет EXIF/GPS/камеру/ICC, исправляет ориентацию; миниатюра EXIF отключается, XMP удаляется.", "\nJPEG sin pérdidas conserva EXIF/GPS/cámara/ICC; normaliza orientación, desconecta miniatura EXIF y elimina XMP.");
            if (!jpeg && !strip) message += Loc.T("\nNo metadata-preservation guarantee for this format.", "\nДля этого формата сохранение метаданных не поддерживается.", "\nNo se conservan metadatos en este formato.");
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            _copy.Content = Loc.T("Save a new copy (recommended)", "Сохранить новую копию (рекомендуется)", "Guardar copia nueva (recomendado)"); _copy.GroupName = "save"; panel.Children.Add(_copy);
            panel.Children.Add(new RadioButton { Content = Loc.T("Replace original and keep recovery backup", "Заменить оригинал и оставить резервную копию", "Reemplazar original y conservar respaldo"), GroupName = "save", Margin = new Thickness(0, 8, 0, 8) });
            _reencode.Content = Loc.T("Use lossy JPEG re-encoding (quality 95); remove ALL metadata", "Использовать JPEG с потерями (качество 95); удалить ВСЕ метаданные", "Usar JPEG con pérdidas (calidad 95); eliminar TODOS los metadatos");
            _reencode.Visibility = jpeg ? Visibility.Visible : Visibility.Collapsed; panel.Children.Add(_reencode);
            panel.Children.Add(new TextBlock { Text = Loc.T("Replacement retains an unencrypted .pmv-original-…bak next to the image, including private metadata. \"Restore original\" works after restarting too, as long as the file isn't changed by another program. Backups are not deleted automatically: \"Delete backup\" (or restoring) moves them to the Recycle Bin.", "При замене рядом остаётся незашифрованный .pmv-original-…bak, включая личные метаданные. «Вернуть оригинал» работает и после перезапуска, пока файл не изменила другая программа. Сами копии не удаляются: «Удалить резервную копию» (или восстановление) перемещает их в корзину.", "El reemplazo conserva .pmv-original-…bak sin cifrar y con metadatos privados. «Restaurar original» funciona también tras reiniciar si otro programa no cambió el archivo. Las copias no se borran solas: «Eliminar copia» (o restaurar) las mueve a la papelera."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
            var cancel = new Button { Content = Loc.T("Cancel", "Отмена", "Cancelar"), IsCancel = true, Padding = new Thickness(12, 6, 12, 6) }; buttons.Children.Add(cancel);
            var save = new Button { Content = Loc.T("Save", "Сохранить", "Guardar"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) }; save.Click += (_, _) => DialogResult = true; buttons.Children.Add(save);
        }
    }
}
