namespace MandoCode.Desktop.Services;

/// <summary>
/// A captured "history point": the conversation as it stood the instant the user switched an
/// agent's model. Switching a model clears the live context (a different model mid-history is a
/// different conversation), so this is the salvaged copy — the user can re-import it into another
/// model later, or delete it.
///
/// Pure data, no UI types, because it is created on a background thread during the switch.
/// <see cref="LightRecap"/> is the free deterministic summary captured immediately;
/// <see cref="RawHistory"/> is the full serialized transcript, kept so a richer LLM summary can be
/// generated on demand later (<see cref="AiRecap"/>) without needing the original conversation to
/// still be alive.
/// </summary>
public sealed class ContextSnapshot
{
    public required int Id { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>The model whose conversation this summarizes.</summary>
    public required string OriginModel { get; init; }

    /// <summary>The model the user switched TO (context for why the snapshot exists).</summary>
    public required string SwitchedToModel { get; init; }

    /// <summary>Deterministic recap (ported from the harness). Always present, capped small.</summary>
    public required string LightRecap { get; init; }

    /// <summary>Full serialized history, kept so an AI summary can be generated later.</summary>
    public required string RawHistory { get; init; }

    /// <summary>Conversation length (messages, excluding the system prompt).</summary>
    public required int MessageCount { get; init; }

    /// <summary>LLM-generated recap, once "Enhance" has run. Null until then.</summary>
    public string? AiRecap { get; set; }

    /// <summary>"Light" until enhanced, then "AI".</summary>
    public string Tag => AiRecap != null ? "AI" : "Light";

    /// <summary>The recap to re-import — the richer AI one if it exists, else the light one.</summary>
    public string BestRecap => AiRecap ?? LightRecap;

    // ---- display helpers for the snapshots flyout ----
    public string TimeLabel => CapturedAt.LocalDateTime.ToString("MMM d · h:mm tt");
}
