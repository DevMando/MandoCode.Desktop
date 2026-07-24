namespace MandoCode.Desktop.Services;

/// <summary>
/// The transcript-fragment methods the request loop emits. <see cref="TranscriptHtmlBuilder"/>
/// implements it; the streaming loop (<see cref="ViewModels.ResponseStreamer"/>) depends on this
/// slice rather than the concrete builder, whose <c>BaseDocument</c> reaches into WinUI-only
/// <c>ThemeManager</c> — so the loop (and its tests) stay free of the Windows App SDK.
/// </summary>
public interface ITranscriptHtml
{
    string AssistantCard(string markdown);
    string Warn(string text);
    string Error(string text);
    string Dim(string text);
    string TokenSummary(string text);
}
