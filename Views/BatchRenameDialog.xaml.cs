using System.Windows;
using System.IO;
using Microsoft.Win32;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    public partial class BatchRenameDialog : Window
    {
        public long StartNumber { get; private set; } = 1;
        public int ZeroPadding { get; private set; } = 3;
        public bool Recursive { get; private set; }
        public bool RenameNumericNames { get; private set; }
        public RenameMediaCategories MediaCategories { get; private set; } = RenameMediaCategories.Photos;
        public bool IncludeCompanions { get; private set; } = true;
        public string FolderPath { get; private set; } = "";
        public RenameNameScheme NameScheme { get; private set; } = RenameNameScheme.FromOption(2);

        public BatchRenameDialog() : this("") { }
        public BatchRenameDialog(string folder)
        {
            InitializeComponent();
            MaxHeight = System.Math.Max(300, SystemParameters.WorkArea.Height - 80);

            Title = Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo");
            FolderLabel.Text = Loc.T("Folder", "Папка", "Carpeta");
            BrowseFolderButton.Content = Loc.T("Browse…", "Выбрать…", "Elegir…");
            FolderPath = folder ?? "";
            FolderPathBox.Text = FolderPath;
            FolderPathBox.ToolTip = FolderPath;
            MediaTypesLabel.Text = Loc.T("File types", "Типы файлов", "Tipos de archivo");
            PhotosCheck.Content = Loc.T("Photos / RAW", "Фото / RAW", "Fotos / RAW");
            VideosCheck.Content = Loc.T("Videos", "Видео", "Vídeos");
            MusicCheck.Content = Loc.T("Music / audio", "Музыка / аудио", "Música / audio");
            CompanionsCheck.Content = Loc.T("Include sidecars and backups", "Связанные файлы и резервные копии", "Incluir auxiliares y copias");
            MediaTypesHint.Text = Loc.T(
                "Select one or more types. Unchecked media types are left unchanged, even as companions. Renaming support does not imply playback support.",
                "Выберите один или несколько типов. Неотмеченные медиа не меняются, даже как спутники. Возможность переименования не означает поддержку воспроизведения.",
                "Seleccione uno o más tipos. Los no marcados no se cambian, ni como asociados. Renombrar no implica poder reproducirlos.");
            RenameModeLabel.Text = Loc.T("Rename mode", "Режим переименования", "Modo de renombrado");
            NonNumericModeItem.Content = Loc.T("Only non-numeric names", "Только нечисловые имена", "Solo nombres no numéricos");
            AllNamesModeItem.Content = Loc.T("All selected files, including numeric names", "Все выбранные, включая числовые имена", "Todos los seleccionados, incluidos nombres numéricos");
            StartNumberLabel.Text = Loc.T("Start number", "Начальный номер", "Número inicial");
            PaddingLabel.Text = Loc.T("Name scheme (12 options)", "Схема имени (12 вариантов)", "Esquema de nombres (12 opciones)");
            RecursiveCheck.Content = Loc.T("Include subfolders", "Включая подпапки", "Incluir subcarpetas");
            InfoText.Text = Loc.T(
                "The default mode skips numeric names; All selected includes them. Existing numbers stay reserved (1 and 001 occupy the same number), so numbering can have gaps. Selected media with the same base name get one number. If enabled, their sidecars and backups follow them. Nothing is overwritten.",
                "По умолчанию числовые имена пропускаются; режим «Все выбранные» включает их. Занятые номера остаются в резерве (1 и 001 занимают один номер), поэтому возможны пропуски. Выбранные медиа с одной основой имени получают один номер. Если включено, их спутники и копии переименовываются вместе. Ничего не перезаписывается.",
                "Por defecto se omiten nombres numéricos; Todos los seleccionados los incluye. Los números ocupados siguen reservados (1 y 001 ocupan el mismo), por lo que puede haber saltos. Los medios seleccionados con la misma base reciben un número; auxiliares y copias los siguen si se activa. No se sobrescribe nada.");
            RecursiveHint.Text = Loc.T(
                "Not available for the root of a drive and system folders. Hidden and system subfolders and folder links (junctions) are skipped.",
                "Недоступно для корня диска и системных папок. Скрытые и системные подпапки и ссылки на папки (junction) пропускаются.",
                "No disponible en la raíz de una unidad ni en carpetas del sistema. Se omiten subcarpetas ocultas, del sistema y enlaces.");
            OkButton.Content = Loc.T("OK", "ОК", "OK");
            CancelButton.Content = Loc.T("Cancel", "Отмена", "Cancelar");

            UpdateNameSchemeHint();
            Loaded += (_, _) =>
            {
                StartNumberBox.Focus();
                StartNumberBox.SelectAll();
            };
        }

        private void PaddingCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateNameSchemeHint();
        private void UpdateNameSchemeHint()
        {
            if (NameSequenceHint == null || StartNumberLabel == null || PaddingCombo.SelectedIndex < 0) return;
            var scheme = RenameNameScheme.FromOption(PaddingCombo.SelectedIndex);
            StartNumberLabel.Text = scheme.Style == RenameNameStyle.Numeric
                ? Loc.T("Start number (0 or more)", "Начальный номер (от 0)", "Número inicial (desde 0)")
                : Loc.T("Start position (1 is the first name)", "Начальная позиция (1 — первое имя)", "Posición inicial (1 es el primer nombre)");
            NameSequenceHint.Text = Loc.T("Sequence: ", "Последовательность: ", "Secuencia: ") +
                scheme.Format(1) + ", " + scheme.Format(2) + ", " + scheme.Format(3) + " … " +
                Loc.T("Occupied names are skipped; no overwriting.", "Занятые имена пропускаются, без перезаписи.", "Se saltan nombres ocupados, sin sobrescribir.");
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFolderDialog
            {
                Title = Loc.T("Choose a media folder", "Выберите папку с медиафайлами", "Elija una carpeta multimedia"),
                Multiselect = false
            };
            if (Directory.Exists(FolderPath)) picker.InitialDirectory = FolderPath;
            if (picker.ShowDialog(this) != true) return;
            FolderPath = picker.FolderName;
            FolderPathBox.Text = FolderPath;
            FolderPathBox.ToolTip = FolderPath;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var categories = RenameMediaCategories.None;
            if (PhotosCheck.IsChecked == true) categories |= RenameMediaCategories.Photos;
            if (VideosCheck.IsChecked == true) categories |= RenameMediaCategories.Videos;
            if (MusicCheck.IsChecked == true) categories |= RenameMediaCategories.Music;
            if (categories == RenameMediaCategories.None)
            {
                MessageBox.Show(this, Loc.T("Select at least one file type.", "Выберите хотя бы один тип файлов.", "Seleccione al menos un tipo de archivo."),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
            {
                MessageBox.Show(this, Loc.T("Choose an existing accessible folder.", "Выберите существующую доступную папку.", "Elija una carpeta existente y accesible."),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (PaddingCombo.SelectedIndex < 0)
            {
                MessageBox.Show(this, Loc.T("Select a name scheme.", "Выберите схему имени.", "Seleccione un esquema de nombres."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var scheme = RenameNameScheme.FromOption(PaddingCombo.SelectedIndex);
            if (!long.TryParse(StartNumberBox.Text.Trim(), out var start) || start < 0 || (scheme.Style != RenameNameStyle.Numeric && start == 0))
            {
                MessageBox.Show(
                    scheme.Style == RenameNameStyle.Numeric
                        ? Loc.T("Start number must be a non-negative integer.", "Начальный номер должен быть целым неотрицательным числом.", "El número inicial debe ser un entero no negativo.")
                        : Loc.T("Letters/mixed names require a position of 1 or more.", "Для буквенной и смешанной схемы нужна позиция от 1.", "Letras/nombres mixtos requieren una posición desde 1."),
                    Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MediaCategories = categories;
            IncludeCompanions = CompanionsCheck.IsChecked == true;
            StartNumber = start;
            NameScheme = scheme;
            ZeroPadding = scheme.Width;
            Recursive = RecursiveCheck.IsChecked == true;
            RenameNumericNames = RenameModeCombo.SelectedIndex == 1;
            DialogResult = true;
        }
    }
}
