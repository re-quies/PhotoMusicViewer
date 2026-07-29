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
                "Only photo and GIF files are renamed. Files whose names are already numbers are skipped. Existing files are never overwritten.",
                "Переименовываются только фото и GIF. Файлы, чьи имена уже являются числами, пропускаются. Существующие файлы никогда не перезаписываются.",
                "Solo se renombran fotos y GIF. Los archivos cuyos nombres ya son números se omiten. Los archivos existentes nunca se sobrescriben.");
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
