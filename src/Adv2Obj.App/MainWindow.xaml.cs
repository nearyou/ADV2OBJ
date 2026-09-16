using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Adv2Obj.Core;
using Microsoft.Win32;

namespace Adv2Obj.App;

public partial class MainWindow : Window
{
    private readonly AdvToObjConverter _converter = new();
    private CancellationTokenSource? _conversionCancellation;
    public ObservableCollection<FileConversionItem> Files { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ChooseFolder("Select the folder containing ADV files", InputFolderTextBox.Text);
        if (folder is null) return;
        InputFolderTextBox.Text = folder;
        RefreshFileList();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ChooseFolder("Select the parent folder for converted stone folders", OutputFolderTextBox.Text);
        if (folder is null) return;
        OutputFolderTextBox.Text = folder;
        RefreshFileList();
    }

    private static string? ChooseFolder(string title, string currentFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(currentFolder)) dialog.InitialDirectory = currentFolder;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void RefreshFileList()
    {
        Files.Clear();
        if (!Directory.Exists(InputFolderTextBox.Text))
        {
            SummaryText.Text = "Choose an input folder to scan for ADV files.";
            return;
        }

        string outputFolder = OutputFolderTextBox.Text;
        foreach (string path in Directory.EnumerateFiles(InputFolderTextBox.Text, "*.adv")
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            Files.Add(new FileConversionItem
            {
                FileName = Path.GetFileName(path),
                InputPath = path,
                OutputDirectory = string.IsNullOrWhiteSpace(outputFolder)
                    ? string.Empty
                    : Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(path))
            });
        }
        SummaryText.Text = Files.Count == 0
            ? "No ADV files were found in the selected input folder."
            : $"{Files.Count:N0} ADV file(s) ready.";
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(InputFolderTextBox.Text))
        {
            MessageBox.Show(this, "Please select a valid input folder.", "Input folder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputFolderTextBox.Text))
        {
            MessageBox.Show(this, "Please select an output folder.", "Output folder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string outputFolder = OutputFolderTextBox.Text;
        Directory.CreateDirectory(outputFolder);
        RefreshFileList();
        if (Files.Count == 0) return;

        ConvertButton.IsEnabled = false;
        ClearButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        InputControls.IsEnabled = false;
        OutputControls.IsEnabled = false;
        _conversionCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _conversionCancellation.Token;
        ConversionProgress.Maximum = Files.Count;
        ConversionProgress.Value = 0;
        int succeeded = 0;
        int failed = 0;

        try
        {
            foreach (FileConversionItem item in Files)
            {
                item.Status = "Converting";
                item.Details = string.Empty;
                try
                {
                    string inputPath = item.InputPath;
                    ConversionResult result = await Task.Run(
                        () => _converter.ConvertAsync(inputPath, outputFolder, cancellationToken),
                        cancellationToken);
                    succeeded++;
                    item.Status = result.Warnings.Count == 0 ? "Completed" : "Review";
                    item.Details = $"{result.ObjectFileCount:N0} OBJ + CSV + INI files";
                    if (result.RepairedVertexCount > 0)
                    {
                        item.Details += $"; {result.RepairedVertexCount:N0} repaired";
                    }
                    if (result.Warnings.Count > 0)
                    {
                        item.Details += "; " + string.Join(" ", result.Warnings);
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    item.Details = "Conversion cancelled; existing output was preserved.";
                    break;
                }
                catch (Exception exception)
                {
                    failed++;
                    item.Status = "Failed";
                    item.Details = $"{exception.GetType().Name}: {exception.Message}";
                }
                ConversionProgress.Value++;
                SummaryText.Text = $"Converted {succeeded:N0}; failed {failed:N0}; "
                                   + $"processed {ConversionProgress.Value:N0} of {Files.Count:N0}.";
            }
        }
        finally
        {
            _conversionCancellation?.Dispose();
            _conversionCancellation = null;
            ConvertButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            InputControls.IsEnabled = true;
            OutputControls.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        SummaryText.Text = "Cancelling after the current operation…";
        _conversionCancellation?.Cancel();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Files.Clear();
        ConversionProgress.Maximum = 1;
        ConversionProgress.Value = 0;
        SummaryText.Text = "History cleared. Click Convert to scan the selected input folder again.";
    }
}
