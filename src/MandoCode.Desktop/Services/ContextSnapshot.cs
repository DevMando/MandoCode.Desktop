namespace MandoCode.Desktop.Services;

/// <summary>
/// A captured "history point": an LLM-written recap of a conversation, saved on demand. Switching an
/// agent's model clears the live context (a different model mid-history is a different conversation);
/// rather than auto-salvaging a deterministic dump, the user is offered the chance to snapshot that
/// conversation — summarized by a model of their choice. A snapshot is therefore always born with a
/// real <see cref="Recap"/>; there is no "light"/"un-enhanced" state.
///
/// Pure data, no UI types — it is created on a background thread while the summary is generated.
/// </summary>
public sealed class ContextSnapshot
{
    public required int Id { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>The model whose conversation this recaps.</summary>
    public required string OriginModel { get; init; }

    /// <summary>The model that generated <see cref="Recap"/> (may differ from the origin — the user
    /// can summarize with a lighter/cheaper local model, or a stronger one).</summary>
    public required string SummarizerModel { get; init; }

    /// <summary>The LLM-generated recap — the whole point of the snapshot, and what Import carries.</summary>
    public required string Recap { get; init; }

    /// <summary>Optional user-given name. Null/empty when the user didn't name it (then the card
    /// falls back to the origin model as its title).</summary>
    public string? Name { get; init; }

    /// <summary>Conversation length (messages, excluding the system prompt).</summary>
    public required int MessageCount { get; init; }

    /// <summary>Project folder the conversation happened in, when known — lets the panel
    /// group snapshots by project now that they survive across launches.</summary>
    public string? ProjectRoot { get; init; }

    // ---- display helpers for the snapshots panel ----

    /// <summary>Card title: the user's name if given, else the model that had the conversation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? OriginModel : Name!;

    [System.Text.Json.Serialization.JsonIgnore]
    public string TimeLabel => CapturedAt.LocalDateTime.ToString("MMM d · h:mm tt");

    /// <summary>Group heading for the panel: the project folder's leaf name, or a stand-in when the
    /// snapshot predates project tracking (older files) or was taken outside any folder.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectRoot)) return "Unknown project";
            var name = System.IO.Path.GetFileName(
                ProjectRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? ProjectRoot! : name;
        }
    }
}
