using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Adv2Obj.Core;
using Microsoft.Win32;

namespace Adv2Obj.App;

public partial class MainWindow : Window
{
    private readonly AdvToObjConverter _converter = new();
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
        string? folder = ChooseFolder("Select the folder for OBJ files", OutputFolderTextBox.Text);
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
                OutputPath = string.IsNullOrWhiteSpace(outputFolder)
                    ? string.Empty
                    : Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(path) + ".obj")
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

        Directory.CreateDirectory(OutputFolderTextBox.Text);
        RefreshFileList();
        if (Files.Count == 0) return;

        ConvertButton.IsEnabled = false;
        ConversionProgress.Maximum = Files.Count;
        ConversionProgress.Value = 0;
        int succeeded = 0;
        int failed = 0;

        foreach (FileConversionItem item in Files)
        {
            item.Status = "Converting";
            item.Details = string.Empty;
            try
            {
                ConversionResult result = await _converter.ConvertAsync(item.InputPath, item.OutputPath);
                succeeded++;
                item.Status = result.RepairedVertexCount == 0 ? "Completed" : "Completed*";
                item.Details = result.RepairedVertexCount == 0
                    ? $"{result.VertexCount:N0} vertices, {result.FaceCount:N0} faces"
                    : $"{result.VertexCount:N0} vertices, {result.FaceCount:N0} faces; "
                      + $"{result.RepairedVertexCount:N0} repaired";
            }
            catch (Exception exception) when (exception is AdvFormatException or IOException or UnauthorizedAccessException)
            {
                failed++;
                item.Status = "Failed";
                item.Details = exception.Message;
            }
            ConversionProgress.Value++;
            SummaryText.Text = $"Converted {succeeded:N0}; failed {failed:N0}; "
                               + $"processed {ConversionProgress.Value:N0} of {Files.Count:N0}.";
        }

        ConvertButton.IsEnabled = true;
    }
}
