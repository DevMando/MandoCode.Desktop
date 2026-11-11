using MandoCode.Desktop.Services;
using MandoCode.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace MandoCode.Desktop;

public sealed partial class ChatTabView
{
    private readonly List<PromptImageAttachment> _draftImages = new();
    private readonly Dictionary<PromptImageAttachment, BitmapImage> _imagePreviews = new();
    private bool _readingImageFiles;
    private bool _imageLoading, _submittingInput;
    private int _imageReadVersion;

    private void ImageNotice(string message)
    {
        if (_shutDown) return;
        ImageAttachmentArea.Visibility = !string.IsNullOrEmpty(message) || _draftImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PromptImageNotice.Text = message;
        PromptImageNotice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshImageAttachment()
    {
        ImageAttachmentArea.Visibility = _draftImages.Count > 0 || !string.IsNullOrEmpty(PromptImageNotice.Text) ? Visibility.Visible : Visibility.Collapsed;
        PromptImagesScroll.Visibility = PromptImageSummary.Visibility = _draftImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachImageButton.IsEnabled = !_imageLoading && !_readingImageFiles && !_submittingInput && !_shutDown && _draftImages.Count < PromptImageAttachment.MaxImages;
        PromptImageSummary.Text = $"{_draftImages.Count} / {PromptImageAttachment.MaxImages} images · {_draftImages.Sum(i => i.Bytes.Length) / 1048576d:F1} MiB · sends with your next message";
        PromptImageCards.Children.Clear();
        for (var index = 0; index < _draftImages.Count; index++)
        {
            var image = _draftImages[index];
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var preview = new Image { Width = 56, Height = 48, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Source = _imagePreviews[image] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, $"Attached image {index + 1}: {image.Name}");
            grid.Children.Add(preview);
            var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock { Text = $"{index + 1}. {image.Name}", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
            details.Children.Add(new TextBlock { Text = $"{image.Bytes.Length / 1024d:F0} KiB", FontSize = 11, Opacity = .7 });
            ToolTipService.SetToolTip(details, image.Name);
            Grid.SetColumn(details, 1);
            grid.Children.Add(details);
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 10 },
                Padding = new Thickness(4), MinWidth = 24, VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !_submittingInput,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(remove, $"Remove {image.Name}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove image {index + 1}: {image.Name}");
            remove.Click += (_, _) => RemoveImage(image);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
            PromptImageCards.Children.Add(new Border
            {
                Width = 240, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MandoPanelBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MandoBorderBrush"], Child = grid
            });
        }
    }

    private bool CanReadImage(bool fromBatch = false)
    {
        if (_shutDown || _imageLoading || _submittingInput || (_readingImageFiles && !fromBatch)) return false;
        if (_draftImages.Count >= PromptImageAttachment.MaxImages)
        {
            ImageNotice("Up to four images per message. Remove an image before attaching another.");
            return false;
        }
        if (_controller.ModelError || _controller.VisionSupport != ModelVisionSupport.Supported)
        {
            ImageNotice("Choose a confirmed vision model before attaching an image.");
            return false;
        }
        return true;
    }

    private async void AttachImage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReadImage()) return;
        try
        {
            var picker = new FileOpenPicker { SettingsIdentifier = "PromptImage" };
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" })
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0 && !_shutDown) await AttachImageFilesAsync(files);
        }
        catch (Exception ex) { ImageNotice($"Could not open the image: {ex.Message}"); }
    }

    private async void InputBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        try
        {
            var data = Clipboard.GetContent();
            if (!data.Contains(StandardDataFormats.Bitmap) && !data.Contains(StandardDataFormats.StorageItems))
                return; // Leave native text paste, including multiline prompts, alone.
            e.Handled = true;
            if (!CanReadImage()) return;
            if (data.Contains(StandardDataFormats.Bitmap))
            {
                await LoadImageAsync(async () =>
                {
                    var reference = await data.GetBitmapAsync();
                    using var source = await reference.OpenReadAsync();
                    if (source.Size > 64 * 1024 * 1024) throw new InvalidDataException("The clipboard image is too large.");
                    var decoder = await BitmapDecoder.CreateAsync(source);
                    CheckImageDimensions(decoder);
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    using var encoded = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                    return PromptImageAttachment.Create("Pasted image.png", await ReadImageBytesAsync(encoded));
                });
            }
            else
            {
                var files = (await data.GetStorageItemsAsync()).OfType<StorageFile>().ToArray();
                if (files.Length == 0 || files.Any(file => !MandoCode.Services.ImageFileReference.IsImage(file.Name)))
                    ImageNotice("Paste PNG, JPEG, GIF, or WebP image files, or use Attach image.");
                else await AttachImageFilesAsync(files);
            }
        }
        catch (Exception ex) { ImageNotice($"Could not paste the image: {ex.Message}"); }
    }

    private Task AttachImageFileAsync(StorageFile file, bool fromBatch = false) => LoadImageAsync(async () =>
    {
        using var source = await file.OpenReadAsync();
        return PromptImageAttachment.Create(file.Name, await ReadImageBytesAsync(source));
    }, fromBatch);

    private async Task AttachImageFilesAsync(IEnumerable<StorageFile> selected)
    {
        if (!CanReadImage()) return;
        var files = selected.ToArray();
        if (files.Length + _draftImages.Count > PromptImageAttachment.MaxImages)
        {
            ImageNotice($"You can add {PromptImageAttachment.MaxImages - _draftImages.Count} more image(s). Select fewer files.");
            return;
        }
        _readingImageFiles = true;
        RefreshImageAttachment();
        var errors = new List<string>();
        try
        {
            foreach (var file in files)
            {
                if (_shutDown) break;
                var before = _draftImages.Count;
                await AttachImageFileAsync(file, fromBatch: true);
                if (_draftImages.Count == before && !_shutDown) errors.Add($"{file.Name}: {PromptImageNotice.Text}");
            }
        }
        finally
        {
            _readingImageFiles = false;
            if (!_shutDown) { ImageNotice(string.Join("\n", errors)); RefreshImageAttachment(); }
        }
    }

    private static async Task<byte[]> ReadImageBytesAsync(IRandomAccessStream stream)
    {
        if (stream.Size == 0 || stream.Size > PromptImageAttachment.MaxBytes)
            throw new InvalidDataException("Use an image no larger than 4 MiB.");
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = checked((uint)stream.Size);
        if (await reader.LoadAsync(size) != size) throw new IOException("The image could not be read completely.");
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static void CheckImageDimensions(BitmapDecoder decoder)
    {
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0
            || (ulong)decoder.PixelWidth * decoder.PixelHeight > PromptImageAttachment.MaxPixels)
            throw new InvalidDataException("Use an image with at most 16 million pixels.");
    }

    private async Task LoadImageAsync(Func<Task<PromptImageAttachment>> read, bool fromBatch = false)
    {
        if (!CanReadImage(fromBatch)) return;
        _imageLoading = true;
        var version = ++_imageReadVersion;
        RefreshImageAttachment();
        ImageNotice("Reading image…");
        try
        {
            var image = await read();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(image.Bytes.ToArray());
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            CheckImageDimensions(decoder);
            stream.Seek(0);
            var preview = new BitmapImage { DecodePixelWidth = 96 };
            await preview.SetSourceAsync(stream);
            if (_shutDown || version != _imageReadVersion) return;
            if (_controller.VisionSupport != ModelVisionSupport.Supported)
                throw new InvalidOperationException("The selected model changed. Choose a vision model and attach the image again.");
            _draftImages.Add(image);
            _imagePreviews[image] = preview;
            ImageNotice("");
        }
        catch (Exception ex) { if (version == _imageReadVersion) ImageNotice($"Image not attached: {ex.Message}"); }
        finally
        {
            if (!_shutDown && version == _imageReadVersion)
            {
                _imageLoading = false;
                RefreshImageAttachment();
            }
        }
    }

    private void RemoveImage(PromptImageAttachment image)
    {
        if (_submittingInput) return;
        _draftImages.Remove(image);
        _imagePreviews.Remove(image);
        ImageNotice("");
        RefreshImageAttachment();
    }
}
