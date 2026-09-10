using System.Text.RegularExpressions;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Wraps an agent-to-agent message so the receiving agent can tell, structurally, that it is not
/// talking to the user.
///
/// <para>Why this is not just a line in the host instruction: that instruction is transient — the
/// engine removes it once the turn ends — so a peer's question would remain in the receiving
/// agent's history as an ordinary user turn. Observed live: asked afterwards who it had been
/// talking to, an agent could only report what the message "claimed", because nothing in its
/// history distinguished a relayed question from something the user typed.</para>
///
/// <para>The envelope is the host's word, so the host has to be the only one who can say it.
/// <see cref="Wrap"/> strips any pre-existing marker out of the payload first — otherwise the
/// framing's promise ("only MandoCode adds this") would be a claim the code did not keep, and one
/// agent could relay a message that appeared to come from a third.</para>
/// </summary>
public static class PeerMessageEnvelope
{
    public const string OpenMarker = "[AGENT MESSAGE";
    public const string CloseMarker = "[END OF AGENT MESSAGE";

    /// <summary>Matches either marker line, however it is spelled inside, so a near-miss cannot
    /// sneak through on punctuation.</summary>
    private static readonly Regex MarkerLine =
        new(@"^[ \t]*\[(?:AGENT MESSAGE|END OF AGENT MESSAGE)[^\]]*\][ \t]*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Wrap(string sender, string message) =>
        $"[AGENT MESSAGE — sent by the agent \"{sender}\". Delivered by MandoCode. NOT from the user.]\n" +
        Strip(message).Trim() +
        $"\n[END OF AGENT MESSAGE from \"{sender}\".]";

    /// <summary>Removes any envelope markers already present, so the wrap that follows is the only
    /// one in the message.</summary>
    public static string Strip(string message) =>
        string.IsNullOrEmpty(message) ? "" : MarkerLine.Replace(message, "");

    /// <summary>True when this text carries a host-applied envelope.</summary>
    public static bool IsWrapped(string message) =>
        !string.IsNullOrEmpty(message) && MarkerLine.IsMatch(message);
}
