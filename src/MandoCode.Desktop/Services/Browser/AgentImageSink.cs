using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Bridges captured preview images to this agent's model. Capability is asked before capture so
/// a text-only model is told plainly it cannot see, rather than being handed bytes it drops.
/// </summary>
public sealed class AgentImageSink(IAiService ai) : IAgentImageSink
{
    public string? Unavailable => ai.VisionSupport switch
    {
        ModelVisionSupport.Supported => null,
        ModelVisionSupport.Unsupported =>
            "This model is text-only, so a screenshot cannot be examined. Use inspect or observe, " +
            "and say that visual layout could not be checked.",
        _ => "This model's image support is unknown, so a screenshot cannot be examined. Use inspect " +
            "or observe, and say that visual layout could not be checked.",
    };

    public bool TryAttach(ReadOnlyMemory<byte> bytes, string mediaType, string caption, out string error) =>
        ai.TryAttachImage(bytes, mediaType, caption, out error);
}
