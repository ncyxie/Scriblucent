using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Services;
using PrintDialog = System.Windows.Controls.PrintDialog;

namespace Scriblucent
{
    public partial class PrintPreviewWindow : Window
    {
        private readonly string _title;
        private readonly string _content;

        public PrintPreviewWindow(string title, string content)
        {
            InitializeComponent();
            Scriblucent.Services.WindowThemeService.Register(this);

            _title = title;
            _content = content;

            // Load preview
            PreviewTitle.Text = _title;
            PreviewContent.Text = _content;
            WindowContentEnterHelper.AnimateOnLoaded(this);
        }

        private void PrintButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var printDialog = new PrintDialog();

                if (printDialog.ShowDialog() == true)
                {
                    // Create a document for printing
                    FlowDocument document = new FlowDocument();

                    // Add title
                    Paragraph titleParagraph = new Paragraph(
                        new Run(_title)
                    )
                    {
                        FontSize = TypeScale.Px(TypeScale.Display, 24),
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.Black
                    };
                    document.Blocks.Add(titleParagraph);

                    // Add spacing
                    document.Blocks.Add(new Paragraph(new Run("\n")));

                    // Add content
                    Paragraph contentParagraph = new Paragraph(
                        new Run(_content)
                    )
                    {
                        FontSize = TypeScale.Px(TypeScale.Caption, 12),
                        Foreground = Brushes.Black,
                        FontFamily = new FontFamily("Segoe UI")
                    };
                    document.Blocks.Add(contentParagraph);

                    // Print the document
                    printDialog.PrintDocument(
                        ((IDocumentPaginatorSource)document).DocumentPaginator,
                        _title
                    );

                    ModernMessageBox.Show(
                        LocalizationService.GetString("DocumentSentToPrinter"),
                        LocalizationService.GetString("PrintComplete"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    this.Close();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("FailedToPrint")}\n\n{ex.Message}",
                    LocalizationService.GetString("PrintError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
