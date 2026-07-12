using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Microsoft.Win32;

namespace Elysium_Cast_IPTV;

/// <summary>
/// Modal tag editor. Collects an <see cref="AudioTagEdit"/> without touching the
/// file itself — the caller performs the write so it can release the player first.
/// </summary>
public partial class MetadataEditorWindow : Window
{
    private byte[]? _newCoverBytes;
    private string? _newCoverMime;
    private bool _removeCover;

    public AudioTagEdit? Result { get; private set; }

    public MetadataEditorWindow(string path, AudioMetadata metadata)
    {
        InitializeComponent();
        FileNameText.Text = path;
        TitleBox.Text = metadata.Title;
        ArtistBox.Text = metadata.Artist ?? "";
        AlbumBox.Text = metadata.Album ?? "";
        AlbumArtistBox.Text = metadata.AlbumArtist ?? "";
        GenreBox.Text = metadata.Genre ?? "";
        TrackBox.Text = metadata.TrackNumber > 0 ? metadata.TrackNumber.ToString() : "";
        DiscBox.Text = metadata.DiscNumber > 0 ? metadata.DiscNumber.ToString() : "";
        if (metadata.CoverBytes is { Length: > 0 } bytes)
            CoverImage.Source = CoverArtCache.DecodeBytes(bytes, 400);
        RemoveCoverBtn.IsEnabled = CoverImage.Source != null;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; return; }
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            Save_Click(sender, e);
    }

    private void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir une pochette",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            var preview = CoverArtCache.DecodeBytes(bytes, 400);
            if (preview == null)
            {
                MessageBox.Show(this, "Cette image est illisible.", "Métadonnées", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            using var sourceStream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = sourceStream;
            bitmap.EndInit();
            bitmap.Freeze();
            using var jpeg = new MemoryStream();
            var frame = BitmapFrame.Create(bitmap);
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(frame);
            encoder.Save(jpeg);
            _newCoverBytes = jpeg.ToArray();
            _newCoverMime = "image/jpeg";
            _removeCover = false;
            CoverImage.Source = preview;
            RemoveCoverBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("Lecture de l'image de pochette impossible", ex);
            MessageBox.Show(this, "Impossible de lire cette image.", "Métadonnées", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveCover_Click(object sender, RoutedEventArgs e)
    {
        _newCoverBytes = null;
        _newCoverMime = null;
        _removeCover = true;
        CoverImage.Source = null;
        RemoveCoverBtn.IsEnabled = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show(this, "Le titre ne peut pas être vide.", "Métadonnées", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        uint.TryParse(TrackBox.Text.Trim(), out var track);
        uint.TryParse(DiscBox.Text.Trim(), out var disc);
        Result = new AudioTagEdit(
            TitleBox.Text,
            ArtistBox.Text,
            AlbumBox.Text,
            AlbumArtistBox.Text,
            GenreBox.Text,
            track,
            disc,
            _newCoverBytes,
            _newCoverMime,
            _removeCover);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
