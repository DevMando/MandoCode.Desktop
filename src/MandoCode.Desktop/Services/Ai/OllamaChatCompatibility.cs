namespace MandoCode.Desktop.Services;

/// <summary>Distinguishes a provider endpoint mismatch from connection and image errors.</summary>
public static class OllamaChatCompatibility
{
    private static bool Matches(string text) =>
        text.Contains("is a decision model", StringComparison.OrdinalIgnoreCase)
        && text.Contains("/v1/systemone", StringComparison.OrdinalIgnoreCase);

    public static bool IsDecisionModelError(Exception? error)
    {
        while (error != null)
        {
            if (Matches(error.Message)) return true;
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsDecisionModelError)) return true;
            error = error.InnerException;
        }
        return false;
    }

    // The engine may format a provider exception into a response instead of rethrowing.
    // Require an error prefix so ordinary discussion of decision models stays normal text.
    public static bool IsDecisionModelResponse(string response)
    {
        var text = response.TrimStart();
        return (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Error communicating with AI:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<red>Error:", StringComparison.OrdinalIgnoreCase)) && Matches(text);
    }

    public static string Message(string model) =>
        $"The model '{model}' is a decision model and cannot be used for this chat workflow. " +
        "It requires a different API that MandoCode chat does not currently use. " +
        "Run /model to choose a chat model, and choose one with vision support for image attachments.";
}
