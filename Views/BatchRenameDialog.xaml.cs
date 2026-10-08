using System.Windows;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    public partial class BatchRenameDialog : Window
    {
        public long StartNumber { get; private set; } = 1;
        public int ZeroPadding { get; private set; } = 3;
        public bool Recursive { get; private set; }

        public BatchRenameDialog()
        {
            InitializeComponent();

            Title = Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo");
            StartNumberLabel.Text = Loc.T("Start number", "Начальный номер", "Número inicial");
            PaddingLabel.Text = Loc.T("Digits in number (zero padding)", "Цифр в номере (ведущие нули)", "Dígitos en el número (relleno con ceros)");
            RecursiveCheck.Content = Loc.T("Include subfolders", "Включая подпапки", "Incluir subcarpetas");
            InfoText.Text = Loc.T(
                "Only photo and GIF files are renamed; files whose names are already numbers are skipped. Files with the same name (RAW + JPG, .xmp, .aae, Live Photo .MOV, backups) get the same number. Existing files are never overwritten.",
                "Переименовываются только фото и GIF; файлы, чьи имена уже числа, пропускаются. Файлы с тем же именем (RAW + JPG, .xmp, .aae, .MOV от Live Photo, резервные копии) получают тот же номер. Существующие файлы никогда не перезаписываются.",
                "Solo se renombran fotos y GIF; se omiten los que ya son números. Los archivos con el mismo nombre (RAW + JPG, .xmp, .aae, .MOV de Live Photo, copias) reciben el mismo número. Nunca se sobrescribe nada.");
            RecursiveHint.Text = Loc.T(
                "Not available for the root of a drive and system folders. Hidden and system subfolders and folder links (junctions) are skipped.",
                "Недоступно для корня диска и системных папок. Скрытые и системные подпапки и ссылки на папки (junction) пропускаются.",
                "No disponible en la raíz de una unidad ni en carpetas del sistema. Se omiten subcarpetas ocultas, del sistema y enlaces.");
            OkButton.Content = Loc.T("OK", "ОК", "OK");
            CancelButton.Content = Loc.T("Cancel", "Отмена", "Cancelar");

            Loaded += (_, _) =>
            {
                StartNumberBox.Focus();
                StartNumberBox.SelectAll();
            };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(StartNumberBox.Text.Trim(), out var start) || start < 0)
            {
                MessageBox.Show(
                    Loc.T("Start number must be a non-negative integer.", "Начальный номер должен быть целым неотрицательным числом.", "El número inicial debe ser un entero no negativo."),
                    Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StartNumber = start;
            ZeroPadding = PaddingCombo.SelectedIndex + 1;
            Recursive = RecursiveCheck.IsChecked == true;
            DialogResult = true;
        }
    }
}
