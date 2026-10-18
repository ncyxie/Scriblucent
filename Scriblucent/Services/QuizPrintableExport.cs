using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Xps.Packaging;
using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

internal static class QuizPrintableExport
{
    internal static FlowDocument Build(QuizDocument snapshot, double captionSize, double titleSize, double headingSize)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = captionSize,
            PagePadding = new Thickness(48),
            ColumnWidth = 700,
            Background = Brushes.White,
            Foreground = Brushes.Black
        };

        document.Blocks.Add(new Paragraph(new Run(snapshot.Title))
        {
            FontSize = titleSize,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16)
        });

        int number = 0;
        foreach (var question in snapshot.Questions)
        {
            document.Blocks.Add(new Paragraph(new Run(string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizQuestionHeaderFormat"), ++number, question.Type switch
            {
                QuizQuestionType.TrueFalse => LocalizationService.GetString("QuizTypeTrueFalse"),
                QuizQuestionType.MultipleChoice => LocalizationService.GetString("QuizTypeMultipleChoice"),
                QuizQuestionType.PlainText => LocalizationService.GetString("QuizTypePlainText"),
                _ => LocalizationService.GetString("QuizTypeSingleChoice")
            })))
            {
                FontSize = headingSize,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6)
            });

            document.Blocks.Add(new Paragraph(new Run(question.Question))
            {
                Margin = new Thickness(0, 0, 0, 6)
            });

            if (question.Type == QuizQuestionType.PlainText)
            {
                document.Blocks.Add(new Paragraph(new Run(string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizCorrectAnswerFormat"), question.PlainTextAnswer)))
                {
                    Margin = new Thickness(12, 0, 0, 2)
                });
            }

            foreach (var option in question.Options.Where(option => !string.IsNullOrWhiteSpace(option.Text)))
            {
                var marker = option.IsCorrect ? "✓" : "•";
                document.Blocks.Add(new Paragraph(new Run($"{marker} {option.Text}"))
                {
                    Margin = new Thickness(12, 0, 0, 2)
                });
            }

            if (!string.IsNullOrWhiteSpace(question.Explanation))
            {
                document.Blocks.Add(new Paragraph(new Run(string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizExplanationFormat"), question.Explanation)))
                {
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        return document;
    }
    internal static Task SaveAsync(QuizDocument snapshot, string path, CancellationToken token)
    {
        // Resolve theme tokens on the caller's UI dispatcher. Pagination/file I/O owns its own STA.
        double caption = TypeScale.Px(TypeScale.Caption, 12), title = TypeScale.Px(TypeScale.Display, 24), heading = TypeScale.Px(TypeScale.BlockTitle, 16);
        var culture = CultureInfo.CurrentCulture; var uiCulture = CultureInfo.CurrentUICulture;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture;
                token.ThrowIfCancellationRequested(); var document = Build(snapshot, caption, title, heading);
                using (var output = new XpsDocument(path, FileAccess.Write))
                    XpsDocument.CreateXpsDocumentWriter(output).Write(((IDocumentPaginatorSource)document).DocumentPaginator);
                token.ThrowIfCancellationRequested(); completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception error) { completion.SetException(error); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "Selected quiz printable export" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
